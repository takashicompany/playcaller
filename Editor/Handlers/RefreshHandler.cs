using UnityEditor;
using PlayCaller.Editor.Models;

namespace PlayCaller.Editor.Handlers
{
	public static class RefreshHandler
	{
		public static string Handle(PlayCallerCommand command)
		{
			AssetDatabase.Refresh();
			return PlayCallerResponse.Success(command.Id, new
			{
				refreshed = true,
				isCompiling = EditorApplication.isCompiling
			});
		}
	}
}
