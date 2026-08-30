# LiveSplit.Bridge 開発ガイド

この文書は、LiveSplit.Bridge本体または接続クライアントを開発する方向けの情報を
まとめたものです。コンポーネントを利用するだけの場合は[`README.md`](README.md)を
参照してください。

## 対象環境

LiveSplit.BridgeはWindows上のLiveSplit 1.8.37を対象とし、.NET Framework 4.8.1向けに
ビルドします。`external/LiveSplit` submoduleは、対象リリースのコミット
`683cd60a9f3fe2dc31a5054c6d10f5d71f2e6ae1`に固定しています。

開発には次の環境が必要です。

- Git（submodule対応）
- .NET Framework 4.8.1をビルドできる.NET SDK
- Python 3.14以降
- [uv](https://docs.astral.sh/uv/)

CIとリリースはGitHub ActionsのWindows runnerで実行します。

## リポジトリ構成

| パス | 内容 |
|---|---|
| `src/LiveSplit.Bridge` | LiveSplitコンポーネント本体 |
| `src/LiveSplit.Bridge.Protocol` | Protobufから生成する.NETプロトコル型 |
| `proto/livesplit/bridge/v1` | RPC・イベントのProtobufスキーマ |
| `tests/LiveSplit.Bridge.Tests` | .NET単体テスト |
| `tests/LiveSplit.Bridge.TestHost` | E2Eテスト用のBridgeホスト |
| `tools/livesplit-bridge-cli` | Python製デバッグCLIとE2Eテスト |
| `external/LiveSplit` | 対象LiveSplitのsubmodule |
| `docs/implementation-plan.md` | 内部設計と実装方針 |

## セットアップ

新しくcloneする場合はsubmoduleも取得します。

```powershell
git clone --recurse-submodules https://github.com/Nanahuse/LiveSplit.Bridge.git
cd LiveSplit.Bridge
```

既存のcloneでは次のコマンドでsubmoduleを同期できます。

```powershell
git submodule update --init --recursive
```

Python CLIの依存関係をロックファイルどおりに復元し、Python用Protobufコードを生成します。

```powershell
cd tools/livesplit-bridge-cli
uv sync --locked
uv run python scripts/generate_proto.py
cd ../..
```

生成される`*_pb2.py`と`*_pb2.pyi`はGit管理対象外です。クリーンcheckout後、および
`.proto`を変更した後は再生成してください。.NET側のProtobufコードはビルド時に生成されます。

## ビルド

リポジトリルートでSolutionをビルドします。

```powershell
dotnet build LiveSplit.Bridge.slnx --nologo
```

コンポーネントのビルド成果物は、構成に応じて次のディレクトリへ配置されます。

```text
external/LiveSplit/bin/debug/Components
external/LiveSplit/bin/release/Components
```

Release構成だけをビルドする場合は次を実行します。

```powershell
dotnet build src/LiveSplit.Bridge/LiveSplit.Bridge.csproj --configuration Release --nologo
```

## テストと品質チェック

.NETテストはリポジトリルートで実行します。

```powershell
dotnet test LiveSplit.Bridge.slnx --nologo
```

Pythonのlint、format検査、型検査、単体・E2EテストはCLIディレクトリで実行します。

```powershell
cd tools/livesplit-bridge-cli
uv run python scripts/generate_proto.py
uv run ruff check src tests scripts
uv run ruff format --check src tests scripts
uv run ty check src tests scripts
uv run pytest
```

`uv run pytest`は.NETテストホストをビルドして起動し、実際のZeroMQ通信でCLIとBridgeを
接続します。LiveSplit本体を手動で起動する必要はありません。

GitHub ActionsのCIは、pull request、`main`へのpush、手動実行で次を検証します。

- Python用Protobufコードの生成
- Ruffによるlintとformat検査
- .NETテスト
- Python単体テストとBridge E2Eテスト

## 通信プロトコル

プロトコルの正本は[`proto/livesplit/bridge/v1`](proto/livesplit/bridge/v1)です。
既定ではローカルPC上の次のエンドポイントを使用します。

| 用途 | エンドポイント | 通信方式 |
|---|---|---|
| 状態取得・操作 | `tcp://127.0.0.1:54000` | ZeroMQ REQ/REP |
| イベント監視 | `tcp://127.0.0.1:54001` | ZeroMQ PUB/SUB |

コンポーネント設定のほか、開発・テスト時は環境変数
`LIVESPLIT_BRIDGE_RPC_ENDPOINT`と`LIVESPLIT_BRIDGE_EVENT_ENDPOINT`で接続先を
上書きできます。

現在の主な機能は次のとおりです。

- 現在のタイマー状態の取得
- Start、Split、Skip、Undo、Reset、Pause、Resume
- Game Timeの初期化、設定、Pause、Resume
- タイマー操作、Run変更、Game Time変更、定期snapshotのイベント配信

### イベント同期

状態変更イベントと定期フルsnapshotには、イベント処理後の`TimerSnapshot`が含まれます。
定期フルsnapshotは30秒周期です。

ハートビートは同じPUBストリームへ1秒周期で配信され、snapshotを含みません。
ハートビート自身は`event_sequence`を増加させず、最後に送信が確定したsequence対象
イベントの番号を通知します。

クライアントは次の場合に評価と操作送信を停止し、RPCで最新のフルsnapshotを取得して
再同期してください。

- ハートビートを3秒以上受信できない
- `event_sequence`の欠落を検出した
- `session_id`が変わった

欠落したイベントを推測して再現しないでください。Bridge内部の詳細な設計は
[`docs/implementation-plan.md`](docs/implementation-plan.md)を参照してください。

## デバッグCLI

Python CLIではsnapshot取得、タイマー・Game Time操作、イベント購読を手動確認できます。

```powershell
cd tools/livesplit-bridge-cli
uv run livesplit-bridge snapshot
uv run livesplit-bridge timer start
uv run livesplit-bridge timer split
uv run livesplit-bridge game-time set 12.345
uv run livesplit-bridge events
uv run livesplit-bridge --json snapshot
```

全コマンドと接続先の変更方法は
[`tools/livesplit-bridge-cli/README.md`](tools/livesplit-bridge-cli/README.md)を参照してください。

## 変更時の注意

- Protobufの既存field番号やenum値を再利用しないでください。
- PUBソケットと`event_sequence`は単一publisherループだけで操作してください。
- snapshot生成でLiveSplitのUIスレッドを待つ間、publisher側の排他制御を保持しないで
  ください。
- `state_revision`は実際のLiveSplit状態変更イベントで1回だけ増加させ、失敗操作やno-op、
  ハートビート、状態が変わらない定期snapshotでは増加させないでください。
- LiveSplitの対象バージョンを変更する場合は、submoduleの固定コミットとREADMEの対応環境を
  同時に更新してください。

## リリース

`.github/workflows/release.yml`は`v*`形式のtagがpushされたときに実行されます。
リリース前に対象コミットが`main`へマージされ、CIが成功していることを確認してください。

```powershell
git switch main
git pull --ff-only
git tag vX.Y.Z
git push origin vX.Y.Z
```

Release workflowはRelease構成のテストとE2Eテストを実行した後、次を自動作成します。

- `LiveSplit.Bridge-vX.Y.Z.zip`
- `SHA256SUMS.txt`
- GitHub Releaseと自動生成したリリースノート

workflowが失敗した場合は同じtagを上書きせず、原因を修正して新しいバージョンのtagを
作成してください。
