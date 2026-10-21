# LiveSplit.Bridge

LiveSplit.Bridgeは、外部アプリケーションとLiveSplitを接続するためのLiveSplit
コンポーネントです。対応する外部アプリケーションから、タイマーの状態確認や操作、
ゲーム内時間の制御ができるようになります。

この開発ブランチでは、JSON Protocol v4を実装しています。公開Releaseと`main`ブランチの
通信仕様はこの作業ブランチの内容とは異なる場合があります。JSON Protocol v4は旧Protobuf
Protocol v3と互換性がありません。同梱のDebug CLIはこのブランチのJSON v4向けであり、v3の
公開Releaseや`main`とは通信できません。

このコンポーネント単体では画面表示や自動Splitを行いません。LiveSplit.Bridgeに対応した
外部アプリケーションと組み合わせて使用してください。

## 対応環境

- Windows
- LiveSplit 1.8.37

## できること

対応する外部アプリケーションから、次の機能を利用できます。

- LiveSplitの現在のタイマー状態を確認する
- タイマーの開始、Split、Skip、Undo、Reset、Pause、Resumeを操作する
- ゲーム内時間を設定、Pause、Resumeする
- LiveSplit側で行われたタイマー操作や設定変更を受け取る

## 公開Releaseのインストール

以下はGitHub Releasesで公開中のパッケージを使う場合の手順です。JSON Protocol v4はこの開発ブランチの候補で、まだReleaseされていません。公開パッケージはこのブランチと異なるProtocolを含むことがあるため、Releaseとv4開発ブランチのクライアント仕様を混在させないでください。v4へ接続するクライアントは、v4を実装したBridgeビルドと組み合わせてください。

1. [最新のReleases](https://github.com/Nanahuse/LiveSplit.Bridge/releases/latest)から
   `LiveSplit.Bridge-v*.zip`をダウンロードします。
2. LiveSplitを終了します。
3. ZIPの内容をLiveSplitの`Components`フォルダーへ展開します。
4. LiveSplitを起動します。
5. レイアウトを右クリックして`Edit Layout...`を開きます。
6. `+`から`Control` > `LiveSplit Bridge`を追加します。

`LiveSplit Bridge`はレイアウト上には表示されません。レイアウトに追加されている間、
対応する外部アプリケーションから接続できます。

## 設定

レイアウト編集画面で`LiveSplit Bridge`の設定を開くと、接続に使用するWebSocket
ポートを変更できます。既定値は次のとおりです。

| 項目 | 既定値 |
|---|---:|
| WebSocketポート | `54000` |

RPCは`ws://127.0.0.1:<port>/bridge/v4/rpc`、イベントは
`ws://127.0.0.1:<port>/bridge/v4/events`で接続します。JSONはWebSocket Text Messageで
送信します。RPC RequestはUTF-8で64 KiBまでです。上限を超える接続はMessage Too Bigで
終了します。通常は既定値のまま使用できます。
外部アプリケーション側で接続先を指定する場合は、ここで設定したポートと同じ値を指定
してください。設定内容はLiveSplitのレイアウトに保存されます。

WebSocketサーバーは引き続きloopbackだけで待ち受けます。Eventsと読み取り専用のQuery RPCは
Originに関係なく利用できます。OBSのブラウザソースでは、ローカルHTMLから追加のHTTPサーバー
なしでQueryとEventsを利用できます。TimerやGame Timeを操作するControl RPCは従来のOrigin
制限に従い、Originヘッダーなし、または`localhost`、`127.0.0.1`、`::1`のHTTP/HTTPS Origin
からのみ許可されます。それ以外のOriginからControlを要求すると`permission_denied`が返り、
WebSocket接続は維持されます。Origin制限はブラウザ由来の要求を制限する仕組みであり、完全な
認証ではありません。

Eventは再取得のきっかけとして扱い、現在の状態はQueryで確認してください。送信待ちの通知は
全接続共通で最大256件まで保持し、満杯時は新しい通知を破棄します。再送や購読者ごとの配送・
順序の補償はありません。クライアントは`session_id`と`event_sequence`を確認し、欠落や再接続時に
必要な状態を再取得してください。`run_changed`は公開Runの内容が変わった場合に発行し、
Resetで比較タイムなどが更新された場合も含みます。

`get_run`は画像を含まないRun情報を返します。アイコンが必要なクライアントは
`{"request_id":10,"method":"get_run_icons"}`をRPCへ送信してください。成功Responseの`result`は
`{"game_icon":null,"segment_icons":[null,"iVBORw0KGgo..."]}`のように、PNGのBase64文字列またはnullを返します。
`segment_icons`は取得時のRunのSegment順に並び、アイコンがない要素は`null`になります。
アイコン専用Eventやキャッシュはありません。取得のたびに現在のRunからPNGへ変換します。
`get_run`との間でRunが切り替わる可能性があるため、画像の再取得はクライアントが判断してください。

## 更新

1. LiveSplitを終了します。
2. 新しいリリースのZIPをダウンロードします。
3. ZIPの内容を既存のLiveSplitの`Components`フォルダーへ上書きします。
4. LiveSplitを起動します。

## アンインストール

1. レイアウト編集画面から`LiveSplit Bridge`を削除します。
2. LiveSplitを終了します。
3. `Components`フォルダーからLiveSplit.Bridgeのファイルを削除します。

## トラブルシューティング

- 外部アプリケーションが接続できない場合は、現在のレイアウトに
  `LiveSplit Bridge`が追加されていることを確認してください。
- LiveSplitを複数起動する場合は、ポートが重複しないよう、それぞれのレイアウトで
  異なるWebSocketポートを設定してください。
- Bridgeは同じPCからの接続だけを受け付けます。別のPCからは接続できません。
- 更新後に問題が起きた場合は、LiveSplitを終了してからファイルを上書きしたか確認して
  ください。

## 関連ドキュメント

- JSON WebSocket実装と検証状況:
  [`docs/phase3c-json-transport.md`](docs/phase3c-json-transport.md)

- JSON v4 Schemaと検証手順:
  [`tools/schema/README.md`](tools/schema/README.md)

- Bridgeへ接続するアプリケーションを作る方:
  [`CLIENT_DEVELOPMENT.md`](CLIENT_DEVELOPMENT.md)
- 同梱のDebug CLIを使う方:
  [`tools/livesplit-bridge-cli/README.md`](tools/livesplit-bridge-cli/README.md)
