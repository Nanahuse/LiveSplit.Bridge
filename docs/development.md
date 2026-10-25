# Bridge本体の開発手順

Bridgeをビルドし、テスト、通信確認、配布ZIPの作成を行う手順です。
各コンポーネントの設計は[アーキテクチャ](architecture.md)、Schemaの保守は
[Schemaの生成・検証](schema-generation.md)を参照してください。

## 開発環境

| 用途 | 必要な環境 |
| --- | --- |
| Bridge・TestHost・.NETテスト | Windows、.NET SDK 10.0.302以降、.NET Framework 4.8.1 Developer Pack |
| コマンドの実行 | PowerShell、Git |
| Debug CLI・CLIテスト | Python 3.14以降、uv |

以下のコマンドはリポジトリルートのPowerShellで実行します。
言語設定とLiveSplitの出力パスは[Directory.Build.props](../Directory.Build.props)、
対象Frameworkと依存パッケージは各`.csproj`、CLIの依存関係は
[pyproject.toml](../tools/livesplit-bridge-cli/pyproject.toml)と`uv.lock`を参照してください。
Schema用の追加ツールは[schema-generation.md](schema-generation.md#必要なツール)にまとめています。

## 依存サブモジュール

```powershell
git submodule update --init --depth 1 external/LiveSplit
git -C external/LiveSplit submodule update --init --depth 1 lib/SpeedrunComSharp
```

ビルドに必要なLiveSplitとSpeedrunComSharpを取得します。サブモジュールはリポジトリに
記録されたコミットを使い、任意の最新バージョンへ更新しないでください。

## ビルドと.NETテスト

```powershell
dotnet restore LiveSplit.Bridge.slnx
dotnet build LiveSplit.Bridge.slnx --configuration Release --no-restore
dotnet test LiveSplit.Bridge.slnx --configuration Release --no-build --no-restore
```

ビルド後のBridge DLLは`src/LiveSplit.Bridge/bin/Release/net4.8.1/`にあります。
ビルドの`DeployToLiveSplit`処理で
`external/LiveSplit/bin/release/Components/`にもコピーされます。
ローカルの通常ビルドには`--configuration Debug`も使用できます。

テストではDTOとスナップショット、LiveSplit Adapter、Runtime、実際のWebSocket通信、
Originによる権限、コンポーネントの起動・停止を確認します。

## CLIの検査

TestHostを含む上のReleaseビルドを済ませてから実行します。

```powershell
Push-Location tools/livesplit-bridge-cli
uv sync --locked
uv run ruff check src tests
uv run ruff format --check src tests
uv run pytest
Pop-Location
```

`pytest`は単体テストと、TestHostを起動するE2Eテストを含みます。
起動オプションとコマンドは[Debug CLI](../tools/livesplit-bridge-cli/README.md)を参照してください。

型検査には同じディレクトリで`uv run ty check`を使用します。現在は既存コードの型診断が
残っているため、終了コードが0になる検査としては扱えません。

## TestHostによる通信確認

TestHostはテスト用のRunと本番の`Component`を起動する内部開発用プログラムです。
LiveSplitの通常画面を操作せずに、実際のJSON v4 WebSocket通信を確認できます。
上のソリューションビルドに含まれます。単独でビルドする場合は次を使います。

```powershell
dotnet build tests/LiveSplit.Bridge.TestHost/LiveSplit.Bridge.TestHost.csproj --configuration Release
./tests/LiveSplit.Bridge.TestHost/bin/Release/net4.8.1/LiveSplit.Bridge.TestHost.exe --port 54000
```

RPCの応答を確認すると標準出力に`READY`を表示します。別の端末からCLIやクライアントを
接続してください。TestHostの端末でEnterを押すと終了します。標準入力を閉じても終了します。
既定のRunには`First`と`Second`の2つのSegmentがあり、画像はありません。

```powershell
./tests/LiveSplit.Bridge.TestHost/bin/Release/net4.8.1/LiveSplit.Bridge.TestHost.exe --port 54000 --with-icons
```

`--with-icons`では次の画像を有効にします。

| 画像 | 内容 |
| --- | --- |
| ゲーム | 256 × 256の不透明なノイズ画像 |
| First Segment | 16 × 16の不透明なノイズ画像。ゲーム画像とは別のseed |
| Second Segment | なし。`null` |

固定seedのxorshift32で画素を作り、本番AdapterでPNGに変換します。外部の画像やサービスは
不要です。ゲーム画像は圧縮しにくく、`get_run_icons`のJSON応答が64 KiBを超えます。
画像を扱うクライアントの検査ではUTF-8応答サイズ、デコードしたPNG、Segment位置、
同じRPC接続で次のQueryを使えることを確認できます。

## 配布ZIP

Releaseビルド後に実行します。

```powershell
./eng/package-release.ps1 -PackageName "LiveSplit.Bridge-local"
```

`artifacts/`にZIPと`SHA256SUMS.txt`を作成します。このスクリプトはZIPに空でない
`LiveSplit.Bridge.dll`が1つだけ入っていることを検査します。
配布先への配置は[READMEのインストール手順](../README.md#インストール)を参照してください。

## CIとリリース

[CI](../.github/workflows/ci.yml)はPR、mainへのpush、手動実行を対象とし、次の2つの
jobを実行します。

| job | 内容 |
| --- | --- |
| Schema contract | Schema生成、公開ファイルとのバイト比較、C#シリアライズ結果と固定ケースのAjv検証 |
| Windows tests | サブモジュール取得、CLI lint、.NETビルド・テスト、配布ZIPの検査、CLIテスト |

両jobの成功を確認してください。`ty check`はローカル開発用で、現在のCIには含まれていません。
Schema contractと同じ手順は[Schemaの生成・検証](schema-generation.md#生成と検証)にあります。

[Release workflow](../.github/workflows/release.yml)は`v*`タグのpushで配布物を作成し、
GitHub ReleaseにZIPとチェックサムを添付します。CIとReleaseは同じパッケージ作成スクリプトを
使用します。
