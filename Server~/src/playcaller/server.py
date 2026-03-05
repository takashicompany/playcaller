"""playcaller – Unity MCP server."""

from __future__ import annotations

import asyncio
import json
import os
import struct
import sys
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any, AsyncIterator

from mcp.server.fastmcp import FastMCP

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
LISTEN_HOST = "127.0.0.1"
PORT_FILE_NAME = "Playcaller.port"
COMMAND_TIMEOUT_S = 30
MAX_MESSAGE_SIZE = 1024 * 1024  # 1 MB
UNITY_CONNECT_WAIT_S = 15  # send_command で Unity 接続を待つ最大秒数

# ---------------------------------------------------------------------------
# UnityServer – Python が TCP サーバー、Unity が接続してくる
# ---------------------------------------------------------------------------

class UnityServer:
    """TCP server that Unity connects to."""

    def __init__(self) -> None:
        self._server: asyncio.Server | None = None
        self._reader: asyncio.StreamReader | None = None
        self._writer: asyncio.StreamWriter | None = None
        self._connected = False
        self._command_id = 0
        self._pending: dict[str, asyncio.Future[Any]] = {}
        self._recv_task: asyncio.Task[None] | None = None
        self._connection_event = asyncio.Event()

        # Last screenshot / screen dimensions for coordinate conversion
        self.last_screenshot_width = 0
        self.last_screenshot_height = 0
        self.last_screen_width = 0
        self.last_screen_height = 0

    # -- public API ---------------------------------------------------------

    @property
    def connected(self) -> bool:
        return self._connected

    async def start(self) -> None:
        """Start the TCP server and write port file."""
        self._server = await asyncio.start_server(
            self._handle_connection, LISTEN_HOST, 0
        )
        port = self._server.sockets[0].getsockname()[1]
        _write_port_file(port)
        _log("TCP server listening on %s:%d", LISTEN_HOST, port)

    def stop(self) -> None:
        """Stop the server and clean up."""
        if self._recv_task and not self._recv_task.done():
            self._recv_task.cancel()
        if self._writer:
            try:
                self._writer.close()
            except Exception:
                pass
        if self._server:
            self._server.close()
        self._reader = None
        self._writer = None
        self._connected = False
        self._connection_event.clear()
        # Reject pending commands
        for fut in self._pending.values():
            if not fut.done():
                fut.set_exception(ConnectionError("Server stopped"))
        self._pending.clear()
        _delete_port_file()

    async def send_command(self, cmd_type: str, params: dict[str, Any] | None = None) -> Any:
        """Send a command and wait for the matching response."""
        if not self._connected:
            # Unity 未接続 → 接続を待つ
            _log("Unity not connected, waiting up to %ds...", UNITY_CONNECT_WAIT_S)
            self._connection_event.clear()
            try:
                await asyncio.wait_for(
                    self._connection_event.wait(), timeout=UNITY_CONNECT_WAIT_S
                )
            except asyncio.TimeoutError:
                raise ConnectionError(
                    f"Unity not connected (waited {UNITY_CONNECT_WAIT_S}s)"
                )

        self._command_id += 1
        cmd_id = str(self._command_id)
        payload = json.dumps(
            {"id": cmd_id, "type": cmd_type, "params": params or {}}
        ).encode()

        frame = struct.pack(">I", len(payload)) + payload

        loop = asyncio.get_running_loop()
        fut: asyncio.Future[Any] = loop.create_future()
        self._pending[cmd_id] = fut

        assert self._writer is not None
        self._writer.write(frame)
        await self._writer.drain()

        try:
            return await asyncio.wait_for(fut, timeout=COMMAND_TIMEOUT_S)
        except asyncio.TimeoutError:
            self._pending.pop(cmd_id, None)
            raise TimeoutError(
                f"Command {cmd_type} timed out after {COMMAND_TIMEOUT_S}s"
            )

    async def wait_for_reconnect(self, timeout_s: float) -> bool:
        """Wait for Unity to (re)connect after domain reload."""
        # まず切断を待つ（短い猶予）
        if self._connected:
            try:
                await asyncio.wait_for(self._wait_for_disconnect(), timeout=3)
            except asyncio.TimeoutError:
                # まだ接続中 = リロードが発生しなかった可能性
                pass

        if self._connected:
            # 切断されなかった → まだ繋がっている → OK
            return True

        # Unity の再接続を待つ
        self._connection_event.clear()
        try:
            await asyncio.wait_for(
                self._connection_event.wait(), timeout=timeout_s
            )
            # 再接続後に ping で確認
            await self.send_command("ping")
            return True
        except (asyncio.TimeoutError, Exception):
            return self._connected

    # -- connection handling -------------------------------------------------

    async def _handle_connection(
        self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter
    ) -> None:
        """Handle a new connection from Unity."""
        peer = writer.get_extra_info("peername")
        _log("Unity connected from %s", peer)

        # 既存接続があれば切断（1接続のみ許可）
        if self._writer:
            try:
                self._writer.close()
            except Exception:
                pass
        if self._recv_task and not self._recv_task.done():
            self._recv_task.cancel()
            try:
                await self._recv_task
            except (asyncio.CancelledError, Exception):
                pass

        # 既存の pending をリジェクト（古いセッションの残り）
        for fut in self._pending.values():
            if not fut.done():
                fut.set_exception(ConnectionError("New connection replaced old"))
        self._pending.clear()

        self._reader = reader
        self._writer = writer
        self._connected = True
        self._connection_event.set()

        self._recv_task = asyncio.create_task(self._receive_loop())

    # -- receive loop -------------------------------------------------------

    async def _receive_loop(self) -> None:
        """Read length-prefixed JSON messages until the connection drops."""
        assert self._reader is not None
        try:
            while True:
                header = await self._reader.readexactly(4)
                (length,) = struct.unpack(">I", header)
                if length <= 0 or length > MAX_MESSAGE_SIZE:
                    _log("Invalid message length: %d", length)
                    continue
                data = await self._reader.readexactly(length)
                try:
                    response = json.loads(data.decode())
                    self._process_response(response)
                except Exception as exc:
                    _log("Failed to parse response: %s", exc)
        except (asyncio.IncompleteReadError, ConnectionError, OSError):
            pass
        except asyncio.CancelledError:
            return
        finally:
            self._on_disconnected()

    def _process_response(self, response: dict[str, Any]) -> None:
        resp_id = (
            str(response.get("id", ""))
            if response.get("id") is not None
            else None
        )

        # Match by ID, or fall back to first pending command
        target_id = None
        if resp_id and resp_id in self._pending:
            target_id = resp_id
        elif self._pending:
            target_id = next(iter(self._pending))

        if target_id is None:
            return

        fut = self._pending.pop(target_id)
        if fut.done():
            return

        status = response.get("status")
        if status == "success":
            fut.set_result(response.get("result", {}))
        elif status == "error":
            fut.set_exception(
                RuntimeError(response.get("error", "Command failed"))
            )
        else:
            fut.set_result(response)

    # -- disconnect handling ------------------------------------------------

    def _on_disconnected(self) -> None:
        _log("Unity disconnected")
        was_connected = self._connected
        self._connected = False
        self._connection_event.clear()
        if self._writer:
            try:
                self._writer.close()
            except Exception:
                pass
        self._reader = None
        self._writer = None
        # Reject pending commands
        if was_connected:
            for fut in self._pending.values():
                if not fut.done():
                    fut.set_exception(ConnectionError("Connection closed"))
            self._pending.clear()

    async def _wait_for_disconnect(self) -> None:
        """Wait until the connection drops."""
        while self._connected:
            await asyncio.sleep(0.1)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _log(fmt: str, *args: object) -> None:
    print(f"[playcaller] {fmt % args}", file=sys.stderr, flush=True)


