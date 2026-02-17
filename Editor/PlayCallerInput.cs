using System.Collections.Generic;
using UnityEngine;

namespace Playcaller.Editor
{
	/// <summary>
	/// PlayCaller MCP からのキー入力状態を管理する静的クラス。
	/// Enqueue で登録し、ConsumeKey で1回だけ取得（消費）する。
	/// </summary>
	public static class PlayCallerInput
	{
		private static readonly HashSet<KeyCode> _pressedKeys = new HashSet<KeyCode>();

		/// <summary>キー押下を登録（ハンドラーから呼ばれる）</summary>
		public static void Enqueue(KeyCode key)
		{
			_pressedKeys.Add(key);
		}

		/// <summary>キーが押されていれば true を返し、消費する（1回だけ反応）</summary>
		public static bool ConsumeKey(KeyCode key)
		{
			return _pressedKeys.Remove(key);
		}
	}
}
