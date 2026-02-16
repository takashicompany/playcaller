using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using PlayCaller.Editor.Models;

namespace PlayCaller.Editor.Handlers
{
	public static class HierarchyHandler
	{
		public static string Handle(PlayCallerCommand command)
		{
			try
			{
				string sceneName = command.Params?["scene"]?.ToString();
				int maxDepth = command.Params?["maxDepth"]?.ToObject<int>() ?? 10;

				Scene scene;
				if (!string.IsNullOrEmpty(sceneName))
				{
					scene = SceneManager.GetSceneByName(sceneName);
					if (!scene.IsValid())
						return PlayCallerResponse.Error(command.Id,
							$"Scene not found: {sceneName}", "SCENE_NOT_FOUND");
				}
				else
				{
					scene = SceneManager.GetActiveScene();
				}

				var rootObjects = scene.GetRootGameObjects();
				var result = new List<object>();
				foreach (var go in rootObjects)
				{
					result.Add(BuildNode(go, 0, maxDepth));
				}

				return PlayCallerResponse.Success(command.Id, new
				{
					scene = scene.name,
					rootObjects = result
				});
			}
			catch (Exception ex)
			{
				return PlayCallerResponse.Error(command.Id,
					$"Get hierarchy failed: {ex.Message}", "HIERARCHY_ERROR");
			}
		}

		private static object BuildNode(GameObject go, int depth, int maxDepth)
		{
			var children = new List<object>();
			if (depth < maxDepth)
			{
				for (int i = 0; i < go.transform.childCount; i++)
				{
					children.Add(BuildNode(go.transform.GetChild(i).gameObject, depth + 1, maxDepth));
				}
			}

			return new
			{
				name = go.name,
				instanceId = go.GetInstanceID(),
				activeSelf = go.activeSelf,
				children = children
			};
		}
	}
}
