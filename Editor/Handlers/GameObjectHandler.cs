using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using PlayCaller.Editor.Models;

namespace PlayCaller.Editor.Handlers
{
	public static class GameObjectHandler
	{
		public static string Handle(PlayCallerCommand command)
		{
			try
			{
				string path = command.Params?["path"]?.ToString();
				int? instanceId = command.Params?["instanceId"]?.ToObject<int>();

				GameObject go = null;

				if (!string.IsNullOrEmpty(path))
				{
					go = GameObject.Find(path);
				}
				else if (instanceId.HasValue)
				{
					var obj = EditorUtility.InstanceIDToObject(instanceId.Value);
					go = obj as GameObject;
				}
				else
				{
					return PlayCallerResponse.Error(command.Id,
						"Either 'path' or 'instanceId' parameter is required.", "MISSING_PARAM");
				}

				if (go == null)
				{
					return PlayCallerResponse.Error(command.Id,
						$"GameObject not found.", "NOT_FOUND");
				}

				var t = go.transform;
				var components = new List<string>();
				foreach (var c in go.GetComponents<Component>())
				{
					if (c != null)
						components.Add(c.GetType().Name);
				}

				return PlayCallerResponse.Success(command.Id, new
				{
					name = go.name,
					instanceId = go.GetInstanceID(),
					activeSelf = go.activeSelf,
					tag = go.tag,
					layer = go.layer,
					transform = new
					{
						position = new[] { t.position.x, t.position.y, t.position.z },
						rotation = new[] { t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w },
						localScale = new[] { t.localScale.x, t.localScale.y, t.localScale.z }
					},
					components = components
				});
			}
			catch (Exception ex)
			{
				return PlayCallerResponse.Error(command.Id,
					$"Get gameobject failed: {ex.Message}", "GAMEOBJECT_ERROR");
			}
		}
	}
}
