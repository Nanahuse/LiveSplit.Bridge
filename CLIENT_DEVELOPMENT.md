# LiveSplit.Bridge クライアント開発ガイド

外部アプリケーションからLiveSplit.Bridgeへ接続する方法を説明します。現在の開発版は
Protocol v3です。v3は以前のProtocolと通信互換性がありません。

## Protobufと接続

Protocolの正本は[`proto/livesplit/bridge/v3`](proto/livesplit/bridge/v3)の3つの
`.proto`ファイルです。packageは`livesplit.bridge.v3`、`protocol_version`は`3`です。
クライアントは対応するProtobufコードを生成してください。

BridgeはloopbackのWebSocketサーバーを既定ポート`54000`で公開します。ポートはLiveSplitの
コンポーネント設定で変更できます。

| 用途 | エンドポイント |
|---|---|
| RPC | `ws://127.0.0.1:<port>/bridge/v3/rpc` |
| Events | `ws://127.0.0.1:<port>/bridge/v3/events` |

通信内容はProtobufメッセージをWebSocket Binary Messageにしたものです。JSONやテキスト
フレームは使いません。Bridgeはloopbackだけにbindするため、別のPCからは接続できません。

## RPC RequestとResponse

各`Request`には`protocol_version: 3`、要求ごとの`request_id`、1つのRequest bodyを設定
します。Responseでは、送信した`request_id`との一致、`protocol_version`が3であること、
有効な`session_id`を確認してください。`error` bodyを受信したら内容を表示し、その要求は
失敗として扱います。成功ResponseのbodyはRequestに対応していなければなりません。

| Request | Response | 内容 |
|---|---|---|
| `get_timer_state` | `get_timer_state` | 現在のタイマー |
| `get_run` | `get_run` | Runの定義 |
| `get_attempt` | `get_attempt` | 現在Attemptの情報 |
| `get_context_state` | `get_context_state` | Timing Methodや設定値 |
| `get_completed_count` | `get_completed_count` | 完走Attempt数 |
| `timer_operation` | `operation` | Timer Control |
| `game_time_operation` | `operation` | Game Time Control |

Control成功時の`OperationResponse`は空メッセージです。v2の`success`、`message`、
`timer_state`フィールドはありません。Timeoutや切断でControlの結果が不明でも、自動で同じ
Control要求を再送しないでください。再送すると操作が二重に実行される可能性があります。

## State

### TimerState

`TimerState`は現在の`phase`、`split_index`、Real Time / Game Time、Game Timeの初期化・
Pause状態を持ちます。時間は100ナノ秒単位のticksです。`game_time_ticks`は値が存在する場合
だけ設定されます。TimerStateにはrevisionやsession IDはありません。`session_id`はRPC
Response envelopeにあります。

### RunState

`RunState`にはゲーム名、カテゴリ、offset、metadata、available comparisons、segmentsと
各segmentのcomparison time、best segment time、画像情報があります。Comparison一覧と
Segment情報はRunの構成確認に使います。

### AttemptStateとCompletedCount

`AttemptState`には`attempt_count`とSegmentごとの`split_time`、custom variablesがあります。
完走したAttempt数は別の`GetCompletedCount` / `CompletedCount` APIで取得します。

### ContextState

`ContextState`には現在のTiming Method、Current Comparison、Run MetadataのCustom
Variablesが含まれます。Timing Methodは`REAL_TIME`または`GAME_TIME`です。

## TimerとGame Time Control

Timerの操作は`TIMER_START`、`TIMER_SPLIT`、`TIMER_SKIP`、`TIMER_UNDO`、`TIMER_RESET`、
`TIMER_PAUSE`、`TIMER_RESUME`です。Game Timeは`INITIALIZE`、`SET`、
`GAME_TIME_PAUSE`、`GAME_TIME_RESUME`をサポートします。`SET`には100ナノ秒単位のticksを
指定します。

## Eventsとevent_sequence

Events endpointは接続中のクライアントへ`BridgeEvent`をbroadcastします。Eventには
`session_id`、`event_sequence`、Event種別が含まれ、Timerに関するイベントでは必要に応じて
`timer_state`も含まれます。Event種別にはTimer操作、Run変更、Context変更があります。
Protocol v3にはHeartbeatイベントはありません。

`event_sequence`は配信された状態変更イベントごとに単調増加します。連続しない番号を受信した
場合、イベントの欠落として扱ってください。異なる`session_id`を受信した場合は、Bridgeの
セッションが変わっており、以前のsequenceとの連続性はありません。

## 初期取得、欠落からの再同期、再接続

接続時はEvents WebSocketに接続して受信を始め、その後RPCで`get_timer_state`、`get_run`、
`get_attempt`、`get_context_state`、`get_completed_count`を取得してください。RPCで取得した
現在値を初期状態として扱います。RPCとEventsは別接続のため、RPC ResponseとEventの到着順は
保証されません。初期取得中に受け取ったEventsはsequenceを確認し、初期状態に含まれる変更を
二重適用しないようにしてください。

イベントsequenceの欠落、接続切断、または`session_id`変更を検出したら、操作を止めてRPCから
必要なStateを取得し直します。そのStateを新しい基準にしてEventsの処理を再開してください。
欠落した操作を推測で再現してはいけません。再接続後は新しいsessionを確認し、初期取得を
やり直してください。

## Debug CLI

同梱CLIのコマンド例は[`tools/livesplit-bridge-cli/README.md`](tools/livesplit-bridge-cli/README.md)
を参照してください。
