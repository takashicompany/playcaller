using System;
using UnityEngine;
using Playcaller.Editor.Models;
using Playcaller.Editor.Handlers;

namespace Playcaller.Editor
{
	public static class CommandRouter
	{
		/// <summary>
		/// Routes a command to the appropriate handler.
		/// Returns either a string (sync) or Task&lt;string&gt; (async).
		/// </summary>
		public static object Route(PlaycallerCommand command)
		{
			if (command == null)
				return PlaycallerResponse.Error(null, "Null command", "NULL_COMMAND");

			var type = command.Type?.ToLowerInvariant();

			switch (type)
			{
				case "ping":
					return PlaycallerResponse.Success(command.Id, new
					{
						message = "pong",
						timestamp = DateTime.UtcNow.ToString("o")
					});

				case "screenshot":
					return ScreenshotHandler.Handle(command);

				case "screenshot_hdr":
					return ScreenshotHandler.HandleHDR(command);

				case "playmode":
					return PlayModeHandler.Handle(command);

				case "console_log":
					return ConsoleLogHandler.Handle(command);

				case "wait":
					return WaitHandler.Handle(command);

				case "tap":
					return InputSimulationHandler.HandleTap(command);

				case "drag":
					return InputSimulationHandler.HandleDrag(command);

				case "flick":
					return InputSimulationHandler.HandleFlick(command);

				case "refresh":
					return RefreshHandler.Handle(command);

				case "get_hierarchy":
					return HierarchyHandler.Handle(command);

				case "get_gameobject":
					return GameObjectHandler.Handle(command);

				case "execute_menu_item":
					return MenuItemHandler.Handle(command);

				case "get_editor_state":
					return EditorStateHandler.Handle(command);

				case "key_press":
					return KeyInputHandler.Handle(command);

				case "game_query":
					return GameQueryHandler.Handle(command);

				case "set_game_view_size":
					return GameViewHandler.Handle(command);

				default:
					return PlaycallerResponse.Error(command.Id,
						$"Unknown command type: {command.Type}", "UNKNOWN_COMMAND");
			}
		}
	}
}
