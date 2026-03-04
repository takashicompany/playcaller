using System.Threading.Tasks;
using UnityEditor;
using Playcaller.Editor.Models;

namespace Playcaller.Editor.Handlers
{
	public static class RefreshHandler
	{
		/// <summary>
		/// AssetDatabase.Refresh() is async - isCompiling may not be true immediately.
		/// Wait a few frames to let Unity start compilation before reporting status.
		/// </summary>
		public static object Handle(PlaycallerCommand command)
		{
			AssetDatabase.Refresh();

			// If already compiling (e.g. from a prior change), return immediately
			if (EditorApplication.isCompiling)
			{
				return PlaycallerResponse.Success(command.Id, new
				{
					refreshed = true,
					isCompiling = true
				});
			}

			// Wait a few frames for isCompiling to potentially become true
			var tcs = new TaskCompletionSource<string>();
			int remainingFrames = 10;

			void Tick()
			{
				if (EditorApplication.isCompiling)
				{
					EditorApplication.update -= Tick;
					tcs.TrySetResult(PlaycallerResponse.Success(command.Id, new
					{
						refreshed = true,
						isCompiling = true
					}));
					return;
				}

				remainingFrames--;
				if (remainingFrames <= 0)
				{
					EditorApplication.update -= Tick;
					tcs.TrySetResult(PlaycallerResponse.Success(command.Id, new
					{
						refreshed = true,
						isCompiling = false
					}));
				}
			}

			EditorApplication.update += Tick;
			return tcs.Task;
		}
	}
}
