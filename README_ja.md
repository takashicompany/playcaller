# Playcaller

AIがUnity上で動作するゲームを、実際のプレイヤーのように操作・確認できるMCPサーバーです。
スクリーンショットの取得、タップ・ドラッグ・フリックなどの入力シミュレーション、Play Modeの制御などをAIエージェントから行えます。

大まかに言うなら **Unity版の [Playwright MCP](https://github.com/anthropics/anthropic-quickstarts/tree/main/mcp-playwright)** です。

## できること

- Game View のスクリーンショット取得
- タップ・ドラッグ・フリック・キー入力のシミュレーション
- Play Mode の開始・停止・一時停止
- シーン内の GameObject 階層の取得・詳細確認
- コンソールログの取得
- AssetDatabase のリフレッシュ
- メニューアイテムの実行
- エディタの状態取得

## 苦手なこと

- アクション性の高いゲームの動作確認
- ゲーム体験の総合的な確認
- 視覚的な演出の評価

## 要件

- Unity 2022.3 以上
- Python 3.10 以上（[uv](https://docs.astral.sh/uv/) 推奨）

## 導入手順

### 1. Unity パッケージのインストール

1. Unity Editor のメニューバーから **Window > Package Manager** を開く
2. 左上の **+** ボタンをクリック
3. **Add package from git URL...** を選択
4. 以下の URL を入力して **Add** をクリック

```
https://github.com/takashicompany/playcaller.git
```

Newtonsoft.Json（`com.unity.nuget.newtonsoft-json`）が未導入の場合は自動的にインストールされます。

### 2. MCP サーバーの登録

以下を実行してください。

```sh
claude mcp add playcaller -- uvx playcaller
```

うまく動かない時はAIに相談しましょう。

### 3. 動作確認

Unity Editor を開いた状態で Claude Code を起動すると、自動的に接続されます。

## MCP ツール一覧

| ツール名 | 説明 |
|---|---|
| `playcaller_screenshot` | Game View のスクリーンショットを取得 |
| `playcaller_tap` | 指定座標をタップ |
| `playcaller_drag` | ドラッグ操作 |
| `playcaller_flick` | フリック（クイックスワイプ）操作 |
| `playcaller_key_press` | キーボード入力 |
| `playcaller_playmode` | Play Mode の開始・停止・一時停止 |
| `playcaller_wait` | 指定時間またはフレーム数の待機 |
| `playcaller_console_log` | Unity コンソールログの取得 |
| `playcaller_refresh` | AssetDatabase のリフレッシュ |
| `playcaller_get_hierarchy` | シーン内の GameObject 階層を取得 |
| `playcaller_get_gameobject` | 特定の GameObject の詳細情報を取得 |
| `playcaller_execute_menu_item` | Unity Editor のメニューアイテムを実行 |
| `playcaller_get_editor_state` | エディタの現在の状態を取得 |
