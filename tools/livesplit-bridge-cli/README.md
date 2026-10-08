# LiveSplit.Bridge Debug CLI

LiveSplit.Bridge の RPC とイベントストリームを確認するための Python CLI です。
Python 3.14以降を使用します。

> [!NOTE]
> このDebug CLIは現在Protocol v2対応で、v3へ移行中です。v3 Runtimeへ移行した
> Bridgeとは互換性がありません。CLIのv3対応とBridgeとの統合テストは後続作業で
> 行います。現時点ではこのCLIをv3 Bridgeへの接続に使用できません。

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