def _find_unity_project_dir() -> Path:
    """Detect the Unity project root directory.

    1. UNITY_PROJECT_DIR environment variable (if set)
    2. Walk up from CWD looking for a directory containing both Assets/ and Packages/
    """
    env_dir = os.environ.get("UNITY_PROJECT_DIR")
    if env_dir:
        p = Path(env_dir).resolve()
        if (p / "Assets").is_dir() and (p / "Packages").is_dir():
            return p
        _log("UNITY_PROJECT_DIR=%s does not look like a Unity project, ignoring", env_dir)

    cur = Path.cwd().resolve()
    for d in [cur, *cur.parents]:
        if (d / "Assets").is_dir() and (d / "Packages").is_dir():
            _log("Auto-detected Unity project: %s", d)
            return d

    raise RuntimeError(
        "Could not find Unity project directory. "
        "Run from within the Unity project tree, or set UNITY_PROJECT_DIR."
    )


def _get_port_file_path() -> Path:
    return _find_unity_project_dir() / "Library" / "Playcaller" / PORT_FILE_NAME


def _write_port_file(port: int) -> None:
    """Write the TCP server port to the port file (Unity reads this)."""
    path = _get_port_file_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(str(port))
    _log("Port file written: %s (port %d)", path, port)


def _delete_port_file() -> None:
    """Delete the port file on server shutdown."""
    try:
        path = _get_port_file_path()
        if path.exists():
            path.unlink()
            _log("Port file deleted: %s", path)
    except Exception:
        pass


