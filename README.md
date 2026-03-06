# Playcaller

[日本語はこちら](README_ja.md)

An MCP server that lets AI agents play-test Unity games like a real player.
It provides screenshot capture, input simulation (tap, drag, flick, key press), Play Mode control, and more — all accessible from AI agents.

In short, it's a **[Playwright MCP](https://github.com/anthropics/anthropic-quickstarts/tree/main/mcp-playwright) for Unity**.

## Features

- Game View screenshot capture
- Tap, drag, flick, and key input simulation
- Play Mode start / stop / pause
- Scene GameObject hierarchy inspection
- Console log retrieval
- AssetDatabase refresh
- Menu item execution
- Editor state retrieval

## Limitations

- Not suited for testing fast-paced action games
- Not suited for evaluating overall game experience
- Not suited for judging visual effects quality

## Requirements

- Unity 2022.3 or later
- Python 3.10 or later ([uv](https://docs.astral.sh/uv/) recommended)

## Setup

### 1. Install the Unity package

1. Open **Window > Package Manager** in the Unity Editor menu bar
2. Click the **+** button in the top-left corner
3. Select **Add package from git URL...**
4. Enter the following URL and click **Add**

```
https://github.com/takashicompany/playcaller.git
```

Newtonsoft.Json (`com.unity.nuget.newtonsoft-json`) will be installed automatically if not already present.

### 2. Register the MCP server

Run the following command:

```sh
claude mcp add playcaller -- uvx playcaller
```

### 3. Verify the connection

Launch Claude Code while the Unity Editor is open, and it will connect automatically.

## MCP Tools

| Tool | Description |
|---|---|
| `playcaller_screenshot` | Capture a Game View screenshot |
| `playcaller_tap` | Tap at specified coordinates |
| `playcaller_drag` | Drag operation |
| `playcaller_flick` | Flick (quick swipe) operation |
| `playcaller_key_press` | Keyboard input |
| `playcaller_playmode` | Start / stop / pause Play Mode |
| `playcaller_wait` | Wait for a specified time or frame count |
| `playcaller_console_log` | Retrieve Unity console logs |
| `playcaller_refresh` | Refresh AssetDatabase |
| `playcaller_get_hierarchy` | Get the GameObject hierarchy in the scene |
| `playcaller_get_gameobject` | Get detailed info about a specific GameObject |
| `playcaller_execute_menu_item` | Execute a Unity Editor menu item |
| `playcaller_get_editor_state` | Get the current editor state |
