using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Playcaller.Editor
{
	public static class PlaycallerSetupMenu
	{
		private const string MenuPath = "Playcaller/初期設定の実行";

		[MenuItem(MenuPath, validate = true)]
		private static bool ValidateSetup()
		{
			Menu.SetChecked(MenuPath, IsConfigured());
			return true;
		}

		[MenuItem(MenuPath)]
		private static void RunSetup()
		{
			string claudePath = FindClaude();
			if (claudePath == null)
			{
				EditorUtility.DisplayDialog(
					"Playcaller",
					"Claude CLI が見つかりません。\nClaude Code をインストールしてください。\nhttps://claude.ai/download",
					"OK");
				return;
			}

			string serverPath = FindServerPath();
			if (serverPath == null)
			{
				EditorUtility.DisplayDialog(
					"Playcaller",
					"server.py が見つかりません。\nパッケージが正しくインストールされているか確認してください。",
					"OK");
				return;
			}

			string projectDir = Path.GetDirectoryName(Application.dataPath);
			string args = $"mcp add --scope local --transport stdio playcaller -- uvx --from \"mcp[cli]>=1.2.0\" mcp run \"{serverPath}\"";

			try
			{
				var psi = new ProcessStartInfo
				{
					FileName = claudePath,
					Arguments = args,
					WorkingDirectory = projectDir,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true,
				};

				using (var process = Process.Start(psi))
				{
					string stdout = process.StandardOutput.ReadToEnd();
					string stderr = process.StandardError.ReadToEnd();
					process.WaitForExit(15000);

					if (process.ExitCode == 0)
					{
						Debug.Log("[Playcaller] MCP サーバーの登録が完了しました。");
						EditorUtility.DisplayDialog(
							"Playcaller",
							"MCP サーバーの登録が完了しました。\nClaude Code を起動すると自動的に接続されます。",
							"OK");
					}
					else
					{
						Debug.LogError($"[Playcaller] 設定に失敗しました: {stderr}");
						EditorUtility.DisplayDialog(
							"Playcaller",
							$"設定に失敗しました。\n\n{stderr}",
							"OK");
					}
				}
			}
			catch (Exception e)
			{
				Debug.LogError($"[Playcaller] エラー: {e.Message}");
				EditorUtility.DisplayDialog(
					"Playcaller",
					$"エラーが発生しました。\n\n{e.Message}",
					"OK");
			}
		}

		private static string FindServerPath()
		{
			var packageInfo = PackageInfo.FindForAssembly(typeof(PlaycallerSetupMenu).Assembly);
			if (packageInfo == null)
				return null;

			string path = Path.Combine(packageInfo.resolvedPath, "Server~", "server.py");
			return File.Exists(path) ? path : null;
		}

		private static bool IsConfigured()
		{
			string projectDir = Path.GetDirectoryName(Application.dataPath);

			string[] candidates =
			{
				Path.Combine(projectDir, ".claude", "settings.local.json"),
				Path.Combine(projectDir, ".mcp.json"),
			};

			foreach (string filePath in candidates)
			{
				if (!File.Exists(filePath)) continue;
				try
				{
					string content = File.ReadAllText(filePath);
					if (content.Contains("playcaller"))
						return true;
				}
				catch
				{
					// ignore
				}
			}

			return false;
		}

		private static string FindClaude()
		{
			try
			{
				string whichCmd = Application.platform == RuntimePlatform.WindowsEditor
					? "where"
					: "which";
				var psi = new ProcessStartInfo
				{
					FileName = whichCmd,
					Arguments = "claude",
					UseShellExecute = false,
					RedirectStandardOutput = true,
					CreateNoWindow = true,
				};
				using (var process = Process.Start(psi))
				{
					string output = process.StandardOutput.ReadToEnd().Trim();
					process.WaitForExit(5000);
					if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
					{
						string firstLine = output.Split('\n')[0].Trim();
						if (File.Exists(firstLine))
							return firstLine;
					}
				}
			}
			catch
			{
				// ignore
			}

			if (Application.platform != RuntimePlatform.WindowsEditor)
			{
				string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
				string[] candidates =
				{
					"/opt/homebrew/bin/claude",
					"/usr/local/bin/claude",
					Path.Combine(home, ".local", "bin", "claude"),
				};
				foreach (string path in candidates)
				{
					if (File.Exists(path))
						return path;
				}
			}

			return null;
		}
	}
}
