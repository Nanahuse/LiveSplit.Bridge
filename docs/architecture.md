# アーキテクチャ

BridgeはLiveSplitのレイアウトに追加される非表示のコンポーネントです。
LiveSplitの状態と操作をJSONの通信モデルへ変換し、RPCとEventsを公開します。
ビルドやテストの手順は[開発手順](development.md)を参照してください。

## コンポーネントの責務

| 要素 | 責務 |
| --- | --- |
| [Factory](../src/LiveSplit.Bridge/Factory.cs) | LiveSplitのControlコンポーネントとしてBridgeを登録する |
| [Component](../src/LiveSplit.Bridge/Component.cs) | 設定、起動・停止・再試行、LiveSplit更新ループとの接続を管理する |
| [BridgeSettings](../src/LiveSplit.Bridge/BridgeSettings.cs) | ポート設定のXML保存・読み込みと設定画面を提供する |
| [JsonBridgeHost](../src/LiveSplit.Bridge/JsonBridgeHost.cs) | RuntimeとTransportを同じライフタイムで所有する |
| [JsonWebSocketTransport](../src/LiveSplit.Bridge/JsonWebSocketTransport.cs) | ループバックでRPC・Eventsを公開し、通知キューと送信処理を管理する |
| [JsonWebSocketRpcHandler](../src/LiveSplit.Bridge/JsonWebSocketRpcHandler.cs) | JSON envelope、ID、重複プロパティを検査し、具体的なRequest DTOを選ぶ |
| [WebSocketOriginValidator](../src/LiveSplit.Bridge/WebSocketOriginValidator.cs) | RPC接続のOriginからControl権限を判定する |
| [JsonBridgeRuntime](../src/LiveSplit.Bridge/JsonBridgeRuntime.cs) | RPCを振り分け、公開スナップショット、Session、Event sequenceを管理する |
| [JsonLiveSplitAdapter](../src/LiveSplit.Bridge/JsonLiveSplitAdapter.cs) | LiveSplitの状態をDTOへ変換し、タイマー・ゲーム内時間の操作を実行する |
| [LiveSplit.Bridge.Json](../src/LiveSplit.Bridge.Json/) | DTO、Serializer設定、スナップショット比較・コピー処理を定義する |

Runtimeは[AdapterとEvent Sinkのインターフェイス](../src/LiveSplit.Bridge/JsonRuntimeInterfaces.cs)を
使うため、WebSocketへの接続処理から独立して検査できます。

## RPCの処理

Transportはメッセージ全体のサイズとText Messageであることを確認します。
RpcHandlerがIDと構造を検査してDTOへ変換し、RuntimeがQueryまたはControlへ振り分けます。
Adapterで取得・操作した結果をResponse DTOに入れ、TransportがJSONにして返します。

QueryのうちRunとContextはRuntimeが保持する公開スナップショットを返し、TimerState、
Attempt、CompletedCount、RunIconsはAdapterから取得します。Controlは`controlGate`で
直列化します。このロックはLiveSplit自身の操作や、すべてのQueryを排他するものではありません。

## 状態観測とEvents

RuntimeはLiveSplitの開始、Split、Skip、Undo、Reset、Pause、Resume、Run手動変更の
通知を購読します。更新ループではRun参照の置換とContextの変更も観測します。
Contextの観測には約100 ms以上の間隔を空け、公開値と内容が異なる場合に更新します。

Runの比較対象には画像を含めません。Reset時は構造が一致すればタイミング情報をコピーして
更新し、構造が異なる場合などはRun全体を取得し直します。更新中のRun置換を検出した場合は、
次の更新ループで再取得します。

Eventのsequence割り当てとキューへの投入は`eventGate`で直列化します。
Transportのキューは全接続で共有し、バックグラウンドの送信処理がEvents接続へ配信します。
満杯のキューへの追加を待たず、新しいEventを破棄するため、LiveSplitの更新スレッドで
ソケット送信を待つ構造にはなっていません。配送上の仕様は[Events](protocol.md#5-events)を
参照してください。

## ライフサイクル

`Component.Update`が初回起動を行い、起動失敗時は約5秒後に再試行します。
設定画面で`Apply`を押すとHostを破棄して作り直します。Hostの作成時に新しいRuntimeと
Sessionができ、Transportの起動に失敗した場合は作成済みのリソースも破棄します。

終了時はRuntimeがLiveSplitのイベント購読を解除し、Transportがキューの受付とWebSocket
サーバーを停止します。Componentをレイアウトから削除した場合も接続が閉じます。

## アイコン取得とUIスレッド

Runアイコンは要求時に取得します。LiveSplitのフォームと同じスレッドで画像を複製し、
古いRun画像の破棄と競合しないようにします。その後、複製した画像をRPC側のスレッドでPNGに
変換し、画像リソースを解放します。フォームやハンドルが使えない場合はQueryがエラーになります。
通常のRunスナップショットに画像を入れないことで、この処理を必要な要求に限定しています。

## DTOとSchema

`LiveSplit.Bridge.Json`のソースはBridgeや契約サンプルのプロジェクトへ組み込まれます。
Schema生成プロジェクトも同じDTOソースを使います。
生成用の属性や画像型の切り替えは`SCHEMA_GENERATION`で分離しています。

RuntimeのSerializer設定はDTOをJSONへ対応付けます。生成したSchema自体を読み込んで
RPC入力を検証する構造ではありません。両者の差と生成器の保守は
[Schemaの生成・検証](schema-generation.md)を参照してください。
