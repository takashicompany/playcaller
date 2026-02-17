using System;
using System.Collections;
using System.IO;
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

		/// <summary>PNG を Temp/Playcaller/Screenshots/screenshot.png に保存し、絶対パスを返す。</summary>
		static string SaveToFile(byte[] imageBytes)
		{
			Directory.CreateDirectory(ScreenshotDir);
			string filePath = Path.Combine(ScreenshotDir, ScreenshotFileName);
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

				if (command.Params != null)
				{
					width = command.Params["width"]?.ToObject<int>() ?? 0;
					height = command.Params["height"]?.ToObject<int>() ?? 0;
				}

				// Play Mode 中は ScreenCapture を使って UI を含む完全なゲーム画面をキャプチャ
				if (Application.isPlaying)
				{
					return CaptureWithScreenCapture(command.Id, width, height);
				}
				else
				{
					return CaptureWithCamera(command.Id, width, height);
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
		private static Task<string> CaptureWithScreenCapture(string commandId, int width, int height)
		{
			var tcs = new TaskCompletionSource<string>();

			// Play Mode 中なので MonoBehaviour コルーチンが使える
			// 一時的な GameObject + MonoBehaviour を作成してコルーチンを実行
			var helperGo = new GameObject("[Playcaller_ScreenshotHelper]");
			helperGo.hideFlags = HideFlags.HideAndDontSave;
			var helper = helperGo.AddComponent<ScreenshotCoroutineHelper>();

			helper.StartCoroutine(CaptureCoroutine(tcs, commandId, width, height, helperGo));

			return tcs.Task;
		}

		private static IEnumerator CaptureCoroutine(
			TaskCompletionSource<string> tcs, string commandId,
			int width, int height, GameObject helperGo)
		{
			// フレームレンダリング完了まで待機
			// Unity 公式ドキュメントで CaptureScreenshotAsTexture は WaitForEndOfFrame 後に呼ぶことが推奨されている
			yield return new WaitForEndOfFrame();

			try
			{
				Texture2D screenshot = ScreenCapture.CaptureScreenshotAsTexture();

				if (screenshot == null)
				{
					tcs.TrySetResult(PlaycallerResponse.Error(commandId,
						"ScreenCapture.CaptureScreenshotAsTexture() returned null", "CAPTURE_ERROR"));
					yield break;
				}

				int captureWidth = screenshot.width;
				int captureHeight = screenshot.height;

				// リサイズが指定されている場合
				bool needsResize = (width > 0 && width != captureWidth) || (height > 0 && height != captureHeight);
				Texture2D finalTexture = screenshot;

				if (needsResize)
				{
					int targetWidth = width > 0 ? Mathf.Clamp(width, 1, 4096) : captureWidth;
					int targetHeight = height > 0 ? Mathf.Clamp(height, 1, 4096) : captureHeight;

					finalTexture = ResizeTexture(screenshot, targetWidth, targetHeight);
					captureWidth = targetWidth;
					captureHeight = targetHeight;

					// 元のスクリーンショットを破棄
					UnityEngine.Object.Destroy(screenshot);
				}

				byte[] imageBytes = finalTexture.EncodeToPNG();
				UnityEngine.Object.Destroy(finalTexture);

				if (imageBytes == null || imageBytes.Length == 0)
				{
					tcs.TrySetResult(PlaycallerResponse.Error(commandId,
						"Failed to encode screenshot", "ENCODE_ERROR"));
					yield break;
				}

				string filePath = SaveToFile(imageBytes);
				tcs.TrySetResult(MakeSuccessResponse(commandId, filePath, captureWidth, captureHeight));
			}
			catch (Exception ex)
			{
				tcs.TrySetResult(PlaycallerResponse.Error(commandId,
					$"Screenshot capture failed: {ex.Message}", "SCREENSHOT_ERROR"));
			}
			finally
			{
				// ヘルパー GameObject を破棄
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
		private static string CaptureWithCamera(string commandId, int width, int height)
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

			string filePath = SaveToFile(imageBytes);
			return MakeSuccessResponse(commandId, filePath, captureWidth, captureHeight);
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
