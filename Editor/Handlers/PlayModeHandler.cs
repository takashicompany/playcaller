using System;
using UnityEngine;
using UnityEditor;
using PlayCaller.Editor.Models;

namespace PlayCaller.Editor.Handlers
{
	public static class PlayModeHandler
	{
		public static string Handle(PlayCallerCommand command)
		{
			try
			{
				string action = command.Params?["action"]?.ToString() ?? "get_state";

				switch (action.ToLowerInvariant())
				{
					case "play":
						if (!EditorApplication.isPlaying)
							EditorApplication.isPlaying = true;
						return PlayCallerResponse.Success(command.Id, new
						{
							isPlaying = true,
							isPaused = EditorApplication.isPaused,
							message = "Play Mode started"
						});

					case "pause":
						EditorApplication.isPaused = !EditorApplication.isPaused;
						return PlayCallerResponse.Success(command.Id, new
						{
							isPlaying = EditorApplication.isPlaying,
							isPaused = EditorApplication.isPaused,
							message = EditorApplication.isPaused ? "Paused" : "Resumed"
						});

					case "stop":
						if (EditorApplication.isPlaying)
							EditorApplication.isPlaying = false;
						return PlayCallerResponse.Success(command.Id, new
						{
							isPlaying = false,
							isPaused = false,
							message = "Play Mode stopped"
						});

					case "get_state":
						return PlayCallerResponse.Success(command.Id, new
						{
							isPlaying = EditorApplication.isPlaying,
							isPaused = EditorApplication.isPaused,
							isCompiling = EditorApplication.isCompiling,
							message = EditorApplication.isPlaying
								? (EditorApplication.isPaused ? "Paused" : "Playing")
								: "Stopped"
						});

					default:
						return PlayCallerResponse.Error(command.Id,
							$"Unknown playmode action: {action}", "INVALID_ACTION");
				}
			}
			catch (Exception ex)
			{
				return PlayCallerResponse.Error(command.Id,
					$"PlayMode command failed: {ex.Message}", "PLAYMODE_ERROR");
			}
		}
	}
}
