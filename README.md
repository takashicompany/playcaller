# PlayCaller

Unity Editor 上で動作するゲームに対して、座標ベースの入力シミュレーション（タップ・ドラッグ・フリック）を行う MCP サーバーです。

AI エージェントが Unity の Game View を「実際のプレイヤーのように」操作し、スクリーンショットで結果を確認できます。Web ブラウザにおける Playwright に相当する役割を Unity Editor で担います。

## アーキテクチャ

```
Claude Code (MCP Client)
    │  stdio
    ▼
Node.js MCP Server (Server~/)
    │  TCP 127.0.0.1:6500
    ▼
Unity Editor (Editor/)
    │  PlayCallerServer ([InitializeOnLoad])
    ▼
Game View (EventSystem / Physics Raycast)
```

- **Node.js MCP サーバー** (`Server~/`): MCP プロトコル (stdio) でクライアントと通信し、TCP で Unity に中継
- **Unity C# エディタスクリプト** (`Editor/`): TCP サーバーとして常駐し、入力シミュレーション・スクリーンショット取得などを実行
- **TCP プロトコル**: big-endian 4-byte length-prefix framing + JSON

## セットアップ

### 要件

- Unity 2022.3 以上
- Node.js 18 以上
- Newtonsoft.Json（Unity 側で使用。UPM `com.unity.nuget.newtonsoft-json` など）

### インストール

#### UPM パッケージとして（推奨）

`Packages/manifest.json` に追加:

```json
{
  "dependencies": {
    "com.takashicompany.playcaller": "git@github.com:takashicompany/playcaller.git"
  }
}
```

または `file:` 参照（git subtree で取り込んだ場合）:

```json
{
  "dependencies": {
    "com.takashicompany.playcaller": "file:../playcaller"
  }
}
```

#### Node.js サーバーのビルド

```bash
cd Server~
npm install
npm run build
```

### MCP クライアント設定

`.mcp.json` に追加:

```json
{
  "mcpServers": {
    "playcaller": {
      "command": "node",
      "args": ["<playcallerへのパス>/Server~/dist/index.js"]
    }
  }
}
```

## MCP ツール一覧

### playcaller_screenshot

Game View のスクリーンショットを取得します。

| パラメータ | 型 | デフォルト | 説明 |
|---|---|---|---|
| `width` | number | 0 (リサイズなし) | リサイズ後の幅 |
| `height` | number | 0 (リサイズなし) | リサイズ後の高さ |

**戻り値**: base64 PNG 画像 + メタデータ (`width`, `height`, `screenWidth`, `screenHeight`)

- Play Mode 中: `ScreenCapture.CaptureScreenshotAsTexture()` で UI を含むキャプチャ
- Play Mode 外: `Camera.Render()` + RenderTexture によるフォールバック

### playcaller_tap

スクリーンショット座標でタップを実行します。

| パラメータ | 型 | デフォルト | 説明 |
|---|---|---|---|
| `x` | number | (必須) | 左端からのピクセル数 |
| `y` | number | (必須) | 上端からのピクセル数 |
| `holdDurationMs` | number | 0 | 長押し時間 (最大 10000) |

**イベント順序**: pointerEnter → pointerDown → (hold) → pointerUp → pointerClick → pointerExit

**ターゲット判定の優先順位**:
1. UI (GraphicRaycaster)
2. 2D Physics (Physics2D.Raycast)
3. 3D Physics (Physics.Raycast)

### playcaller_drag

ある座標から別の座標へドラッグを実行します。

| パラメータ | 型 | デフォルト | 説明 |
|---|---|---|---|
| `fromX` | number | (必須) | 開始 X |
| `fromY` | number | (必須) | 開始 Y |
| `toX` | number | (必須) | 終了 X |
| `toY` | number | (必須) | 終了 Y |
| `durationMs` | number | 300 | ドラッグ時間 (最大 10000) |
| `steps` | number | 10 | 中間ポイント数 (2-100) |

**イベント順序**: pointerDown → beginDrag → drag (複数回) → endDrag → pointerUp