# ---------------------------------------------------------------------------
# FastMCP application
# ---------------------------------------------------------------------------

unity = UnityServer()


@asynccontextmanager
async def server_lifespan(app: FastMCP) -> AsyncIterator[None]:
    """Start TCP server on startup, clean up on shutdown."""
    await unity.start()
    try:
        yield None
    finally:
        unity.stop()


mcp = FastMCP("playcaller", lifespan=server_lifespan)


# -- screenshot -------------------------------------------------------------

@mcp.tool()
async def playcaller_screenshot(width: int = 0, height: int = 0, filename: str = "") -> str:
    """Capture a screenshot of the Unity Game View.

    Returns the file path of the saved PNG screenshot.
    width/height: optional resolution in pixels (default: Game View size).
    filename: optional save path (relative to project root, or absolute). Default: Temp/Playcaller/Screenshots/screenshot.png.
    """
    try:
        params: dict[str, Any] = {"width": width, "height": height}
        if filename:
            params["filename"] = filename
        result = await unity.send_command("screenshot", params)
        unity.last_screenshot_width = result["width"]
        unity.last_screenshot_height = result["height"]
        unity.last_screen_width = result.get("screenWidth", result["width"])
        unity.last_screen_height = result.get("screenHeight", result["height"])

        file_path = result["filePath"]
        return (
            f"Screenshot saved: {file_path}\n"
            f"Size: {result['width']}x{result['height']} "
            f"(screen: {unity.last_screen_width}x{unity.last_screen_height}).\n"
            f"Input coordinates for tap/drag/flick should use the screenshot image coordinate system "
            f"({result['width']}x{result['height']}, top-left origin, Y-down).\n"
            f"Use the Read tool to view the image file."
        )
    except Exception as exc:
        return f"Screenshot failed: {exc}"


# -- tap --------------------------------------------------------------------

@mcp.tool()
async def playcaller_tap(x: int, y: int, holdDurationMs: int = 0) -> str:
    """Tap/click at a specific screen coordinate.

    Coordinates use top-left origin with Y-axis pointing down (matching screenshot image coordinates).
    x: pixels from left, y: pixels from top.
    holdDurationMs: hold duration before releasing (default 0, max 10000).
    """
    try:
        result = await unity.send_command("tap", {
            "x": x, "y": y, "holdDurationMs": holdDurationMs,
            "screenshotWidth": unity.last_screenshot_width,
            "screenshotHeight": unity.last_screenshot_height,
            "screenWidth": unity.last_screen_width,
            "screenHeight": unity.last_screen_height,
        })
        if result.get("tapped"):
            return f'Tapped on "{result.get("targetName")}" at ({x}, {y})'
        return f"Tap at ({x}, {y}) - no target hit"
    except Exception as exc:
        return f"Tap failed: {exc}"


# -- multi_tap --------------------------------------------------------------

