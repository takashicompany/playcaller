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

## アップデート

### Unity パッケージ

1. **Window > Package Manager** を開く
2. リストから **Playcaller** を選択
3. **Update** をクリック

### MCP サーバー（PyPI）

```sh
uvx --upgrade playcaller
```

## 注意事項

### Run In Background

MCP経由でPlay Modeを開始した後、ゲームが進行しない場合は「Run In Background」が無効になっている可能性があります。**Edit > Project Settings > Player > Resolution and Presentation > Run In Background** を有効にするか、ゲームコード内で `Application.runInBackground = true` を設定してください。

### HDR プロジェクト

URP/HDRP で HDR が有効なプロジェクトでは、`playcaller_screenshot` が [Unity の既知の問題](https://discussions.unity.com/t/screencapture-capturescreenshot-fails-when-hdr-is-enabled/896701) によりハングすることがあります。代わりに `playcaller_read_gameview_pixels` を使用してください。GameView の内部バッファを直接読み取るため、HDR 環境でも動作します。

## MCP ツール一覧

| ツール名 | 説明 |
|---|---|
| `playcaller_screenshot` | Game View のスクリーンショットを取得 |
| `playcaller_read_gameview_pixels` | GameView の内部バッファを読み取ってキャプチャ（HDR対応） |
| `playcaller_tap` | 指定座標をタップ |
| `playcaller_multi_tap` | 複数のタップを連続実行 |
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
| `playcaller_game_query` | 実行中のゲームにカスタムクエリを送信 |
| `playcaller_set_game_view_size` | Game View の解像度を設定 |
