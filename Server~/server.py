# /// script
# requires-python = ">=3.10"
# dependencies = [
#     "mcp[cli]>=1.2.0",
# ]
# ///
"""playcaller – Unity MCP server (FastMCP, single-file)."""

from __future__ import annotations

import asyncio
import json
import os
import struct
import sys
from pathlib import Path
from typing import Any

from mcp.server.fastmcp import FastMCP

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
UNITY_HOST = "127.0.0.1"
PORT_FILE_NAME = "PlayCaller.port"
COMMAND_TIMEOUT_S = 30
RECONNECT_DELAY_S = 2
MAX_RECONNECT_DELAY_S = 30
MAX_MESSAGE_SIZE = 1024 * 1024  # 1 MB

# ---------------------------------------------------------------------------
# UnityConnection – asyncio TCP with 4-byte BE length-prefix framing
# ---------------------------------------------------------------------------

class UnityConnection:
    """Manages a TCP connection to the Unity Editor playcaller plugin."""

    def __init__(self) -> None:
        self._reader: asyncio.StreamReader | None = None
        self._writer: asyncio.StreamWriter | None = None
        self._connected = False
        self._command_id = 0
        self._pending: dict[str, asyncio.Future[Any]] = {}
        self._recv_task: asyncio.Task[None] | None = None
        self._reconnect_task: asyncio.Task[None] | None = None
        self._reconnect_attempts = 0
        self._disconnecting = False
        self._connect_lock = asyncio.Lock()

        # Last screenshot / screen dimensions for coordinate conversion
        self.last_screenshot_width = 0
        self.last_screenshot_height = 0
        self.last_screen_width = 0
        self.last_screen_height = 0

    # -- public API ---------------------------------------------------------

    @property
    def connected(self) -> bool:
        return self._connected

    async def connect(self) -> None:
        """Open the TCP connection (idempotent)."""
        async with self._connect_lock:
            if self._connected:
                return
            port = _read_port_from_file()
            _log("Connecting to Unity at %s:%s ...", UNITY_HOST, port)
            try:
                self._reader, self._writer = await asyncio.wait_for(
                    asyncio.open_connection(UNITY_HOST, port),
                    timeout=10,
                )
            except Exception as exc:
                raise ConnectionError(f"Unity connection failed: {exc}") from exc
            self._connected = True
            self._reconnect_attempts = 0
            self._recv_task = asyncio.create_task(self._receive_loop())
            _log("Connected to Unity at %s:%s", UNITY_HOST, port)

    def disconnect(self) -> None:
        """Tear down the connection synchronously (best-effort)."""
        self._disconnecting = True
        self._cancel_reconnect()
        if self._recv_task and not self._recv_task.done():
            self._recv_task.cancel()
        if self._writer:
            self._writer.close()
        self._reader = None
        self._writer = None
        self._connected = False
        # Reject pending commands
        for fut in self._pending.values():
            if not fut.done():
                fut.set_exception(ConnectionError("Disconnected"))
        self._pending.clear()
        self._disconnecting = False

    async def send_command(self, cmd_type: str, params: dict[str, Any] | None = None) -> Any:
        """Send a command and wait for the matching response."""
        if not self._connected:
            await self.connect()

        self._command_id += 1
        cmd_id = str(self._command_id)
        payload = json.dumps({"id": cmd_id, "type": cmd_type, "params": params or {}}).encode()

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
            # Connection is likely dead — tear down and schedule reconnect
            _log("Command %s timed out, marking connection as dead", cmd_type)
            self._connected = False
            if self._recv_task and not self._recv_task.done():
                self._recv_task.cancel()
            if self._writer:
                self._writer.close()
            self._reader = None
            self._writer = None
            for f in self._pending.values():
                if not f.done():
                    f.set_exception(ConnectionError("Connection reset after timeout"))
            self._pending.clear()
            self._schedule_reconnect()
            raise TimeoutError(f"Command {cmd_type} timed out after {COMMAND_TIMEOUT_S}s")

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
        resp_id = str(response.get("id", "")) if response.get("id") is not None else None

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
            fut.set_exception(RuntimeError(response.get("error", "Command failed")))
        else:
            fut.set_result(response)

    # -- reconnection -------------------------------------------------------

    def _on_disconnected(self) -> None:
        if self._disconnecting:
            return
        _log("Disconnected from Unity")
        self._connected = False
        if self._writer:
            self._writer.close()
        self._reader = None
        self._writer = None
        # Reject pending commands
        for fut in self._pending.values():
            if not fut.done():
                fut.set_exception(ConnectionError("Connection closed"))
        self._pending.clear()
        if not self._disconnecting:
            self._schedule_reconnect()

    def _cancel_reconnect(self) -> None:
        if self._reconnect_task and not self._reconnect_task.done():
            self._reconnect_task.cancel()
            self._reconnect_task = None

    def _schedule_reconnect(self) -> None:
        if self._reconnect_task and not self._reconnect_task.done():
            return
        delay = min(RECONNECT_DELAY_S * (2 ** self._reconnect_attempts), MAX_RECONNECT_DELAY_S)
        _log("Scheduling reconnection in %.1fs (attempt %d)", delay, self._reconnect_attempts + 1)
        self._reconnect_task = asyncio.create_task(self._reconnect_after(delay))

    async def _reconnect_after(self, delay: float) -> None:
        await asyncio.sleep(delay)
        self._reconnect_attempts += 1
        try:
            await self.connect()
        except Exception as exc:
            _log("Reconnection failed: %s", exc)
            if not self._disconnecting:
                self._reconnect_task = None
                self._schedule_reconnect()


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _log(fmt: str, *args: object) -> None:
    print(f"[playcaller] {fmt % args}", file=sys.stderr, flush=True)


