using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using PlayCaller.Editor.Models;

namespace PlayCaller.Editor.Handlers
{
	public static class EditorStateHandler
	{
		public static string Handle(PlayCallerCommand command)
		{
			try
			{
				var activeScene = SceneManager.GetActiveScene();

				return PlayCallerResponse.Success(command.Id, new
				{
					isPlaying = EditorApplication.isPlaying,
					isPaused = EditorApplication.isPaused,
					isCompiling = EditorApplication.isCompiling,
					activeScene = activeScene.name,
					activeScenePath = activeScene.path,
					screenWidth = Screen.width,
					screenHeight = Screen.height,
					unityVersion = Application.unityVersion,
					platform = Application.platform.ToString()
				});
			}
			catch (Exception ex)
			{
				return PlayCallerResponse.Error(command.Id,
					$"Get editor state failed: {ex.Message}", "EDITOR_STATE_ERROR");
			}
		}
	}
}
