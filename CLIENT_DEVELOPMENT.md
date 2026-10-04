# LiveSplit.Bridge クライアント開発ガイド

この文書は、LiveSplit.Bridgeへ接続する外部アプリケーションを開発する方向けです。

## プロトコル

プロトコルの正本は[`proto/livesplit/bridge/v2`](proto/livesplit/bridge/v2)にある
Protobufスキーマです。Protobuf packageは`livesplit.bridge.v2`、現在の
`protocol_version`は`2`です。v2は破壊的変更を含み、旧v1プロトコルとの互換性は
ありません。

| ファイル | 内容 |
|---|---|
| `bridge.proto` | RPCのRequestとResponse |
| `common.proto` | 状態モデル、操作種別、イベント、エラー |
| `run.proto` | Runの状態モデルと画像 |

利用する言語のProtobufコンパイラーでスキーマからコードを生成してください。既存のfieldを
独自に読み替えたり、同名のデータ型を手書きで複製したりせず、スキーマを通信形式の正本として
扱います。

## 接続先

Bridgeは既定でローカルPC上の`127.0.0.1:54000`でWebSocketサーバーを起動し、次の2つの
endpointを公開します。

| 用途 | エンドポイント |
|---|---|
| 状態取得・操作 | `ws://127.0.0.1:<port>/bridge/v2/rpc` |
| イベント監視 | `ws://127.0.0.1:<port>/bridge/v2/events` |

Payloadは既存のProtobufメッセージをWebSocketのBinary Messageとして送受信します。JSONでは
ありません。ポートはLiveSplitのコンポーネント設定で変更できます。クライアント側でも接続先を
設定可能にしてください。Bridgeはloopbackだけにbindするため、別のPCから直接接続することは
できません。

## 状態モデル

v2では、監視対象の状態を役割ごとに4つへ分離します。クライアントは必要な状態だけを
必要な頻度で取得します。

| 状態 | 内容 | 想定される取得頻度 |
|---|---|---|
| `TimerState` | 高頻度で変化するライブタイマー状態 | 約40Hz |
| `RunState` | Run定義やComparisonなど比較的静的な情報 | 変更時のみ |
| `AttemptState` | 現在Attemptに依存する情報 | 変更時のみ |
| `RuntimeState` | LiveSplitの現在設定・UI状態 | 変更時のみ |

### `TimerState`

高頻度取得を想定した軽量な状態です。

| フィールド | 内容 |
|---|---|
| `session_id` | 現在のBridgeセッション |
| `state_revision` | Timerの現在状態に対するrevision |
| `phase` | Timerの状態 |
| `split_index` | 現在のSplit index |
| `real_time_ticks` / `game_time_ticks` | 現在の時間。100ナノ秒単位。存在しない場合はunset |
| `is_game_time_initialized` | Game Timeが初期化済みか |
| `is_game_time_paused` | Game TimeがPause中か |
| `run_revision` | `RunState`のrevision |
| `attempt_revision` | `AttemptState`のrevision |
| `runtime_revision` | `RuntimeState`のrevision |

`event_sequence`、`split_count`、`is_paused`は`TimerState`には含まれません。
`event_sequence`はイベント配送情報として`BridgeEvent`にのみ存在します。Split数は
`RunState.segments`から、Pause状態は`phase`から判断してください。

### `RunState`

Runに属する比較的静的な情報を保持します。

| フィールド | 内容 |
|---|---|
| `session_id` / `run_revision` | セッションとRunのrevision |
| `game_name` / `category_name` | `IRun`由来のゲーム名・カテゴリ名 |
| `offset_ticks` | Runの開始オフセット。100ナノ秒単位 |
| `file_path` / `layout_path` | 保存済みRun/Layoutのパス。存在しない場合はunset |
| `metadata` | Run Metadata。`run_id`、platform、region、emulator使用、変数、custom variable |
| `comparisons` | Run全体で利用可能なComparison名の一覧 |
| `segments` | Segment一覧 |
| `game_icon` | Game icon |

`segments`の各`SegmentInfo`は`index`（0始まり）、`name`、`comparisons`、
`best_segment_time`、`icon`を持ちます。`comparisons`はRun全体のComparison一覧を正とし、
各Segmentには同じ名前の`ComparisonTime`が入ります。値が存在しないComparisonも一覧には
残り、対応する時間値が無い場合は`TimeValue`のreal time / game timeがunsetになります。

`RunState`はAttempt依存の情報を持ちません。`attempt_count`は`AttemptState`で、
Segmentのcustom variableは`AttemptState`で取得します。

### `AttemptState`

現在Attemptに依存する情報を保持します。

| フィールド | 内容 |
|---|---|
| `session_id` / `attempt_revision` | セッションとAttemptのrevision |
| `attempt_count` | 現在のRunのAttempt数 |
| `completed_count` | 完了済みAttempt数 |
| `segments` | 現在AttemptのSegment情報 |

`segments`の各`AttemptSegment`は`index`、`split_time`（`TimeValue`）、
`custom_variables`を持ちます。`custom_variables`はそのAttemptでSegmentに保存された
`Segment.CustomVariableValues`を表します。

