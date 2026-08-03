# LiveSplit.Bridge
A LiveSplit component for controlling and monitoring LiveSplit from external applications.

## Debug CLI

Python製のデバッグCLIは [`tools/livesplit-bridge-cli`](tools/livesplit-bridge-cli) にあります。
`uv sync` で環境を作成し、スナップショット取得、タイマー／ゲーム時間操作、
イベント監視を実行できます。詳しいセットアップとコマンド例はCLIのREADMEを参照してください。

## Release

`v`から始まるタグ（例: `v0.1.0`）をpushすると、GitHub Actionsが.NETテスト、
CLIのlint、およびCLIからBridgeを操作するE2Eテストを実行します。すべて成功した場合は
Release構成のコンポーネント一式をZIP化し、SHA-256チェックサムとともにGitHub Releaseへ
自動的に添付します。

```powershell
git tag v0.1.0
git push origin v0.1.0
```
