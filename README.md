# LiveSplit.Bridge

LiveSplit.BridgeはLiveSplitと同じPC上の外部アプリケーションをWebSocketで接続する
LiveSplitコンポーネントです。通信にはJSON Protocol v4を使用します。

## できること

- タイマーの状態、Run情報、比較タイム、アイコンを取得する
- タイマーの開始、Split、Skip、Undo、Reset、Pause、Resumeを操作する
- ゲーム内時間を設定、一時停止、再開する
- LiveSplit側のタイマー操作やRun・Contextの変更通知を受け取る

Bridge単体では画面表示や自動Splitを行いません。対応する外部アプリケーションと
組み合わせて使用してください。

## インストール

1. [GitHub Releases](https://github.com/Nanahuse/LiveSplit.Bridge/releases)から
   JSON Protocol v4対応の`LiveSplit.Bridge-v*.zip`を取得します。
2. LiveSplitを終了し、ZIP内の`LiveSplit.Bridge.dll`を`Components`フォルダーへ配置します。
3. LiveSplitを起動し、右クリックから`Edit Layout...`を開きます。
4. `+`から`Control` > `LiveSplit Bridge`を追加します。

対応環境やポート設定、更新方法は[導入・設定](docs/installation.md)を参照してください。

## ドキュメント

| 目的 | 参照先 |
| --- | --- |
| 導入、設定、更新、接続できないときの確認 | [導入・設定](docs/installation.md) |
| 自分のアプリから接続してRPCとEventsを使う | [クライアント開発ガイド](docs/client-guide.md) |
| Protocol v4の通信仕様と保証を調べる | [Protocol Reference](docs/protocol.md) |
| Bridge本体をビルド・テストする | [開発手順](docs/development.md) |
| 内部構造と各コンポーネントの責務を知る | [アーキテクチャ](docs/architecture.md) |
| JSON Schemaを生成・検証する | [Schemaの生成・検証](docs/schema-generation.md) |
| コマンドで通信を確認する | [Debug CLI](tools/livesplit-bridge-cli/README.md) |

## ライセンス

[MIT License](LICENSE)
