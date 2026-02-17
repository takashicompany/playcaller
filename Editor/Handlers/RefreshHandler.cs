using UnityEditor;
using Playcaller.Editor.Models;

namespace Playcaller.Editor.Handlers
{
	public static class RefreshHandler
	{
		public static string Handle(PlaycallerCommand command)
		{
			AssetDatabase.Refresh();
			return PlaycallerResponse.Success(command.Id, new
			{
				refreshed = true,
				isCompiling = EditorApplication.isCompiling
			});
		}
	}
}
