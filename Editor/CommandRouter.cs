using System;
using UnityEngine;
using PlayCaller.Editor.Models;
using PlayCaller.Editor.Handlers;

namespace PlayCaller.Editor
{
	public static class CommandRouter
	{
		/// <summary>
		/// Routes a command to the appropriate handler.
		/// Returns either a string (sync) or Task&lt;string&gt; (async).
		/// </summary>
		public static object Route(PlayCallerCommand command)
		{
			if (command == null)
				return PlayCallerResponse.Error(null, "Null command", "NULL_COMMAND");

			var type = command.Type?.ToLowerInvariant();

			switch (type)
			{
				case "ping":
					return PlayCallerResponse.Success(command.Id, new
					{
						message = "pong",
						timestamp = DateTime.UtcNow.ToString("o")
					});

				case "screenshot":
					return ScreenshotHandler.Handle(command);

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

				default:
					return PlayCallerResponse.Error(command.Id,
						$"Unknown command type: {command.Type}", "UNKNOWN_COMMAND");
			}
		}
	}
}
