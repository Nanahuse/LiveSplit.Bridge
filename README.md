# LiveSplit.Bridge
LiveSplit.Bridge は、外部アプリケーションから LiveSplit の状態を取得・監視し、
タイマーやゲーム内時間を操作するための LiveSplit コンポーネントです。

## 対応バージョン

- **LiveSplit 1.8.37**
- Windows

動作対象は LiveSplit 1.8.37 です。リポジトリ内の LiveSplit submodule も、同リリースの
コミット `683cd60a9f3fe2dc31a5054c6d10f5d71f2e6ae1` に固定しています。

## インストール

1. Releases から最新の `LiveSplit.Bridge-v*.zip` をダウンロードします。
2. LiveSplit を終了します。
3. ZIP の内容を、LiveSplit の `Components` フォルダーへ展開します。
4. LiveSplit を起動します。
5. レイアウトを右クリックし、`Edit Layout...` を開きます。
6. `+` から `Control` > `LiveSplit Bridge` を追加します。

このコンポーネントは画面には何も描画しません。レイアウトに追加されている間、外部
アプリケーションから接続できる Bridge サーバーとして動作します。

## 接続先

既定では、ローカルPC上の次のエンドポイントを使用します。

| 用途 | エンドポイント | 通信方式 |
|---|---|---|
| 状態取得・操作 | `tcp://127.0.0.1:54000` | ZeroMQ REQ/REP |
| イベント監視 | `tcp://127.0.0.1:54001` | ZeroMQ PUB/SUB |

RPCポートとイベントポートは、レイアウト編集画面のコンポーネント設定から変更できます。
変更内容はLiveSplitのレイアウトに保存されます。

外部クライアントは、同梱の Protobuf スキーマに従って通信します。スキーマは
[`proto`](proto) にあります。

現在、次の操作に対応しています。

- 現在のタイマー状態の取得
- タイマーの開始、Split、Skip、Undo、Reset、Pause、Resume
- ゲーム内時間の初期化、設定、Pause、Resume
- タイマー操作、Run変更、ゲーム内時間変更、定期スナップショットの購読

## 動作確認

リポジトリには Python 製のデバッグCLIがあります。CLIを利用すると、スナップショット
取得、タイマー操作、イベント監視を手動で確認できます。セットアップとコマンド例は
[`tools/livesplit-bridge-cli/README.md`](tools/livesplit-bridge-cli/README.md) を参照してください。

## トラブルシューティング

- クライアントが接続できない場合は、レイアウトに `LiveSplit Bridge` が追加されているか
  確認してください。
- 同じPCで LiveSplit を複数起動する場合は、ポートが競合しないよう各レイアウトの
  コンポーネント設定で異なるRPCポートとイベントポートを指定してください。
- Bridge は既定でループバックアドレスだけを使用し、別のPCからは接続できません。

開発環境の準備、ビルド、テスト、リリース方法は [`DEVELOPMENT.md`](DEVELOPMENT.md) を
参照してください。
