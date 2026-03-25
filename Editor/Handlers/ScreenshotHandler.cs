using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using Playcaller.Editor.Models;

namespace Playcaller.Editor.Handlers
{
	public static class ScreenshotHandler
	{
		static readonly string ScreenshotDir = Path.Combine(
			Path.GetDirectoryName(Application.dataPath), "Temp", "Playcaller", "Screenshots");
		const string ScreenshotFileName = "screenshot.png";
		static readonly string ProjectRoot = Path.GetDirectoryName(Application.dataPath);

		/// <summary>PNG をファイルに保存し、絶対パスを返す。</summary>
		static string SaveToFile(byte[] imageBytes, string filename)
		{
			string filePath;
			if (!string.IsNullOrEmpty(filename))
			{
				// filename が指定されていればそのパスに保存
				filePath = Path.IsPathRooted(filename) ? filename : Path.Combine(ProjectRoot, filename);
			}
			else
			{
				filePath = Path.Combine(ScreenshotDir, ScreenshotFileName);
			}

			Directory.CreateDirectory(Path.GetDirectoryName(filePath));
			File.WriteAllBytes(filePath, imageBytes);
			return filePath;
		}

		/// <summary>スクリーンショット成功時のレスポンスを生成する。</summary>
		static string MakeSuccessResponse(string commandId, string filePath, int width, int height)
		{
			return PlaycallerResponse.Success(commandId, new
			{
				filePath = filePath,
				width = width,
				height = height,
				screenWidth = Screen.width,
				screenHeight = Screen.height,
			});
		}

		/// <summary>
		/// Play Mode 中は ScreenCapture.CaptureScreenshotAsTexture() を使い、
		/// Screen Space Overlay の Canvas UI を含むゲーム画面全体をキャプチャする。
		/// Play Mode 外では camera.Render() + RenderTexture 方式にフォールバックする。
		/// </summary>
		public static object Handle(PlaycallerCommand command)
		{
			try
			{
				int width = 0;
				int height = 0;
				string filename = null;

				if (command.Params != null)
				{
					width = command.Params["width"]?.ToObject<int>() ?? 0;
					height = command.Params["height"]?.ToObject<int>() ?? 0;
					filename = command.Params["filename"]?.ToString();
				}

				// Play Mode 中は ScreenCapture を使って UI を含む完全なゲーム画面をキャプチャ
				if (Application.isPlaying)
				{
					return CaptureWithScreenCapture(command.Id, width, height, filename);
				}
				else
				{
					return CaptureWithCamera(command.Id, width, height, filename);
				}
			}
			catch (Exception ex)
			{
				return PlaycallerResponse.Error(command.Id,
					$"Screenshot failed: {ex.Message}", "SCREENSHOT_ERROR");
			}
		}

		/// <summary>
		/// Play Mode 中: ScreenCapture.CaptureScreenshotAsTexture() を使用。
		/// WaitForEndOfFrame 後に呼び出す必要があるため、一時的な MonoBehaviour のコルーチンを使う。
		/// Task<string> を返し、PlaycallerClient の ProcessCommandAsync で await される。
		/// </summary>
		private static Task<string> CaptureWithScreenCapture(string commandId, int width, int height, string filename)
		{
			var tcs = new TaskCompletionSource<string>();

			// Play Mode 中なので MonoBehaviour コルーチンが使える
			// 一時的な GameObject + MonoBehaviour を作成してコルーチンを実行
			var helperGo = new GameObject("[Playcaller_ScreenshotHelper]");
			helperGo.hideFlags = HideFlags.HideAndDontSave;
			var helper = helperGo.AddComponent<ScreenshotCoroutineHelper>();

			helper.StartCoroutine(CaptureCoroutine(tcs, commandId, width, height, filename, helperGo));

			return tcs.Task;
		}