def _read_port_from_file() -> int:
    """Read the Unity TCP port from the port file written by PlayCallerServer."""
    project_dir = os.environ.get("UNITY_PROJECT_DIR")
    if not project_dir:
        raise RuntimeError(
            "UNITY_PROJECT_DIR environment variable is not set. "
            "Set it to the Unity project root directory."
        )
    port_file = Path(project_dir) / "Temp" / PORT_FILE_NAME
    if not port_file.exists():
        raise FileNotFoundError(
            f"Port file not found: {port_file}. "
            "Is the Unity Editor running with PlayCaller?"
        )
    text = port_file.read_text().strip()
    try:
        return int(text)
    except ValueError:
        raise ValueError(f"Invalid port number in {port_file}: {text!r}")


async def _wait_for_reconnect(unity: UnityConnection, timeout_s: float) -> bool:
    """Poll until Unity reconnects after domain reload."""
    await asyncio.sleep(1)  # wait for disconnect to happen
    deadline = asyncio.get_event_loop().time() + timeout_s
    while asyncio.get_event_loop().time() < deadline:
        if unity.connected:
            try:
                await unity.send_command("ping")
                return True
            except Exception:
                pass
        else:
            try:
                await unity.connect()
                try:
                    await unity.send_command("ping")
                    return True
                except Exception:
                    pass
            except Exception:
                pass
        await asyncio.sleep(0.5)
    return False


# ---------------------------------------------------------------------------
# FastMCP application
# ---------------------------------------------------------------------------

mcp = FastMCP("playcaller")
unity = UnityConnection()

# -- screenshot -------------------------------------------------------------

@mcp.tool()
async def playcaller_screenshot(width: int = 0, height: int = 0) -> str:
    """Capture a screenshot of the Unity Game View.

    Returns the file path of the saved PNG screenshot.
    width/height: optional resolution in pixels (default: Game View size).
    """
    try:
        result = await unity.send_command("screenshot", {"width": width, "height": height})
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
    try:
        result = await unity.send_command("playmode", {"action": action})
        text = json.dumps(result, indent=2)

        if action in ("play", "stop"):
            reconnected = await _wait_for_reconnect(unity, 15)
            if not reconnected:
                return (
                    text + "\n\nWarning: Unity domain reload completed but TCP reconnection timed out. "
                    "Subsequent commands may fail. Try playcaller_wait with ms=2000 before next command."
                )
        return text
    except Exception as exc:
        if action in ("play", "stop"):
            _log("%s command got error (expected during domain reload): %s", action, exc)
            reconnected = await _wait_for_reconnect(unity, 15)
            if reconnected:
                return f"Play Mode {'started' if action == 'play' else 'stopped'} (reconnected after domain reload)"
            return f"Play Mode {action} sent but reconnection failed after domain reload. Unity may still be reloading."
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
    try:
        result = await unity.send_command("refresh")
    except Exception as exc:
        # Connection lost during refresh likely means domain reload started
        _log("Refresh command error (possible domain reload): %s", exc)
        reconnected = await _wait_for_reconnect(unity, 30)
        if reconnected:
            return "AssetDatabase refreshed (domain reload completed, reconnected)."
        return "AssetDatabase refresh triggered domain reload but reconnection timed out. Unity may still be compiling."

    if result.get("isCompiling"):
        # Compilation started — domain reload will follow, connection will drop
        _log("Refresh triggered compilation, waiting for domain reload...")
        reconnected = await _wait_for_reconnect(unity, 30)
        if reconnected:
            return "AssetDatabase refreshed (compilation and domain reload completed)."
        return "AssetDatabase refreshed but compilation/domain reload timed out. Unity may still be compiling."

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

if __name__ == "__main__":
    mcp.run(transport="stdio")
