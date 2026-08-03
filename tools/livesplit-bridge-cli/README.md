# LiveSplit.Bridge Debug CLI

LiveSplit.Bridge の RPC とイベントストリームを確認するための Python CLI です。

## セットアップ

リポジトリルートから実行します。

```powershell
cd tools/livesplit-bridge-cli
uv sync
uv run python scripts/generate_proto.py
```

品質チェックは次のコマンドで実行できます。

```powershell
uv run ruff check src tests scripts
uv run ty check src tests scripts
uv run pytest
```

通常の接続先は RPC が `tcp://127.0.0.1:54000`、イベントが
`tcp://127.0.0.1:54001` です。LiveSplit を起動し、レイアウトへ
`LiveSplit Bridge` コンポーネントを追加してから使ってください。

## 使用例

```powershell
uv run livesplit-bridge snapshot
uv run livesplit-bridge timer start
uv run livesplit-bridge timer split
uv run livesplit-bridge game-time set 12.345
uv run livesplit-bridge events
uv run livesplit-bridge --json snapshot
```

接続先はオプションまたは本体と同じ環境変数で変更できます。

```powershell
uv run livesplit-bridge --rpc-endpoint tcp://127.0.0.1:55000 snapshot
$env:LIVESPLIT_BRIDGE_EVENT_ENDPOINT = "tcp://127.0.0.1:55001"
uv run livesplit-bridge events
```

全コマンドは `uv run livesplit-bridge --help` で確認できます。
