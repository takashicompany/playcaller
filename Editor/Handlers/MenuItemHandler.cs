using System;
using UnityEditor;
using PlayCaller.Editor.Models;

namespace PlayCaller.Editor.Handlers
{
	public static class MenuItemHandler
	{
		public static string Handle(PlayCallerCommand command)
		{
			try
			{
				string menuPath = command.Params?["menuPath"]?.ToString();

				if (string.IsNullOrEmpty(menuPath))
				{
					return PlayCallerResponse.Error(command.Id,
						"'menuPath' parameter is required.", "MISSING_PARAM");
				}

				bool result = EditorApplication.ExecuteMenuItem(menuPath);

				if (result)
				{
					return PlayCallerResponse.Success(command.Id, new
					{
						executed = true,
						menuPath = menuPath
					});
				}
				else
				{
					return PlayCallerResponse.Error(command.Id,
						$"Menu item not found or failed: {menuPath}", "MENU_ITEM_FAILED");
				}
			}
			catch (Exception ex)
			{
				return PlayCallerResponse.Error(command.Id,
					$"Execute menu item failed: {ex.Message}", "MENU_ITEM_ERROR");
			}
		}
	}
}
