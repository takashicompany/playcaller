using System;
using UnityEngine;
using Playcaller.Editor.Models;

namespace Playcaller.Editor.Handlers
{
	/// <summary>
	/// MCP からのキー入力コマンドを処理するハンドラー。
	/// 受信したキーを PlayCallerInput に登録し、ゲームコードから参照可能にする。
	/// </summary>
	public static class KeyInputHandler
	{
		public static string Handle(PlaycallerCommand command)
		{
			try
			{
				string keyName = command.Params?["key"]?.ToString();
				if (string.IsNullOrEmpty(keyName))
					return PlaycallerResponse.Error(command.Id, "Missing 'key' parameter", "MISSING_PARAM");

				if (!Enum.TryParse<KeyCode>(keyName, true, out var keyCode))
					return PlaycallerResponse.Error(command.Id, $"Unknown key: {keyName}", "UNKNOWN_KEY");

				PlayCallerInput.Enqueue(keyCode);

				return PlaycallerResponse.Success(command.Id, new
				{
					key = keyCode.ToString(),
					enqueued = true
				});
			}
			catch (Exception ex)
			{
				return PlaycallerResponse.Error(command.Id, $"Key press failed: {ex.Message}", "KEY_ERROR");
			}
		}
	}
}
