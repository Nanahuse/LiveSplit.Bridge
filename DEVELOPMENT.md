# LiveSplit.Bridge 本体開発ガイド

この文書は、LiveSplit.Bridgeコンポーネント本体を開発する方向けです。

- LiveSplitでコンポーネントを利用する場合は[`README.md`](README.md)
- Bridgeへ接続するクライアントを実装する場合は
  [`CLIENT_DEVELOPMENT.md`](CLIENT_DEVELOPMENT.md)

を参照してください。

## 対象環境

LiveSplit.BridgeはWindows上のLiveSplit 1.8.37を対象とし、.NET Framework 4.8.1向けに
ビルドします。`external/LiveSplit` submoduleは、対象リリースのコミット
`683cd60a9f3fe2dc31a5054c6d10f5d71f2e6ae1`に固定しています。

開発には次の環境が必要です。

- Git（submodule対応）
- .NET Framework 4.8.1をビルドできる.NET SDK
- Python 3.14以降
- [uv](https://docs.astral.sh/uv/)

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

新しくcloneした後、Bridgeのビルドに必要なsubmoduleだけを取得します。

```powershell
git clone https://github.com/Nanahuse/LiveSplit.Bridge.git
cd LiveSplit.Bridge
git submodule update --init --depth 1 external/LiveSplit
git -C external/LiveSplit submodule update --init --depth 1 lib/SpeedrunComSharp
```

既存のcloneでも同じ2つの`git submodule update`を実行してください。LiveSplit配下のその他の
コンポーネントと`lib/livesplit-core`は、Bridgeのビルドには使用しません。

テストで使用するPython CLIの環境を準備します。

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

## 実装上の注意

- Protobufの既存field番号やenum値を再利用しないでください。
- PUBソケットと`event_sequence`は単一publisherループだけで操作してください。
- snapshot生成でLiveSplitのUIスレッドを待つ間、publisher側の排他制御を保持しないで
  ください。
- `state_revision`は実際のLiveSplit状態変更イベントで1回だけ増加させ、失敗操作やno-op、
  ハートビート、状態が変わらない定期snapshotでは増加させないでください。
- LiveSplitの対象バージョンを変更する場合は、submoduleの固定コミットとREADMEの対応環境を
  同時に更新してください。

通信方式やスレッドモデルを含む内部設計は
[`docs/implementation-plan.md`](docs/implementation-plan.md)を参照してください。

## バージョン管理方針

### 製品バージョン

LiveSplit.Bridgeのリリースバージョンは、`vMAJOR.MINOR.PATCH`形式のGit tagを正とし、
Semantic Versioningに従います。tagと配布ZIPのバージョンは必ず一致させます。

安定版となる`v1.0.0`以降の増分基準は次のとおりです。

| 変更 | 増分 | 例 |
|---|---|---|
| 後方互換性を壊す変更 | MAJOR | 既存設定の非互換変更、既存クライアントが動作しなくなる変更 |
| 後方互換性を保つ機能追加 | MINOR | 新しい操作、イベント、設定の追加 |
| 後方互換性を保つ修正 | PATCH | 不具合修正、内部改善、文書やビルドの修正 |

`v0.x`は安定版前として扱います。この期間も互換性への影響を明示し、次の基準で更新します。

- 既存利用者やクライアントの変更を必要とする非互換変更: MINOR
- 後方互換性のある機能追加: MINOR
- 後方互換性のある修正、文書、CI、パッケージ変更: PATCH

複数種類の変更を含む場合は、最も大きい増分を採用します。すべてのmergeでreleaseを作る必要は
ありませんが、releaseを作る際は前回release以降の全変更から次のversionを決定します。

### プロトコルバージョン

製品バージョンと`protocol_version`は別に管理します。製品のPATCHやMINORを更新しても、
wire protocolに互換性があれば`protocol_version`は変更しません。

次のような追加は、既存クライアントが未知のfieldやenum値を安全に扱える限り、原則として
後方互換変更です。

- 新しいoptional fieldの追加
- 新しいRequest、Response、イベント種別の追加
- 既存fieldの意味を変えない新機能の追加

次のような変更はprotocolの非互換変更です。

- 既存field番号やenum値の削除、再利用、型変更
- 既存fieldやイベントの意味・必須条件の変更
- 既存のRPC順序や状態同期の前提を壊す変更

非互換変更では既存の`v1`スキーマを変更せず、新しいpackageとディレクトリ
（例: `livesplit.bridge.v2`、`proto/livesplit/bridge/v2`）を追加し、`protocol_version`も
更新します。対応するクライアントガイド、テスト、移行方法を同じpull requestで更新して
ください。

### tagとリリース履歴

- 公開releaseのtagは`vX.Y.Z`形式とし、先頭ゼロを付けません。
- push済みのtagと公開済みの成果物は上書きしません。
- release失敗や公開後の修正では、新しいversionを採番します。
- 互換性への影響と必要な移行作業をrelease notesへ記載します。
- 対応LiveSplitバージョンを変更した場合は、README、submodule固定先、release notesを同時に
  更新します。

## リリース

リリースは[`.github/workflows/release.yml`](.github/workflows/release.yml)で自動化されています。
`v*`形式のtagをpushすると、次の処理が実行されます。

1. submoduleとPython環境を復元する
2. Protobufコードを生成する
3. Ruffのlintとformat検査を実行する
4. Release構成の.NETテストを実行する
5. Python単体テストとBridge E2Eテストを実行する
6. コンポーネント一式をZIPへまとめる
7. ZIPのSHA-256チェックサムを生成する
8. GitHub Releaseとリリースノートを作成する

リリース前に、対象の変更が`main`へマージされ、`main`のCIが成功していることを確認します。
最新の`main`から新しいバージョンのtagを作成してpushしてください。

```powershell
git switch main
git pull --ff-only
git tag vX.Y.Z
git push origin vX.Y.Z
```

workflowが成功すると、GitHub Releaseに次のファイルが添付されます。

- `LiveSplit.Bridge-vX.Y.Z.zip`
- `SHA256SUMS.txt`

ZIP名とtag、チェックサムの対象ファイル名、リリースノートの内容を確認してください。
workflowが失敗した場合は通常のpull requestで原因を修正し、同じtagを上書きせず、修正後に
新しいバージョンのtagを作成します。

## 関連ドキュメント

- クライアント開発: [`CLIENT_DEVELOPMENT.md`](CLIENT_DEVELOPMENT.md)
- デバッグCLI: [`tools/livesplit-bridge-cli/README.md`](tools/livesplit-bridge-cli/README.md)
