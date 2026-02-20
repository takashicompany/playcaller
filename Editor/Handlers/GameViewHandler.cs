using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Playcaller.Editor.Models;

namespace Playcaller.Editor.Handlers
{
	/// <summary>
	/// Game View の解像度をプログラム的に設定するハンドラー。
	/// Unity Recorder と同じ方式で GameViewSizes 内部 API をリフレクション経由で操作する。
	/// </summary>
	public static class GameViewHandler
	{
		private static readonly Type GameViewType;
		private static readonly Type GameViewSizesType;
		private static readonly Type GameViewSizeType;
		private static readonly Type GameViewSizeTypeEnum;

		static GameViewHandler()
		{
			var assembly = typeof(UnityEditor.Editor).Assembly;
			GameViewType = assembly.GetType("UnityEditor.GameView");
			GameViewSizesType = assembly.GetType("UnityEditor.GameViewSizes");
			GameViewSizeType = assembly.GetType("UnityEditor.GameViewSize");
			GameViewSizeTypeEnum = assembly.GetType("UnityEditor.GameViewSizeType");
		}

		public static string Handle(PlaycallerCommand command)
		{
			try
			{
				int width = command.Params?["width"]?.ToObject<int>() ?? 0;
				int height = command.Params?["height"]?.ToObject<int>() ?? 0;

				if (width <= 0 || height <= 0)
					return PlaycallerResponse.Error(command.Id,
						"width and height must be positive integers", "INVALID_PARAMS");

				SetGameViewSize(width, height);

				return PlaycallerResponse.Success(command.Id, new
				{
					width,
					height,
					message = $"Game View resolution set to {width}x{height}. " +
					          "The change takes effect on the next frame."
				});
			}
			catch (Exception ex)
			{
				return PlaycallerResponse.Error(command.Id,
					$"Failed to set Game View size: {ex.Message}", "GAME_VIEW_ERROR");
			}
		}

		/// <summary>
		/// Game View を指定解像度に設定する（MenuItem 等から直接呼べるようにパブリック公開）。
		/// </summary>
		public static void SetGameViewSize(int width, int height)
		{
			// GameViewSizes singleton を取得
			var singletonType = typeof(ScriptableSingleton<>).MakeGenericType(GameViewSizesType);
			var instance = singletonType.GetProperty("instance").GetValue(null);

			// 現在のグループタイプ（Standalone, iOS, Android 等）を取得
			var currentGroupTypeProp = GameViewSizesType.GetProperty("currentGroupType");
			var groupType = currentGroupTypeProp.GetValue(instance);

			// グループを取得
			var getGroup = GameViewSizesType.GetMethod("GetGroup");
			var group = getGroup.Invoke(instance, new object[] { groupType });

			// 既存のカスタムサイズを検索、なければ追加
			int index = FindSizeIndex(group, width, height);
			if (index == -1)
			{
				AddCustomSize(group, width, height);
				index = FindSizeIndex(group, width, height);
			}

			if (index == -1)
				throw new Exception($"Failed to register custom Game View size {width}x{height}");

			// GameView の選択中サイズインデックスを変更
			var gameView = EditorWindow.GetWindow(GameViewType);
			var selectedSizeIndexProp = GameViewType.GetProperty(
				"selectedSizeIndex",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			selectedSizeIndexProp.SetValue(gameView, index);
			gameView.Repaint();
		}

		private static int FindSizeIndex(object group, int width, int height)
		{
			var getBuiltinCount = group.GetType().GetMethod("GetBuiltinCount");
			var getCustomCount = group.GetType().GetMethod("GetCustomCount");
			var getGameViewSize = group.GetType().GetMethod("GetGameViewSize");

			int builtinCount = (int)getBuiltinCount.Invoke(group, null);
			int customCount = (int)getCustomCount.Invoke(group, null);

			for (int i = builtinCount; i < builtinCount + customCount; i++)
			{
				var size = getGameViewSize.Invoke(group, new object[] { i });
				int w = (int)size.GetType().GetProperty("width").GetValue(size);
				int h = (int)size.GetType().GetProperty("height").GetValue(size);
				if (w == width && h == height)
					return i;
			}
			return -1;
		}

		private static void AddCustomSize(object group, int width, int height)
		{
			// GameViewSizeType.FixedResolution = 1
			var ctor = GameViewSizeType.GetConstructor(new Type[]
			{
				GameViewSizeTypeEnum, typeof(int), typeof(int), typeof(string)
			});
			var newSize = ctor.Invoke(new object[]
			{
				1, width, height, $"Playcaller {width}x{height}"
			});

			var addCustomSize = group.GetType().GetMethod("AddCustomSize");
			addCustomSize.Invoke(group, new object[] { newSize });
		}

		// -- Menu Items (MCP 再起動なしで execute_menu_item から呼べる) --

		[MenuItem("Playcaller/Game View/iPhone 5.5 inch (1242x2208)")]
		static void SetIPhone55() => SetGameViewSize(1242, 2208);

		[MenuItem("Playcaller/Game View/iPhone 6.5 inch (1242x2688)")]
		static void SetIPhone65() => SetGameViewSize(1242, 2688);

		[MenuItem("Playcaller/Game View/iPad (2048x2732)")]
		static void SetIPad() => SetGameViewSize(2048, 2732);
	}
}
