# LiveSplit.Bridge 開発ガイド

この文書は、LiveSplit.Bridge 本体およびデバッグCLIを開発する人向けの手順をまとめた
ものです。コンポーネントのインストールと利用方法は [`README.md`](README.md) を参照して
ください。

## 対象 LiveSplit

開発・テスト対象は **LiveSplit 1.8.37** です。`external/LiveSplit` submodule は、この
リリースのコミット `683cd60a9f3fe2dc31a5054c6d10f5d71f2e6ae1` に固定しています。

submodule を別のコミットへ更新する場合は、対応バージョンを検証し、`README.md` とこの
文書のバージョンおよびコミットIDも同時に更新してください。

## 必要な環境

- Windows
- .NET SDK
- .NET Framework 4.8.1 のビルド環境
- Git
- Python 3.14 以降、`uv`（デバッグCLIを開発する場合）

## セットアップ

submodule を含めてリポジトリを取得します。

```powershell
git clone --recurse-submodules <repository-url>
cd LiveSplit.Bridge
```

すでに clone 済みの場合は、次のコマンドで submodule を展開します。

```powershell
git submodule update --init --recursive
```

固定先を確認するには次を実行します。

```powershell
git submodule status
```

`external/LiveSplit` の先頭に
`683cd60a9f3fe2dc31a5054c6d10f5d71f2e6ae1` が表示されれば正しい状態です。

## ビルド

```powershell
dotnet build LiveSplit.Bridge.slnx
```

Bridge のビルド後、必要なDLLとPDBは自動的に次の場所へ配置されます。

```text
external/LiveSplit/bin/debug/Components/
```

LiveSplit 本体を起動し、レイアウトへ `LiveSplit Bridge` を追加すると、ビルドした
コンポーネントを確認できます。

## テスト

.NET のテストを実行します。

```powershell
dotnet test LiveSplit.Bridge.slnx --nologo
```

デバッグCLIの環境構築、lint、単体テスト、BridgeとのE2Eテストについては
[`tools/livesplit-bridge-cli/README.md`](tools/livesplit-bridge-cli/README.md) を参照して
ください。

## 実装資料

通信方式、プロトコル、スレッドモデルなどの設計方針は
[`docs/implementation-plan.md`](docs/implementation-plan.md) にあります。

## リリース

`v` から始まるタグ（例: `v0.1.0`）を push すると、GitHub Actions が次を実行します。

- .NET テスト
- CLI の lint
- CLI から Bridge を操作するE2Eテスト
- Release構成のコンポーネント一式のZIP化
- ZIPのSHA-256チェックサム生成
- GitHub Releaseへの成果物添付

```powershell
git tag v0.1.0
git push origin v0.1.0
```
