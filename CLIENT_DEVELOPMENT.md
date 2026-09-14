# LiveSplit.Bridge クライアント開発ガイド

この文書は、LiveSplit.Bridgeへ接続する外部アプリケーションを開発する方向けです。

## プロトコル

プロトコルの正本は[`proto/livesplit/bridge/v1`](proto/livesplit/bridge/v1)にある
Protobufスキーマです。現在の`protocol_version`は`1`です。

| ファイル | 内容 |
|---|---|
| `bridge.proto` | RPCのRequestとResponse |
| `common.proto` | snapshot、操作種別、イベント、エラー |

利用する言語のProtobufコンパイラーでスキーマからコードを生成してください。既存のfieldを
独自に読み替えたり、同名のデータ型を手書きで複製したりせず、スキーマを通信形式の正本として
扱います。

## 接続先

Bridgeは既定でローカルPC上の次のエンドポイントを使用します。

| 用途 | エンドポイント | ZeroMQパターン |
|---|---|---|
| 状態取得・操作 | `tcp://127.0.0.1:54000` | REQ/REP |
| イベント監視 | `tcp://127.0.0.1:54001` | PUB/SUB |

ポートはLiveSplitのコンポーネント設定で変更できます。クライアント側でも接続先を設定可能に
してください。Bridgeはloopbackだけにbindするため、別のPCから直接接続することはできません。

## RPC

すべての`Request`に次を設定します。

- `protocol_version`: `1`
- `request_id`: クライアントが要求と応答を対応付けるための一意な値
- `body`: 実行する要求を1つだけ設定

`Response`では最初に`request_id`と`body`を確認してください。`error`が設定されている場合は
要求が処理されていないものとして扱います。

利用可能な要求は次のとおりです。

| Request body | 用途 |
|---|---|
| `attach` | セッションIDと現在のフルsnapshotを取得 |
| `get_snapshot` | 現在のフルsnapshotを取得 |
| `get_run` | 現在LiveSplitにロードされているRunの情報を取得 |
| `timer_operation` | TimerのStart、Split、Skip、Undo、Reset、Pause、Resume |
| `game_time_operation` | Game Timeの初期化、設定、Pause、Resume |

操作結果は`OperationResponse.success`で判定します。成功時のsnapshotを、操作が実行された後の
Bridge状態として扱ってください。Game Timeの`ticks`は100ナノ秒単位です。

ハートビートは、Timer操作を送信する直前の状態確認を代替しません。操作の前提状態が重要な
場合は、必要に応じてRPCで最新snapshotを取得して確認してください。

## Run API

`get_run`は、呼び出した時点でLiveSplitにロードされているRunの情報を`RunSnapshot`として
返します。LiveSplitの内部型をそのまま公開するものではありません。呼び出しのたびに現在の
Runから生成するため、Bridgeが過去のRunや過去revisionのコピーを保持することはありません。

主なフィールドは次のとおりです。

| フィールド | 内容 |
|---|---|
| `session_id` | 現在のBridgeセッション |
| `run_revision` | 現在のRunのrevision |
| `captured_state_revision` | この`RunSnapshot`を生成した時点の`state_revision` |
| `game_name` / `category_name` | `IRun`由来のゲーム名・カテゴリ名 |
| `offset_ticks` | Runの開始オフセット。100ナノ秒単位 |
| `file_path` / `layout_path` | 保存済みRun/Layoutのパス。存在しない場合はunset |
| `metadata` | Run Metadata。`run_id`、platform、region、emulator使用、変数、custom variable |
| `comparisons` | Run全体で利用可能なComparison名の一覧 |
| `segments` | Segment一覧 |
| `attempt_count` | 現在のRunのAttempt数 |

`segments`の各`SegmentInfo`は`index`（0始まり）と`name`を持ちます。`comparisons`は
Run全体のComparison一覧を正とし、各Segmentには同じ名前の`ComparisonTime`が入ります。
値が存在しないComparisonも一覧には残り、対応する時間値が無い場合は`TimeValue`の
real time / game timeがunsetになります。

時間値は`TimeValue`で表し、`real_time_ticks` / `game_time_ticks`は100ナノ秒単位です。
存在しない値はunsetで、空文字列や0で代用しません。Personal Bestを専用フィールドには
分けておらず、`Personal Best`などのComparison名をそのまま使用します。

### Run変更と`run_revision`

`run_revision`はBridge起動時に`1`から始まり、Runが変更されるたびに増加します。同じ
`session_id`の中では単調増加しますが、Bridgeを再起動すると`session_id`と`run_revision`は
新しい値になります。クライアントは`(session_id, run_revision)`をRunSnapshotのidentityとして
扱えます。

Run変更時は、更新後の`run_revision`を持つ`TimerSnapshot`を含む`EVENT_RUN_CHANGED`が
配信されます。通常のTimerおよびGame Timeイベント、定期snapshotにも現在の`run_revision`が
含まれるため、クライアントは`TimerSnapshot.run_revision`の変化だけを見てRun変更を検出し、
必要になった時点で`get_run`により詳細を再取得できます。`run_revision`が変化していなければ、
`get_run`を再送する必要はありません。

