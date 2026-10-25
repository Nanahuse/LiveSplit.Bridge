# LiveSplit.Bridge

LiveSplit.Bridgeは外部アプリケーションからLiveSplitの情報を取得したり、タイマーを
操作したりするためのコンポーネントです。WebSocketを使用し、同じPC上の対応アプリケーションと
LiveSplitを接続します。

## 主な機能

- タイマーの状態や記録情報、アイコンを取得する
- タイマーの開始、Split、Skip、Undo、Reset、一時停止・再開を操作する
- ゲーム内時間を設定・制御する
- タイマー操作やRun情報の変更通知を受け取る

LiveSplit.Bridge自体には操作画面や自動Split機能はありません。
対応する外部アプリケーションと組み合わせて使用してください。

## 対応環境

- Windows
- LiveSplit 1.8.37
- .NET Framework 4.8.1
- 使用するBridgeのバージョンに対応した外部アプリケーション

## インストール

1. [GitHub Releases](https://github.com/Nanahuse/LiveSplit.Bridge/releases)から、使用する外部
   アプリケーションと互換性のあるバージョンの`LiveSplit.Bridge-v*.zip`をダウンロードします。
2. LiveSplitを終了します。
3. ZIP内の`LiveSplit.Bridge.dll`をLiveSplitの`Components`フォルダーへ配置します。
4. LiveSplitを起動し、右クリックから`Edit Layout...`を開きます。
5. `+`から`Control` > `LiveSplit Bridge`を追加します。
6. 使用するレイアウトを保存します。

コンポーネントはレイアウト上には表示されません。レイアウトに追加されている間、
外部アプリケーションからの接続を受け付けます。

## 設定

レイアウト編集画面から`LiveSplit Bridge`の設定を開きます。
WebSocketポートの初期値は`54000`です。通常は変更する必要はありません。
変更する場合は`1`～`65535`の番号を指定できます。
設定画面に`Status: Running`と表示されていれば、接続を受け付けている状態です。

ポート番号を変更した場合は`Apply`を押してください。Bridgeが再起動して接続が切断されるため、
外部アプリケーション側にも同じポート番号を指定し、再接続してください。
設定内容を保存するため、LiveSplitのレイアウトも保存します。

複数のLiveSplitを起動する場合は、それぞれのBridgeに異なるポート番号を設定してください。
接続できるのは同じPC上のアプリケーションだけです。

## 更新・アンインストール

### 更新

1. 使用する外部アプリケーションとの互換性を確認し、新しいリリースのZIPを取得します。
2. LiveSplitを終了します。
3. ZIP内の`LiveSplit.Bridge.dll`を`Components`フォルダーの既存ファイルへ上書きします。
4. LiveSplitを起動し、外部アプリケーションから再接続します。

### アンインストール

1. レイアウト編集画面から`LiveSplit Bridge`を削除し、レイアウトを保存します。
2. LiveSplitを終了します。
3. `Components`フォルダーから`LiveSplit.Bridge.dll`を削除します。

## トラブルシューティング

### 外部アプリケーションから接続できない

- 現在のレイアウトに`LiveSplit Bridge`が追加されているか確認してください。
- 設定画面に`Status: Running`と表示されているか確認してください。
- 外部アプリケーションとBridgeでポート番号が一致しているか確認してください。
- Bridgeと外部アプリケーションの対応バージョンを確認してください。
- ポート番号を変更した場合は、外部アプリケーションから再接続してください。

### Status: Failedと表示される

設定画面に併記されたエラー内容を確認してください。ポートがすでに使用されている場合は
別の番号に変更して`Apply`を押し、外部アプリケーション側のポート番号も変更してください。
起動に失敗した場合は5秒ごとに自動で再試行します。

### 別のPCから接続できない

LiveSplit.Bridgeは同じPC上のアプリケーションからの接続専用です。

### 更新後に問題が起きる

LiveSplitを終了してからDLLを上書きしたか、Bridgeと外部アプリケーションのバージョンに
互換性があるか確認してください。

## 関連ドキュメント

- [クライアント開発ガイド](docs/client-guide.md)
- [Protocol v4 Reference](docs/protocol.md)
- [Bridgeの開発手順](docs/development.md)

### クライアントライブラリ

- [livesplit-bridge-client（Python）](https://github.com/Nanahuse/livesplit-bridge-client)
- [livesplit-bridge-client-cpp（C++）](https://github.com/Nanahuse/livesplit-bridge-client-cpp)

## ライセンス

[MIT License](LICENSE)