### `RuntimeState`

LiveSplitの現在設定・UI状態を保持します。

| フィールド | 内容 |
|---|---|
| `session_id` / `runtime_revision` | セッションとRuntimeのrevision |
| `current_timing_method` | 現在のTiming Method |
| `current_comparison` | 現在のComparison名 |
| `global_hotkeys_enabled` | Global Hotkeysが有効か |
| `custom_variables` | 現在の`Run.Metadata.CustomVariables`の値 |

画像は`Image`（`mime_type`、`data`、`width`、`height`）で表現します。

## RPC

すべての`Request`に次を設定します。

- `protocol_version`: `2`
- `request_id`: クライアントが要求と応答を対応付けるための一意な値
- `body`: 実行する要求を1つだけ設定

`Response`では最初に`request_id`と`body`を確認してください。`error`が設定されている場合は
要求が処理されていないものとして扱います。

利用可能な要求は次のとおりです。

| Request body | Response body | 用途 |
|---|---|---|
| `attach` | `attach` | セッションIDと現在の`TimerState`を取得 |
| `get_timer_state` | `get_timer_state` | 現在の`TimerState`を取得 |
| `get_run` | `get_run` | 現在の`RunState`を取得 |
| `get_attempt` | `get_attempt` | 現在の`AttemptState`を取得 |
| `get_runtime_state` | `get_runtime_state` | 現在の`RuntimeState`を取得 |
| `timer_operation` | `operation` | TimerのStart、Split、Skip、Undo、Reset、Pause、Resume |
| `game_time_operation` | `operation` | Game Timeの初期化、設定、Pause、Resume |

`get_snapshot`は廃止しました。現在のTimer状態は`get_timer_state`で取得します。
`get_timer_state`は高頻度取得を想定した軽量APIです。

`AttachResponse`は`session_id`と`TimerState`を返します。

```protobuf
message AttachResponse {
  uint64 session_id = 1;
  TimerState timer_state = 2;
}
```

`timer_operation`と`game_time_operation`の結果は`OperationResponse`で返します。
`success`が真の場合、`timer_state`には操作後の`TimerState`が入ります。操作が状態を
変えなかった場合も、成功時は操作時点の`TimerState`を返します。

```protobuf
message OperationResponse {
  bool success = 1;
  string message = 2;
  TimerState timer_state = 3;
}
```

Game Timeの`ticks`は100ナノ秒単位です。

## revision

各状態にはrevisionがあり、クライアントは`TimerState`のrevisionとキャッシュ済みの値を
比較することで、変更された状態だけを再取得できます。

### `state_revision`

Timerの現在状態に対するrevisionです。Timer操作やGame Time操作、Timer状態の変化で
増加します。同じ`session_id`の中では単調増加します。

### `run_revision`

`get_run`の返却内容が変更されたことを表します。想定例は次のとおりです。

- Game / Category変更
- Segment構成変更
- Metadata変更
- PB更新
- Best Segment更新
- 生成Comparison更新
- Icon変更

### `attempt_revision`

`get_attempt`の返却内容が変更されたことを表します。想定例は次のとおりです。

- Start
- Split
- Skip
- Undo
- Reset

### `runtime_revision`

`get_runtime_state`の返却内容が変更されたことを表します。想定例は次のとおりです。

- Current Comparison変更
- Timing Method変更
- Global Hotkeys状態変更
- Metadata Custom Variable変更

クライアントは`TimerState`に含まれる`run_revision` / `attempt_revision` /
`runtime_revision`を監視し、キャッシュ済みの値から変化した場合だけ対応するRPCを
呼び出してください。変化していなければ再取得は不要です。

## クライアント利用モデル

### Web UI

Web UIはタイマー表示の更新頻度が高いため、`TimerState`を高頻度でpollingします。

```text
attach
↓
get_run
get_attempt
get_runtime_state

約40Hz:
get_timer_state

revision変更:
run_revision     → get_run
attempt_revision → get_attempt
runtime_revision → get_runtime_state
```

`get_timer_state`は高頻度取得を想定した軽量APIです。Run / Attempt / Runtimeの詳細は
高頻度で取得せず、各revisionの変化を検出したときだけ再取得してください。

### DivergenceSplitter

DivergenceSplitterは判定のためにイベントを主軸にします。

```text
attach
↓
get_run
↓
eventsを購読

判定成立
↓
timer_operation
↓
OperationResponse.timer_state
```

通常時は`get_timer_state`を高頻度pollingせず、接続・復旧・整合性確認の用途に限定して
利用できます。操作後の状態は`OperationResponse.timer_state`から取得できます。

## イベントストリーム

イベントは`/bridge/v2/events`へ接続したすべてのクライアントへ`BridgeEvent`として
broadcastされます。イベント種別ごとの購読機能はなく、接続したクライアントはすべての
`BridgeEvent`を受信します。

```protobuf
message BridgeEvent {
  uint64 session_id = 1;
  uint64 event_sequence = 2;
  BridgeEventType type = 3;
  TimerState timer_state = 4;
}
```