LiveSplitで別の`.lss`ファイルを開いた場合も、New Splitsで未保存のRunに切り替えた場合も、
通常のRun変更として扱われます。未保存Runでは`file_path`、Layoutが無い場合は`layout_path`が
unsetになります。

### 再接続時の同期

PUB/SUBは到達保証を持たないため、`run_revision`だけに依存してRun情報を再構築しないで
ください。`session_id`の変更、`event_sequence`の欠落、ハートビートのタイムアウトを検出した
場合は、通常の復旧手順に加えて`get_run`で最新の`RunSnapshot`を再取得し、記録済みの
`run_revision`を更新してください。LiveSplitのRunが正であり、`get_run`は常に現在のRunを
返します。

### 今回対象外

以下は現在のRun APIに含まれません。将来必要になった場合に別APIとして追加します。

- Game icon / Segment icon
- Attempt History / Segment History
- Auto Splitter Settings
- 進行中AttemptのSplitTime

## イベントストリーム

イベントは既存のPUB/SUB endpointから`BridgeEvent`として届きます。

- 状態変更イベントにはイベント処理後の`TimerSnapshot`が含まれます。
- 定期フルsnapshotは30秒周期で配信され、通常のsequence対象です。
- ハートビートは1秒周期で配信され、snapshotを含みません。

受信時は必ず`BridgeEvent.type`を先に判定してください。`EVENT_HEARTBEAT`でsnapshotが未設定
なのは正常です。それ以外の現在のイベントではsnapshotを必須として扱います。

### `event_sequence`

`event_sequence`は、クライアントが受信すべきイベントの欠落を検出するための単調増加番号です。
状態変更イベントと定期snapshotごとに1増加します。送信に失敗した番号も再利用されません。

ハートビート自身はsequence対象ではありません。ハートビートには、最後に送信成功または失敗が
確定したsequence対象イベントの番号が入ります。そのため、状態が変わらなければ複数の
ハートビートが同じ番号を通知します。

```text
状態イベント sequence=10
heartbeat   sequence=10
heartbeat   sequence=10
状態イベント sequence=11
heartbeat   sequence=11
```

クライアントが最後に処理した番号より大きい番号をハートビートが通知した場合、途中のイベントを
受信できていません。欠落したイベントを推測して補完しないでください。

### `session_id`

`session_id`はBridgeの配信セッションを識別します。Bridgeの再起動やPUBセッションの再作成後は
新しい値になります。異なる`session_id`を受信した場合、以前の`event_sequence`との連続性を
仮定しないでください。

## 接続手順

起動時は次の順序を推奨します。

1. SUB socketをイベントendpointへ接続し、すべての`BridgeEvent`を購読する
2. RPCの`attach`で`session_id`とフルsnapshotを取得する
3. snapshotを初期状態として適用し、その`event_sequence`を記録する
4. 同じ`session_id`のイベントをsequence順に処理する
5. snapshot取得前からキューに残っていた、記録済みsequence以下のイベントは再適用しない

ZeroMQ PUB/SUBでは購読確立前のイベントを受信できません。初期状態はイベントから推測せず、
必ずRPC snapshotを基準にしてください。

## 切断・欠落からの復旧

ハートビートのタイムアウト判定には、システム時計ではなく単調時計を使用します。次のいずれかを
検出したら、通常の評価と操作送信を停止します。

- 最後のハートビートから3秒以上経過した
- sequence対象イベントが連続していない
- ハートビートが未処理の大きい`event_sequence`を通知した
- `session_id`が変わった

復旧手順は次のとおりです。

1. シナリオ評価とアクション送信を停止する
2. RPCで最新のフルsnapshotを取得する
3. snapshotをRESYNCとして適用する
4. ConditionやRuleなど、履歴に依存するクライアント内部状態をリセットする
5. 取得した`session_id`と`event_sequence`を新しい基準にする
6. 評価を再開する

欠落した操作や状態をクライアント側で再現しないでください。状態復旧のauthorityはRPCの
フルsnapshotです。

## デバッグCLI

同梱のPython CLIを使うと、独自クライアントを実装する前にBridgeのRPCとイベントを確認できます。

```powershell
cd tools/livesplit-bridge-cli
uv sync --locked
uv run python scripts/generate_proto.py
uv run livesplit-bridge snapshot
uv run livesplit-bridge timer start
uv run livesplit-bridge game-time set 12.345
uv run livesplit-bridge events
```

詳しいコマンドと接続先の変更方法は
[`tools/livesplit-bridge-cli/README.md`](tools/livesplit-bridge-cli/README.md)を参照してください。

## 互換性

- Bridgeの製品バージョンと`protocol_version`は別の値です。接続可否はrelease tagではなく、
  RPCの`protocol_version`と利用するProtobuf packageで判定してください。
- 未知のenum値や将来追加されるfieldを安全に扱えるProtobuf実装を使用してください。
- `BridgeEvent`は`type`を確認してから、イベント種別に応じたfieldを参照してください。
- `protocol_version`が未対応の場合は接続を継続せず、利用者へ明確なエラーを表示してください。
- クライアントが依存する仕様変更では、対応する`.proto`とクライアント実装を同時に更新して
  ください。