### playcaller_flick

フリック（クイックスワイプ）を実行します。

| パラメータ | 型 | デフォルト | 説明 |
|---|---|---|---|
| `fromX` | number | (必須) | 開始 X |
| `fromY` | number | (必須) | 開始 Y |
| `dx` | number | (必須) | 水平オフセット (正=右) |
| `dy` | number | (必須) | 垂直オフセット (正=下) |
| `durationMs` | number | 150 | フリック時間 (最大 5000) |

### playcaller_playmode

Unity Play Mode を制御します。

| パラメータ | 型 | 説明 |
|---|---|---|
| `action` | enum | `play` / `pause` / `stop` / `get_state` |

**Play Mode 切り替え時の動作**: play/stop はドメインリロードを引き起こし TCP が切断されます。Node.js 側が最大 15 秒間自動再接続を試行し、復帰後にレスポンスを返します。

### playcaller_wait

指定期間の待機を行います。

| パラメータ | 型 | デフォルト | 説明 |
|---|---|---|---|
| `ms` | number | - | ミリ秒 (最大 30000) |
| `frames` | number | - | フレーム数 (最大 600) |

両方指定した場合は `frames` が優先されます。

### playcaller_console_log

Unity コンソールログを取得します。

| パラメータ | 型 | デフォルト | 説明 |
|---|---|---|---|
| `count` | number | 50 | 取得件数 (最大 200) |
| `level` | enum | `all` | `all` / `log` / `warning` / `error` |
| `clear` | boolean | false | 取得後にクリアするか |

メモリ上に最大 1000 件のログを保持します。

## 座標系

全ての入力座標は **スクリーンショット画像の座標系** を使用します:

- **原点**: 左上
- **X 軸**: 右方向が正
- **Y 軸**: 下方向が正

内部でスクリーンショット解像度と Game View 解像度の比率からスケーリング変換し、Unity の画面座標（左下原点、Y 上向き）に変換します。

```
スクリーンショット座標 (x, y)
  ↓ scaleX = screenWidth / screenshotWidth
  ↓ scaleY = screenHeight / screenshotHeight
Unity 座標 (x * scaleX, screenHeight - y * scaleY)
```

## 典型的な使用フロー

```
1. playcaller_playmode(action: "play")     -- Play Mode 開始
2. playcaller_screenshot()                  -- 画面確認
3. playcaller_tap(x: 164, y: 280)          -- ボタンタップ
4. playcaller_wait(ms: 500)                -- アニメーション待ち
5. playcaller_screenshot()                  -- 結果確認
6. playcaller_playmode(action: "stop")     -- Play Mode 終了
```

## ディレクトリ構成

```
playcaller/
├── package.json              # UPM パッケージ定義
├── Editor/                   # Unity C# (Editor only)
│   ├── PlayCaller.Editor.asmdef
│   ├── PlayCallerServer.cs   # TCP サーバー (127.0.0.1:6500)
│   ├── CommandRouter.cs      # コマンドディスパッチ
│   ├── Models/
│   │   ├── PlayCallerCommand.cs
│   │   └── PlayCallerResponse.cs
│   └── Handlers/
│       ├── InputSimulationHandler.cs  # tap/drag/flick
│       ├── ScreenshotHandler.cs       # スクリーンショット取得
│       ├── PlayModeHandler.cs         # Play Mode 制御
│       ├── WaitHandler.cs             # 待機
│       └── ConsoleLogHandler.cs       # コンソールログ
└── Server~/                  # Node.js MCP サーバー (~ = Unity 無視)
    ├── package.json
    ├── tsconfig.json
    └── src/
        ├── index.ts           # エントリポイント (stdio transport)
        ├── server.ts          # MCP サーバー + ツール登録
        ├── unity-connection.ts # TCP クライアント + 再接続
        └── tools/
            ├── tap.ts
            ├── drag.ts
            ├── flick.ts
            ├── screenshot.ts
            ├── playmode.ts
            ├── wait.ts
            └── console-log.ts
```

## License

MIT
