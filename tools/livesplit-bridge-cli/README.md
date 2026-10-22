# LiveSplit.Bridge Debug CLI

Python CLIでLiveSplit.Bridge JSON Protocol v4のRPCとEventsを確認できます。Python 3.14以降と
[uv](https://docs.astral.sh/uv/)を使用します。

## セットアップ

```powershell
cd tools/livesplit-bridge-cli
uv sync --locked
```

LiveSplitでBridge Componentを有効にし、CLIと同じWebSocket portを設定してください。既定値は
`54000`です。`--port`、`LIVESPLIT_BRIDGE_WEBSOCKET_PORT`で接続先ポート、`--timeout`でRPCの
タイムアウト秒数を指定できます。`--port`は環境変数より優先され、タイムアウトの既定値は3秒です。

## コマンド

`--json`はResponseまたはEvent全体を整形して出力します。通常表示は概要を読みやすい形式で出力し、
Protocol v4の小文字Enum表記を使います。

```powershell
uv run livesplit-bridge timer-state
uv run livesplit-bridge run
uv run livesplit-bridge --json run-icons
uv run livesplit-bridge attempt
uv run livesplit-bridge context
uv run livesplit-bridge completed-count

uv run livesplit-bridge timer start
uv run livesplit-bridge timer split
uv run livesplit-bridge timer skip
uv run livesplit-bridge timer undo
uv run livesplit-bridge timer reset
uv run livesplit-bridge timer pause
uv run livesplit-bridge timer resume

uv run livesplit-bridge game-time initialize
uv run livesplit-bridge game-time set 12.345
uv run livesplit-bridge game-time pause
uv run livesplit-bridge game-time resume

uv run livesplit-bridge events --count 5
uv run livesplit-bridge --port 54001 --timeout 5 --json timer-state
```

Game Timeは100ns単位ticksへ変換します。10進秒の丸めはPythonの`round()`と同じties-to-evenで、
符号付き64bit範囲外の値は接続前に拒否します。RPC timeoutや切断時はControl操作を自動再送しません。

`events`はEvent種別、sequence、存在するTimerStateを表示します。Session変更や同一Session内の
sequence不連続は標準エラーに警告します。イベント履歴の再取得や状態の自動再同期は行いません。
Bridge削除などによる切断は未処理例外にせず、説明付きエラーと終了コード1で報告します。

BridgeはQueryとEventsを外部Originにも許可しますが、Control RPCは接続時のOrigin制限に従います。

`run`に画像は含まれません。`run-icons`は現在のRunのゲーム・Segmentアイコンを一括取得します。
通常表示はPNGの有無、`--json`はBase64 PNG文字列または`null`を出力します。Segment順は取得時の
Runに従い、`run`との間のRun切り替えやアイコンの変更通知・キャッシュは扱いません。

## 開発チェック

CLIは完全なSchema validatorとしては動作しません。応答の照合やEventのsequence追跡に必要な
envelopeを確認し、通常表示に必要な情報の不足は説明付きエラーとして報告します。通信モデル全体の
構造とC#実出力との適合性はSchema contract jobで確認します。

E2Eテストは`LiveSplit.Bridge.TestHost`から本番の`Component`を起動し、CLIと同じJSON v4
WebSocket endpointを使います。TestHostのビルドには、.NET SDK 10.0.302以降と
.NET Framework 4.8.1 Developer Packが必要です。Windows上でリポジトリルートから
依存サブモジュールを取得し、TestHostをビルドしてください。

```powershell
git submodule update --init --depth 1 external/LiveSplit
git -C external/LiveSplit submodule update --init --depth 1 lib/SpeedrunComSharp
dotnet build tests/LiveSplit.Bridge.TestHost/LiveSplit.Bridge.TestHost.csproj --configuration Release
```

続いて`tools/livesplit-bridge-cli`で実行します。

```powershell
uv sync --locked
uv run ruff check src tests
uv run ruff format --check src tests
uv run pytest
```
