using System;
using UnityEditor;
using Playcaller.Editor.Models;

namespace Playcaller.Editor.Handlers
{
	public static class MenuItemHandler
	{
		public static string Handle(PlaycallerCommand command)
		{
			try
			{
				string menuPath = command.Params?["menuPath"]?.ToString();

				if (string.IsNullOrEmpty(menuPath))
				{
					return PlaycallerResponse.Error(command.Id,
						"'menuPath' parameter is required.", "MISSING_PARAM");
				}

				bool result = EditorApplication.ExecuteMenuItem(menuPath);

				if (result)
				{
					return PlaycallerResponse.Success(command.Id, new
					{
						executed = true,
						menuPath = menuPath
					});
				}
				else
				{
					return PlaycallerResponse.Error(command.Id,
						$"Menu item not found or failed: {menuPath}", "MENU_ITEM_FAILED");
				}
			}
			catch (Exception ex)
			{
				return PlaycallerResponse.Error(command.Id,
					$"Execute menu item failed: {ex.Message}", "MENU_ITEM_ERROR");
			}
		}
	}
}