状態変更イベントにはイベント処理後の`TimerState`が含まれます。ハートビートは1秒周期で
配信され、`timer_state`を含みません。受信時は必ず`BridgeEvent.type`を先に判定してください。
`EVENT_HEARTBEAT`で`timer_state`が未設定なのは正常です。それ以外のイベントでは
`timer_state`を必須として扱います。

イベント種別は少なくとも次のとおりです。

```text
EVENT_TIMER_STARTED
EVENT_TIMER_SPLIT
EVENT_TIMER_SKIPPED
EVENT_TIMER_UNDO
EVENT_TIMER_RESET
EVENT_TIMER_PAUSED
EVENT_TIMER_RESUMED

EVENT_GAME_TIME_INITIALIZED
EVENT_GAME_TIME_SET
EVENT_GAME_TIME_PAUSED
EVENT_GAME_TIME_RESUMED

EVENT_RUN_CHANGED
EVENT_RUNTIME_CHANGED

EVENT_HEARTBEAT
```

`EVENT_STATE_SNAPSHOT`は削除しました。Runの変更は`EVENT_RUN_CHANGED`、
Runtime状態の変更は`EVENT_RUNTIME_CHANGED`で通知されます。詳細はRPCで再取得して
ください。

### `event_sequence`

`event_sequence`は、クライアントが受信すべきイベントの欠落を検出するための単調増加番号です。
状態変更イベントごとに1増加します。送信に失敗した番号も再利用されません。

ハートビート自身はsequence対象ではありません。ハートビートには、最後に送信成功または失敗が
確定したsequence対象イベントの番号が入ります。そのため、状態が変わらなければ複数の
ハートビートが同じ番号を通知します。

クライアントが最後に処理した番号より大きい番号をハートビートが通知した場合、途中のイベントを
受信できていません。欠落したイベントを推測して補完しないでください。

### `session_id`

`session_id`はBridgeの配信セッションを識別します。Bridgeの再起動後は新しい値になります。
異なる`session_id`を受信した場合、以前の`event_sequence`との連続性を仮定しないでください。

## 接続手順

起動時は次の順序を推奨します。

1. イベントendpointへWebSocketで接続し、`BridgeEvent`の受信を開始する
2. RPCの`attach`で`session_id`と`TimerState`を取得する
3. `get_run` / `get_attempt` / `get_runtime_state`で各状態を取得する
4. 取得した状態を初期状態として適用し、その時点で受信済みの`event_sequence`を記録する
5. 同じ`session_id`のイベントをsequence順に処理する
6. 状態取得前からキューに残っていた、記録済みsequence以下のイベントは再適用しない

WebSocket接続を確立する前のイベントは受信できません。初期状態はイベントから推測せず、
必ずRPCで取得した各状態を基準にしてください。

## 切断・欠落からの復旧

ハートビートのタイムアウト判定には、システム時計ではなく単調時計を使用します。次のいずれかを
検出したら、通常の評価と操作送信を停止します。

- 最後のハートビートから3秒以上経過した
- sequence対象イベントが連続していない
- ハートビートが未処理の大きい`event_sequence`を通知した
- `session_id`が変わった

復旧手順は次のとおりです。

1. シナリオ評価とアクション送信を停止する
2. RPCで最新の状態を取得し直す（`get_timer_state` / `get_run` / `get_attempt` /
   `get_runtime_state`）
3. 取得した状態をRESYNCとして適用する
4. ConditionやRuleなど、履歴に依存するクライアント内部状態をリセットする
5. 取得した`session_id`と`event_sequence`を新しい基準にする
6. 評価を再開する

欠落した操作や状態をクライアント側で再現しないでください。状態復旧のauthorityはRPCで
取得できる現在の状態です。

## デバッグCLI

同梱のPython CLIを使うと、独自クライアントを実装する前にBridgeのRPCとイベントを確認できます。

```powershell
cd tools/livesplit-bridge-cli
uv sync --locked
uv run python scripts/generate_proto.py
uv run livesplit-bridge timer-state
uv run livesplit-bridge run
uv run livesplit-bridge attempt
uv run livesplit-bridge runtime
uv run livesplit-bridge timer start
uv run livesplit-bridge game-time set 12.345
uv run livesplit-bridge events
```

詳しいコマンドと接続先の変更方法は
[`tools/livesplit-bridge-cli/README.md`](tools/livesplit-bridge-cli/README.md)を参照してください。

## 互換性

- Bridgeの製品バージョンと`protocol_version`は別の値です。接続可否はrelease tagではなく、
  RPCの`protocol_version`と利用するProtobuf packageで判定してください。
- v2はv1との互換性を持ちません。`protocol_version`とProtobuf packageを必ず確認して
  ください。
- 未知のenum値や将来追加されるfieldを安全に扱えるProtobuf実装を使用してください。
- `BridgeEvent`は`type`を確認してから、イベント種別に応じたfieldを参照してください。
- `protocol_version`が未対応の場合は接続を継続せず、利用者へ明確なエラーを表示してください。
- クライアントが依存する仕様変更では、対応する`.proto`とクライアント実装を同時に更新して
  ください。
