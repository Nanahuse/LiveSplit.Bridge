# LiveSplit.Bridge Debug CLI

Python CLIでLiveSplit.Bridge Protocol v3のRPCとEventsを確認できます。Python 3.14以降と
[uv](https://docs.astral.sh/uv/)を使用します。

## セットアップ

```powershell
cd tools/livesplit-bridge-cli
uv sync --locked
uv run python scripts/generate_proto.py
```

生成スクリプトはリポジトリのProtocol v3 `.proto`から実行時用の`*_pb2.py`と型チェック用の
`*_pb2.pyi`を作成します。生成物はGit管理されません。スクリプトを再実行すれば生成し直せます。

## コマンド

Bridgeの既定ポートは`54000`です。`--port`でポート、`--timeout`でRPC timeout秒数、`--json`で
受信ProtobufメッセージのJSON表示を指定できます。`LIVESPLIT_BRIDGE_WEBSOCKET_PORT`
環境変数も既定ポートを変更します。

```powershell
uv run livesplit-bridge timer-state
uv run livesplit-bridge run
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

`events`はEvent種別とsequenceを表示し、EventにTimerStateが含まれる場合はその値も表示
します。HeartbeatはProtocol v3にありません。RPCエラーや接続エラーでは内容を標準エラーへ
出力し、終了コード1を返します。

## 開発チェック

```powershell
uv run ruff check src tests scripts
uv run ruff format --check src tests scripts
uv run pytest
```
