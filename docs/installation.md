# 導入・設定

基本的な導入・設定・更新・削除の手順は[README](../README.md)で確認できます。
この文書では設定できるポートの範囲やアプリケーションの接続先、操作制限などを補足します。
Bridgeはレイアウト上には表示されません。レイアウトに追加されている間、接続を受け付けます。

## 対応環境

- Windows
- LiveSplit 1.8.37
- .NET Framework 4.8.1

外部アプリケーションもJSON Protocol v4に対応している必要があります。
旧Protobuf Protocol v3を使用するアプリケーションとは接続できません。

## インストール

この文書は開発中のv4を対象としています。v4と公開済みのv3対応版には互換性がありません。
v4対応版の公開状況は[READMEのインストール案内](../README.md#インストール)で確認してください。

1. [GitHub Releases](https://github.com/Nanahuse/LiveSplit.Bridge/releases)から
   JSON Protocol v4対応の`LiveSplit.Bridge-v*.zip`をダウンロードします。
2. LiveSplitを終了します。
3. ZIP内の`LiveSplit.Bridge.dll`をLiveSplitの`Components`フォルダーへ配置します。
4. LiveSplitを起動し、右クリックから`Edit Layout...`を開きます。
5. `+`から`Control` > `LiveSplit Bridge`を追加します。
6. 使用するレイアウトを保存します。

## ポート設定

レイアウト編集画面から`LiveSplit Bridge`の設定を開きます。WebSocketポートの既定値は
`54000`です。変更する場合は`1`～`65535`の値を指定し、`Apply`を押してください。
設定内容はLiveSplitのレイアウトに保存されます。
変更後はレイアウトも保存してください。`Status: Running`はBridgeが接続を受け付けている状態です。

`Apply`を押すとBridgeが再起動し、接続中のアプリケーションは切断されます。
外部アプリケーション側にも同じポートを指定して再接続してください。

| 用途 | 接続先 |
| --- | --- |
| 状態取得・操作のRPC | `ws://127.0.0.1:<port>/bridge/v4/rpc` |
| 変更通知のEvents | `ws://127.0.0.1:<port>/bridge/v4/events` |

接続できるのは同じPC上のアプリケーションだけです。LiveSplitを複数起動する場合は、
各Bridgeに異なるポートを設定してください。

ブラウザーやOBSのブラウザーソースからも状態取得とEventsの受信ができます。
タイマー操作には接続元による制限があります。対応アプリの説明に従い、通信の詳細が
必要な場合は[クライアント開発ガイド](client-guide.md)を参照してください。

## 更新

1. LiveSplitを終了します。
2. JSON Protocol v4対応の新しいリリースのZIPを取得します。
3. ZIP内の`LiveSplit.Bridge.dll`を`Components`フォルダーへ上書きします。
4. LiveSplitを起動し、外部アプリケーションから再接続します。

## アンインストール

1. レイアウト編集画面から`LiveSplit Bridge`を削除し、レイアウトを保存します。
2. LiveSplitを終了します。
3. `Components`フォルダーから`LiveSplit.Bridge.dll`を削除します。

## トラブルシューティング

| 症状 | 確認すること |
| --- | --- |
| 接続できない | 現在のレイアウトにBridgeが追加されているか、設定画面が`Status: Running`か確認します |
| `Status: Failed`と表示される | 併記されたエラーを確認します。ポートが使用中なら別のポートに変更し、`Apply`を押します。起動失敗時は5秒ごとに自動再試行します |
| ポート変更後に接続できない | 外部アプリケーションのポート設定も変更し、再接続します |
| 別のPCから接続できない | Bridgeは同じPCからの接続だけを受け付けます |
| 状態は読めるが操作が拒否される | アプリケーションの接続元がControl権限の条件を満たすか、[権限の仕様](protocol.md#7-権限)で確認します |
| 更新後に問題が起きる | LiveSplit終了後にDLLを上書きしたか、Bridgeとアプリケーションが両方v4に対応しているか確認します |

通信をコマンドで確認する場合は[Debug CLI](../tools/livesplit-bridge-cli/README.md)を使用できます。
