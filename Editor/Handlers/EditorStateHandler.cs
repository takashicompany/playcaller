using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Playcaller.Editor.Models;

namespace Playcaller.Editor.Handlers
{
	public static class EditorStateHandler
	{
		public static string Handle(PlaycallerCommand command)
		{
			try
			{
				var activeScene = SceneManager.GetActiveScene();

				return PlaycallerResponse.Success(command.Id, new
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
				return PlaycallerResponse.Error(command.Id,
					$"Get editor state failed: {ex.Message}", "EDITOR_STATE_ERROR");
			}
		}
	}
}