		private static IEnumerator CaptureCoroutine(
			TaskCompletionSource<string> tcs, string commandId,
			int width, int height, string filename, GameObject helperGo)
		{
			yield return new WaitForEndOfFrame();

			try
			{
				Texture2D finalTexture;
				int captureWidth, captureHeight;

				if (width > 0 && height > 0)
				{
					// 指定解像度で正確にキャプチャ (Unity Recorder と同じ API)
					// CaptureScreenshotIntoRenderTexture は RenderTexture の解像度で
					// Game View 全体（UI 含む）をレンダリングする
					int tw = Mathf.Clamp(width, 1, 8192);
					int th = Mathf.Clamp(height, 1, 8192);
					var rt = new RenderTexture(tw, th, 24);
					ScreenCapture.CaptureScreenshotIntoRenderTexture(rt);

					// CaptureScreenshotIntoRenderTexture はプラットフォームによって
					// 水平反転した結果を返す場合がある。Blit で補正する。
					var correctedRT = RenderTexture.GetTemporary(tw, th, 0);
					Graphics.Blit(rt, correctedRT, new Vector2(-1, 1), new Vector2(1, 0));

					RenderTexture.active = correctedRT;
					finalTexture = new Texture2D(tw, th, TextureFormat.RGB24, false);
					finalTexture.ReadPixels(new Rect(0, 0, tw, th), 0, 0);
					finalTexture.Apply();

					RenderTexture.active = null;
					RenderTexture.ReleaseTemporary(correctedRT);
					rt.Release();
					UnityEngine.Object.Destroy(rt);

					captureWidth = tw;
					captureHeight = th;
				}
				else
				{
					// デフォルト: Game View の現在の解像度でキャプチャ
					finalTexture = ScreenCapture.CaptureScreenshotAsTexture();
					if (finalTexture == null)
					{
						tcs.TrySetResult(PlaycallerResponse.Error(commandId,
							"ScreenCapture.CaptureScreenshotAsTexture() returned null", "CAPTURE_ERROR"));
						yield break;
					}
					captureWidth = finalTexture.width;
					captureHeight = finalTexture.height;
				}

				byte[] imageBytes = finalTexture.EncodeToPNG();
				UnityEngine.Object.Destroy(finalTexture);

				if (imageBytes == null || imageBytes.Length == 0)
				{
					tcs.TrySetResult(PlaycallerResponse.Error(commandId,
						"Failed to encode screenshot", "ENCODE_ERROR"));
					yield break;
				}

				string filePath = SaveToFile(imageBytes, filename);
				tcs.TrySetResult(MakeSuccessResponse(commandId, filePath, captureWidth, captureHeight));
			}
			catch (Exception ex)
			{
				tcs.TrySetResult(PlaycallerResponse.Error(commandId,
					$"Screenshot capture failed: {ex.Message}", "SCREENSHOT_ERROR"));
			}
			finally
			{
				if (helperGo != null)
				{
					UnityEngine.Object.Destroy(helperGo);
				}
			}
		}

		/// <summary>
		/// Play Mode 外: camera.Render() + RenderTexture 方式でキャプチャ。
		/// Screen Space Overlay UI は映らないが、Editor 非再生時のフォールバックとして使用。
		/// </summary>
		private static string CaptureWithCamera(string commandId, int width, int height, string filename)
		{
			Camera camera = Camera.main;
			if (camera == null)
			{
				var cameras = Camera.allCameras;
				if (cameras.Length > 0)
					camera = cameras[0];
			}

			if (camera == null)
			{
				return PlaycallerResponse.Error(commandId,
					"No camera available for screenshot", "NO_CAMERA");
			}

			int captureWidth = width > 0 ? width : camera.pixelWidth;
			int captureHeight = height > 0 ? height : camera.pixelHeight;

			// Clamp to reasonable size
			captureWidth = Mathf.Clamp(captureWidth, 1, 4096);
			captureHeight = Mathf.Clamp(captureHeight, 1, 4096);

			// Create RenderTexture and capture
			var renderTexture = new RenderTexture(captureWidth, captureHeight, 24);
			var previousTarget = camera.targetTexture;

			camera.targetTexture = renderTexture;
			camera.Render();

			RenderTexture.active = renderTexture;
			var screenshot = new Texture2D(captureWidth, captureHeight, TextureFormat.RGB24, false);
			screenshot.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0);
			screenshot.Apply();

			camera.targetTexture = previousTarget;
			RenderTexture.active = null;

			byte[] imageBytes = screenshot.EncodeToPNG();

			UnityEngine.Object.DestroyImmediate(renderTexture);
			UnityEngine.Object.DestroyImmediate(screenshot);