@mcp.tool()
async def playcaller_multi_tap(
    taps: list[dict],
    intervalMs: int = 50,
) -> str:
    """Execute multiple taps in rapid succession within a single MCP call.

    taps: list of {x, y} coordinate pairs (screenshot coordinates).
    intervalMs: delay between taps in milliseconds (default 50, 0-1000).
    """
    intervalMs = max(0, min(1000, intervalMs))
    results: list[str] = []
    for i, tap in enumerate(taps):
        x = tap.get("x", 0)
        y = tap.get("y", 0)
        hold = tap.get("holdDurationMs", 0)
        try:
            result = await unity.send_command("tap", {
                "x": x, "y": y, "holdDurationMs": hold,
                "screenshotWidth": unity.last_screenshot_width,
                "screenshotHeight": unity.last_screenshot_height,
                "screenWidth": unity.last_screen_width,
                "screenHeight": unity.last_screen_height,
            })
            if result.get("tapped"):
                results.append(f'[{i}] Tapped "{result.get("targetName")}" at ({x}, {y})')
            else:
                results.append(f"[{i}] Tap at ({x}, {y}) - no target hit")
        except Exception as exc:
            results.append(f"[{i}] Tap at ({x}, {y}) failed: {exc}")
        if intervalMs > 0 and i < len(taps) - 1:
            await asyncio.sleep(intervalMs / 1000.0)
    return "\n".join(results)


# -- drag -------------------------------------------------------------------

@mcp.tool()
async def playcaller_drag(
    fromX: int, fromY: int, toX: int, toY: int,
    durationMs: int = 300, steps: int = 10,
) -> str:
    """Drag from one screen coordinate to another.

    Coordinates use top-left origin, Y-down (matching screenshot).
    fromX/fromY: start, toX/toY: end.
    durationMs: drag duration (default 300, max 10000).
    steps: intermediate points (default 10, 2-100).
    """
    try:
        result = await unity.send_command("drag", {
            "fromX": fromX, "fromY": fromY, "toX": toX, "toY": toY,
            "durationMs": durationMs, "steps": steps,
            "screenshotWidth": unity.last_screenshot_width,
            "screenshotHeight": unity.last_screenshot_height,
            "screenWidth": unity.last_screen_width,
            "screenHeight": unity.last_screen_height,
        })
        if result.get("dragged"):
            return f'Dragged "{result.get("targetName")}" from ({fromX},{fromY}) to ({toX},{toY}) in {result.get("steps")} steps'
        return f"Drag from ({fromX},{fromY}) to ({toX},{toY}) - no target hit"
    except Exception as exc:
        return f"Drag failed: {exc}"


# -- flick ------------------------------------------------------------------

@mcp.tool()
async def playcaller_flick(
    fromX: int, fromY: int, dx: int, dy: int,
    durationMs: int = 150,
) -> str:
    """Flick (quick swipe) from a coordinate in a direction.

    Coordinates use top-left origin, Y-down.
    fromX/fromY: start point.
    dx: horizontal displacement (positive=right). dy: vertical displacement (positive=down).
    durationMs: flick duration (default 150, max 5000).
    """
    try:
        result = await unity.send_command("flick", {
            "fromX": fromX, "fromY": fromY, "dx": dx, "dy": dy,
            "durationMs": durationMs,
            "screenshotWidth": unity.last_screenshot_width,
            "screenshotHeight": unity.last_screenshot_height,
            "screenWidth": unity.last_screen_width,
            "screenHeight": unity.last_screen_height,
        })
        if result.get("dragged"):
            return f'Flicked "{result.get("targetName")}" from ({fromX},{fromY}) by ({dx},{dy})'
        return f"Flick from ({fromX},{fromY}) by ({dx},{dy}) - no target hit"
    except Exception as exc:
        return f"Flick failed: {exc}"


# -- playmode ---------------------------------------------------------------

@mcp.tool()
async def playcaller_playmode(action: str) -> str:
    """Control Unity Play Mode.

    action: "play" to start, "pause" to toggle pause, "stop" to stop, "get_state" to check current state.
    Play/stop will wait for Unity domain reload to complete before returning.
    """
    if not unity.connected:
        return f"ERROR: Unity is not connected. The {action} command was NOT sent."

    try:
        result = await unity.send_command("playmode", {"action": action})
        text = json.dumps(result, indent=2)

        if action in ("play", "stop"):
            reconnected = await unity.wait_for_reconnect(15)
            if not reconnected:
                return (
                    text + "\n\nCommand was sent and Unity responded, "
                    "but TCP reconnection after domain reload timed out. "
                    "The play/stop action itself succeeded."
                )
        return text
    except Exception as exc:
        if action in ("play", "stop"):
            _log("%s command error: %s", action, exc)
            reconnected = await unity.wait_for_reconnect(15)
            if reconnected:
                return f"Play Mode {'started' if action == 'play' else 'stopped'} (reconnected after domain reload)"
            return (
                f"ERROR: send_command('{action}') raised an exception: {exc}\n"
                f"UNKNOWN: Whether the {action} command reached Unity.\n"
                f"FACT: Reconnection failed after 15s."
            )
        return f"PlayMode command failed: {exc}"


