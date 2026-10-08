# LiveSplit.Bridge Debug CLI

LiveSplit.Bridge の RPC とイベントストリームを確認するための Python CLI です。
Python 3.14以降を使用します。

## セットアップ

リポジトリルートから実行します。

```powershell
cd tools/livesplit-bridge-cli
uv sync
uv run python scripts/generate_proto.py
```

`generate_proto.py` は本体の
`proto` を入力として、実行時に必要な
`*_pb2.py` と型チェック用の `*_pb2.pyi` を同時に生成します。これらは生成物のため
Gitには含めません。`.proto` を変更した場合や、クリーンチェックアウト後には再生成して
ください。

品質チェックは次のコマンドで実行できます。

```powershell
uv run ruff check src tests scripts
uv run ty check src tests scripts
uv run pytest
```

## 自動テスト

`uv run pytest` はCLI自身のプロトコル非依存テストを実行します。Bridgeとの
統合テストは、CLIのv3対応が完了した後に追加します。GitHub Actionsでは.NET
テスト、CLIのlint、およびCLIテストをWindows環境で実行します。

通常の接続先は WebSocket の `ws://127.0.0.1:54000/bridge/v2/rpc` と
`ws://127.0.0.1:54000/bridge/v2/events` です。LiveSplit を起動し、レイアウトへ
`LiveSplit Bridge` コンポーネントを追加してから使ってください。

## 使用例

```powershell
uv run livesplit-bridge timer-state
uv run livesplit-bridge run
uv run livesplit-bridge attempt
uv run livesplit-bridge runtime
uv run livesplit-bridge timer start
uv run livesplit-bridge timer split
uv run livesplit-bridge game-time set 12.345
uv run livesplit-bridge events
uv run livesplit-bridge --json timer-state
```

接続先ポートはオプションまたは本体と同じ環境変数で変更できます。

```powershell
uv run livesplit-bridge --port 55000 timer-state
$env:LIVESPLIT_BRIDGE_WEBSOCKET_PORT = "55000"
uv run livesplit-bridge events
```

全コマンドは `uv run livesplit-bridge --help` で確認できます。
