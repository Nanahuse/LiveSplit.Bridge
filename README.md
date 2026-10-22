# LiveSplit.Bridge

LiveSplit.Bridgeは、外部アプリケーションとLiveSplitを接続するためのLiveSplit
コンポーネントです。対応する外部アプリケーションから、タイマーの状態確認や操作、
ゲーム内時間の制御ができるようになります。

通信にはJSON Protocol v4を使用し、同じPC上の外部アプリケーションからWebSocketで接続できます。

このコンポーネント単体では画面表示や自動Splitを行いません。LiveSplit.Bridgeに対応した
外部アプリケーションと組み合わせて使用してください。

## 対応環境

- Windows
- LiveSplit 1.8.37
- .NET Framework 4.8.1

## できること

対応する外部アプリケーションから、次の機能を利用できます。

- LiveSplitの現在のタイマー状態を確認する
- ゲーム名、カテゴリ、Segment、比較タイムなどのRun情報と、必要に応じてアイコンを取得する
- タイマーの開始、Split、Skip、Undo、Reset、Pause、Resumeを操作する
- ゲーム内時間を設定、Pause、Resumeする
- LiveSplit側で行われたタイマー操作や設定変更を受け取る

## インストール

1. [GitHub Releases](https://github.com/Nanahuse/LiveSplit.Bridge/releases)から
   JSON Protocol v4対応の`LiveSplit.Bridge-v*.zip`をダウンロードします。
2. LiveSplitを終了します。
3. ZIP内の`LiveSplit.Bridge.dll`をLiveSplitの`Components`フォルダーへ配置します。
4. LiveSplitを起動します。
5. レイアウトを右クリックして`Edit Layout...`を開きます。
6. `+`から`Control` > `LiveSplit Bridge`を追加します。

`LiveSplit Bridge`はレイアウト上には表示されません。レイアウトに追加されている間、
対応する外部アプリケーションから接続できます。

## 設定と接続

レイアウト編集画面で`LiveSplit Bridge`の設定を開くと、接続に使用するWebSocket
ポートを変更できます。変更後は`Apply`を押してください。既定値は次のとおりです。

| 項目 | 既定値 |
|---|---:|
| WebSocketポート | `54000` |

通常は既定値のまま使用できます。接続先は次のとおりです。

| 用途 | 接続先 |
| --- | --- |
| 状態の取得・操作（RPC） | `ws://127.0.0.1:<port>/bridge/v4/rpc` |
| 変更通知（Events） | `ws://127.0.0.1:<port>/bridge/v4/events` |

外部アプリケーション側で接続先を指定する場合は、ここで設定したポートと同じ値を指定
してください。設定内容はLiveSplitのレイアウトに保存されます。

接続できるのは同じPC上のアプリケーションだけです。通信形式、ブラウザーからの操作権限、
通知の扱いは[クライアント開発ガイド](CLIENT_DEVELOPMENT.md)を参照してください。

## 更新

1. LiveSplitを終了します。
2. 新しいリリースのZIPをダウンロードします。
3. ZIP内の`LiveSplit.Bridge.dll`を既存のLiveSplitの`Components`フォルダーへ上書きします。
4. LiveSplitを起動します。

## アンインストール

1. レイアウト編集画面から`LiveSplit Bridge`を削除します。
2. LiveSplitを終了します。
3. `Components`フォルダーから`LiveSplit.Bridge.dll`を削除します。

## トラブルシューティング

- 外部アプリケーションが接続できない場合は、現在のレイアウトに
  `LiveSplit Bridge`が追加されていることを確認してください。
- LiveSplitを複数起動する場合は、ポートが重複しないよう、それぞれのレイアウトで
  異なるWebSocketポートを設定してください。
- 設定画面で`Status: Failed`と表示される場合は、併記されたエラーを確認してください。
  ポートが使用中の場合は、別のポートを設定して`Apply`を押してください。
- Bridgeは同じPCからの接続だけを受け付けます。別のPCからは接続できません。
- 更新後に問題が起きた場合は、LiveSplitを終了してからファイルを上書きしたか確認して
  ください。

## 開発者向け資料

- [クライアント開発ガイド](CLIENT_DEVELOPMENT.md): JSON v4の通信仕様、権限、再同期
- [Debug CLI](tools/livesplit-bridge-cli/README.md): コマンドによるRPC・Eventsの確認
- [Schema生成・検証](tools/schema/README.md): C# DTOからのSchema生成と契約検証