# -- console_log ------------------------------------------------------------

@mcp.tool()
async def playcaller_console_log(
    count: int = 50, level: str = "all", clear: bool = False,
) -> str:
    """Get Unity Console log messages.

    count: max entries (default 50, max 200).
    level: filter (all/log/warning/error).
    clear: clear after retrieval (default false).
    """
    try:
        result = await unity.send_command("console_log", {
            "count": count, "level": level, "clear": clear,
        })
        logs = result.get("logs", [])
        total = result.get("totalCount", 0)
        lines = [f"Console Logs ({len(logs)}/{total}):"]
        for log in logs:
            lvl = log.get("level", "log")
            prefix = "[ERR]" if lvl == "error" else "[WRN]" if lvl == "warning" else "[LOG]"
            lines.append(f"{prefix} {log.get('message', '')}")
        return "\n".join(lines)
    except Exception as exc:
        return f"Console log failed: {exc}"


# -- wait -------------------------------------------------------------------

@mcp.tool()
async def playcaller_wait(ms: int = 0, frames: int = 0) -> str:
    """Wait for a specified duration.

    ms: milliseconds to wait (max 30000).
    frames: Unity frame count to wait (max 600).
    Specify either ms or frames.
    """
    if ms == 0 and frames == 0:
        return "Either ms or frames must be specified"
    try:
        result = await unity.send_command("wait", {"ms": ms, "frames": frames})
        return f"Wait completed: {result.get('waited', '')}"
    except Exception as exc:
        return f"Wait failed: {exc}"


# -- refresh ----------------------------------------------------------------

@mcp.tool()
async def playcaller_refresh() -> str:
    """Refresh Unity AssetDatabase.

    Triggers AssetDatabase.Refresh() to reimport changed assets.
    Use after modifying files outside Unity to ensure changes are picked up.
    If the refresh triggers script recompilation, waits for domain reload to complete.
    """
    if not unity.connected:
        return "ERROR: Unity is not connected. The refresh command was NOT sent."

    try:
        result = await unity.send_command("refresh")
    except Exception as exc:
        _log("Refresh command error: %s", exc)
        reconnected = await unity.wait_for_reconnect(30)
        if reconnected:
            return (
                "WARNING: send_command('refresh') raised an exception, "
                "then reconnection succeeded.\n"
                "UNKNOWN: Whether the refresh command reached Unity.\n"
                "Verify independently whether compilation occurred."
            )
        return (
            "ERROR: send_command('refresh') raised an exception: "
            f"{exc}\n"
            "UNKNOWN: Whether the refresh command reached Unity.\n"
            "FACT: Reconnection also failed after 30s."
        )

    if result.get("isCompiling"):
        # send_command 成功 + isCompiling=true → refresh は確実に届き、コンパイル開始
        _log("Refresh triggered compilation, waiting for domain reload...")
        reconnected = await unity.wait_for_reconnect(30)
        if reconnected:
            return "AssetDatabase refreshed (compilation and domain reload completed)."
        return (
            "FACT: Refresh command was sent and compilation started.\n"
            "FACT: Reconnection timed out after 30s.\n"
            "UNKNOWN: Whether compilation finished."
        )

    return "AssetDatabase refreshed successfully (no recompilation needed)."


# -- get_hierarchy ----------------------------------------------------------

@mcp.tool()
async def playcaller_get_hierarchy(scene: str = "", maxDepth: int = 10) -> str:
    """Get the GameObject hierarchy of a Unity scene.

    Returns the tree of GameObjects with name, instanceId, activeSelf, and children.
    scene: scene name (default: active scene).
    maxDepth: max recursion depth (default 10).
    """
    try:
        params: dict[str, Any] = {"maxDepth": maxDepth}
        if scene:
            params["scene"] = scene
        result = await unity.send_command("get_hierarchy", params)
        return json.dumps(result, indent=2, ensure_ascii=False)
    except Exception as exc:
        return f"Get hierarchy failed: {exc}"


