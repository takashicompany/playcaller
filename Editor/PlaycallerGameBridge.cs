namespace Playcaller.Editor
{
	/// <summary>
	/// ゲームが登録するクエリハンドラーのブリッジ。
	/// ゲーム固有コードが InitializeOnLoadMethod で QueryHandler を設定する。
	/// </summary>
	public static class PlaycallerGameBridge
	{
		/// <summary>
		/// ゲームが登録するクエリハンドラー。
		/// 引数: queryType (空文字列 = デフォルトクエリ)
		/// 戻り値: JSON文字列 (フォーマットはゲーム固有)
		/// null を返す場合はブリッジ未登録と同等。
		/// </summary>
		public static System.Func<string, string> QueryHandler;
	}
}