			if (imageBytes == null || imageBytes.Length == 0)
			{
				return PlaycallerResponse.Error(commandId,
					"Failed to encode screenshot", "ENCODE_ERROR");
			}

			string filePath = SaveToFile(imageBytes, filename);
			return MakeSuccessResponse(commandId, filePath, captureWidth, captureHeight);
		}

		/// <summary>
		/// GameView の内部 RenderTexture (m_RenderTexture) を直接読み取ってキャプチャする。
		/// Screen Space Overlay Canvas UI を含む GameView 全体が取得できる。
		/// ScreenCapture API を使わないため、HDR 有効時でもハングしない。
		/// </summary>
		public static string HandleReadGameViewPixels(PlaycallerCommand command)
		{
			try
			{
				string filename = null;
				if (command.Params != null)
				{
					filename = command.Params["filename"]?.ToString();
				}

				var gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
				if (gameViewType == null)
				{
					return PlaycallerResponse.Error(command.Id,
						"GameView type not found", "GAMEVIEW_ERROR");
				}

				var gameView = EditorWindow.GetWindow(gameViewType, false, null, false);
				if (gameView == null)
				{
					return PlaycallerResponse.Error(command.Id,
						"GameView window not found", "GAMEVIEW_ERROR");
				}

				var rtField = gameViewType.GetField("m_RenderTexture",
					BindingFlags.NonPublic | BindingFlags.Instance);
				if (rtField == null)
				{
					return PlaycallerResponse.Error(command.Id,
						"m_RenderTexture field not found", "GAMEVIEW_ERROR");
				}

				var renderTexture = rtField.GetValue(gameView) as RenderTexture;
				if (renderTexture == null)
				{
					return PlaycallerResponse.Error(command.Id,
						"m_RenderTexture is null (GameView may not be visible)",
						"GAMEVIEW_ERROR");
				}

				int w = renderTexture.width;
				int h = renderTexture.height;

				RenderTexture.active = renderTexture;
				var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
				tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
				tex.Apply();
				RenderTexture.active = null;

				// m_RenderTexture は上下反転しているため補正
				Color[] pixels = tex.GetPixels();
				Color[] flipped = new Color[pixels.Length];
				for (int y = 0; y < h; y++)
				{
					for (int x = 0; x < w; x++)
					{
						flipped[y * w + x] = pixels[(h - 1 - y) * w + x];
					}
				}
				tex.SetPixels(flipped);
				tex.Apply();

				byte[] imageBytes = tex.EncodeToPNG();
				UnityEngine.Object.DestroyImmediate(tex);

				if (imageBytes == null || imageBytes.Length == 0)
				{
					return PlaycallerResponse.Error(command.Id,
						"Failed to encode image", "ENCODE_ERROR");
				}

				string filePath = SaveToFile(imageBytes, filename);
				return MakeSuccessResponse(command.Id, filePath, w, h);
			}
			catch (Exception ex)
			{
				return PlaycallerResponse.Error(command.Id,
					$"GameView read pixels failed: {ex.Message}", "GAMEVIEW_ERROR");
			}
		}

		/// <summary>
		/// Texture2D を指定サイズにリサイズする。
		/// RenderTexture を使ったバイリニアフィルタリングによる高品質リサイズ。
		/// </summary>
		private static Texture2D ResizeTexture(Texture2D source, int targetWidth, int targetHeight)
		{
			var rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
			rt.filterMode = FilterMode.Bilinear;

			RenderTexture.active = rt;
			Graphics.Blit(source, rt);

			var result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGB24, false);
			result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
			result.Apply();

			RenderTexture.active = null;
			RenderTexture.ReleaseTemporary(rt);

			return result;
		}
	}

	/// <summary>
	/// ScreenCapture.CaptureScreenshotAsTexture() を WaitForEndOfFrame 後に呼ぶための
	/// 一時的な MonoBehaviour ヘルパー。Play Mode 中にのみ使用される。
	/// </summary>
	internal class ScreenshotCoroutineHelper : MonoBehaviour
	{
		// コルーチン実行用のダミー MonoBehaviour
		// ScreenshotHandler.CaptureWithScreenCapture() から生成され、キャプチャ完了後に破棄される
	}
}