# -- get_gameobject ---------------------------------------------------------

@mcp.tool()
async def playcaller_get_gameobject(path: str = "", instanceId: int = 0) -> str:
    """Get detailed information about a specific GameObject.

    Returns name, instanceId, activeSelf, tag, layer, transform, and component list.
    path: hierarchy path (e.g. "/Canvas/Button"). Uses GameObject.Find().
    instanceId: instance ID from get_hierarchy. Specify either path or instanceId.
    """
    try:
        params: dict[str, Any] = {}
        if path:
            params["path"] = path
        if instanceId:
            params["instanceId"] = instanceId
        if not params:
            return "Either 'path' or 'instanceId' parameter is required."
        result = await unity.send_command("get_gameobject", params)
        return json.dumps(result, indent=2, ensure_ascii=False)
    except Exception as exc:
        return f"Get gameobject failed: {exc}"


# -- execute_menu_item ------------------------------------------------------

@mcp.tool()
async def playcaller_execute_menu_item(menuPath: str) -> str:
    """Execute a Unity Editor menu item.

    menuPath: full menu path (e.g. "Assets/Refresh", "File/Save", "Edit/Play").
    Returns whether the menu item was found and executed.
    """
    try:
        result = await unity.send_command("execute_menu_item", {"menuPath": menuPath})
        if result.get("executed"):
            return f"Menu item executed: {result.get('menuPath')}"
        return f"Execute menu item result: {json.dumps(result)}"
    except Exception as exc:
        return f"Execute menu item failed: {exc}"


# -- key_press --------------------------------------------------------------

@mcp.tool()
async def playcaller_key_press(key: str) -> str:
    """Press a key on the keyboard.

    key: Unity KeyCode name (e.g. "LeftArrow", "RightArrow", "Space", "Return", "A", "B").
    See Unity KeyCode enum for all valid values.
    """
    try:
        result = await unity.send_command("key_press", {"key": key})
        if result.get("enqueued"):
            return f'Key pressed: {result.get("key")}'
        return f"Key press result: {json.dumps(result)}"
    except Exception as exc:
        return f"Key press failed: {exc}"


# -- game_query -------------------------------------------------------------

@mcp.tool()
async def playcaller_game_query(queryType: str = "") -> str:
    """Ask the game what actions are currently available.

    The game responds with available operations, screen coordinates, and expected outcomes.
    If the game has not implemented query support, an error message is returned.

    queryType: optional query type hint (default: general query).
    """
    try:
        result = await unity.send_command("game_query", {"queryType": queryType})
        # If result contains a "text" field, return it directly for readability
        if isinstance(result, dict) and "text" in result:
            return result["text"]
        return json.dumps(result, indent=2, ensure_ascii=False)
    except Exception as exc:
        return f"Game query failed: {exc}"


# -- set_game_view_size -----------------------------------------------------

@mcp.tool()
async def playcaller_set_game_view_size(width: int, height: int) -> str:
    """Set the Unity Game View to a specific resolution.

    Uses the same internal mechanism as Unity Recorder.
    The Game View will render at the exact specified resolution.
    Useful for capturing store screenshots at specific dimensions (e.g., 1242x2208 for iPhone).

    width: target width in pixels.
    height: target height in pixels.
    """
    try:
        result = await unity.send_command("set_game_view_size", {
            "width": width, "height": height,
        })
        return result.get("message", json.dumps(result))
    except Exception as exc:
        return f"Set Game View size failed: {exc}"


# -- get_editor_state -------------------------------------------------------

@mcp.tool()
async def playcaller_get_editor_state() -> str:
    """Get the current Unity Editor state.

    Returns isPlaying, isPaused, isCompiling, activeScene, activeScenePath,
    screenWidth, screenHeight, unityVersion, and platform.
    """
    try:
        result = await unity.send_command("get_editor_state")
        return json.dumps(result, indent=2, ensure_ascii=False)
    except Exception as exc:
        return f"Get editor state failed: {exc}"


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def main():
    mcp.run(transport="stdio")


if __name__ == "__main__":
    main()
