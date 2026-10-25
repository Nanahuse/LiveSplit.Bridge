# LiveSplit.Bridge Protocol v4 Reference

## 1. 概要

Protocol v4はLiveSplitの状態取得と操作にRPC、変更通知にEventsを使用するJSON
プロトコルです。この文書はAPIの意味、処理、通信順序、配送上の保証を規定します。
接続から実装する手順は[クライアント開発ガイド](client-guide.md)を参照してください。

現在の仕様はJSON v4のみを対象とします。旧Protobuf v3とは互換性がありません。

メッセージの構造・型・値の制約は[公式JSON Schema](#9-json-schema)で定義します。
各フィールドの型一覧はSchemaを参照し、この文書では意味と通信上の規則を説明します。
Schemaに適合していても、操作の実行やイベントの配送が成功するとは限りません。

## 2. Transport

| 用途 | WebSocketエンドポイント |
| --- | --- |
| Requestを送信しResponseを受信する | `ws://127.0.0.1:<port>/bridge/v4/rpc` |
| Eventを受信する | `ws://127.0.0.1:<port>/bridge/v4/events` |

BridgeはIPv4ループバック`127.0.0.1`で待ち受けます。ポートの設定は
[READMEの設定](../README.md#設定)を参照してください。

1つのJSONオブジェクトを1つのWebSocket Text Messageとして送受信します。
複数オブジェクトの配列によるbatch RPCはありません。WebSocketの断片化は利用できます。
Eventsは接続によって購読が始まり、購読Requestや購読開始の応答はありません。
Eventsへのクライアントメッセージは購読条件を変更しません。

## 3. RPC

### RequestとResponse

Requestには`request_id`と`method`が必要です。Queryに`params`は付けません。
Controlには操作に応じた`params`が必要です。

```json
{"request_id":0,"method":"get_timer_state"}
```

ResponseはRequestと同じ`request_id`、Bridgeの`session_id`、成功時の`result`または
失敗時の`error`を持ちます。`result`と`error`を同時に返すことはありません。

```json
{"request_id":0,"session_id":123456789,"result":{"phase":"not_running","split_index":-1,"real_time_ticks":0,"game_time_ticks":null,"is_game_time_initialized":false,"is_game_time_paused":false}}
```

Control成功時は`result: null`です。成功は呼び出しが例外なく完了したことを示します。
LiveSplitが現在の状態で操作を無視する場合も成功になり、状態変化やEvent発行を約束しません。

```json
{"request_id":1,"session_id":123456789,"result":null}
```

### IDとSession

`request_id`の範囲はRequest Schemaで定義し、`0`も有効です。クライアントは同じRPC
接続の未完了Request間でIDを重複させず、ResponseをIDで照合してください。
完了後は再利用できます。サーバーはIDによる重複排除を行いません。
同じIDを再送しても以前の応答の再取得にはならず、操作が再実行される可能性があります。

`session_id`はRuntimeの生成時に選ばれる非ゼロのIDです。同じRuntimeのRPCとEventsで
共通し、接続単位では変わりません。再起動やポート設定の適用でRuntimeが作り直されると
IDも再生成されます。乱数のため、過去のSessionすべてに対する一意性は保証しません。

RPCとEventsは別接続です。ControlのResponseと、それに関連するEventの到着順は
保証しません。複数Queryの結果をまとめた原子的なスナップショットも提供しません。

### エラー

```json
{"request_id":1,"session_id":123456789,"error":{"code":"permission_denied","message":"Control permission is required."}}
```

| `code` | 意味 |
| --- | --- |
| `invalid_request` | IDを読み取れたRequestの構造・method・引数が不正、または未対応 |
| `invalid_argument` | DTOで定義されたコード。現在のRPC経路では発行していない |
| `operation_failed` | Controlを実行するAdapterが例外を返した |
| `internal_error` | Queryなどの処理中に予期しない例外が発生した |
| `permission_denied` | Control権限がない |

`message`は説明用の文字列で、固定文言として扱わないでください。
`operation_failed`は操作がまったく実行されなかったことを保証しません。

### 不正な入力

64 KiB以内のメッセージについては次のとおりです。サイズ超過の扱いは
[制限](#8-制限)を参照してください。

| 入力 | 処理 |
| --- | --- |
| Binary Message、JSON構文エラー、JSONオブジェクト以外 | Responseを返さない |
| `request_id`の欠落、不正な型・範囲、同名IDプロパティの重複 | Responseを返さない |
| 有効なIDがある未知のmethod、不正な`params`、未知プロパティ | 同じIDの`invalid_request` |
| ID以外のプロパティ名の重複。ネスト内も対象 | 同じIDの`invalid_request` |
| 複数method・operationをカンマで連結した値 | 同じIDの`invalid_request` |

これらの入力や権限拒否だけではRPC接続を閉じません。クライアントはRequestごとに
タイムアウトを設け、Responseがないことを成功や未実行の根拠にしないでください。

## 4. Methods

### Query

すべてのQueryは`params`を持たず、Query権限で利用できます。結果の構造は
[Response Schema](../schemas/v4/rpc-response.schema.json)の該当する`$defs`を参照してください。

| `method` | 結果DTO | 意味 |
| --- | --- | --- |
| `get_timer_state` | `TimerState` | 現在のphase、Split位置、実時間・ゲーム内時間、ゲーム内時間の初期化・停止状態 |
| `get_run` | `RunState` | 公開済みのゲーム名・カテゴリ、オフセット、ファイル・レイアウトのパス、Metadata、Segment構造、比較タイム、Best Segment |
| `get_run_icons` | `RunIcons` | 取得時のRunのゲームアイコンと、Segment順の全アイコン |
| `get_attempt` | `AttemptState` | Runの試行回数と、各Segmentに記録された今回のSplitTime・カスタム変数 |
| `get_context_state` | `ContextState` | 公開済みの計測方式、比較対象、現在のカスタム変数値 |
| `get_completed_count` | `CompletedCount` | RunのAttemptHistoryで実時間が記録されている試行の件数 |

`get_run`と`get_context_state`は公開済みのスナップショットを返します。Runは参照の
置換、手動変更通知、Resetに応じて更新されます。Contextは更新ループで約100 ms以上の
間隔を空けて観測されます。Queryを送っても、この2つをその場で再観測するわけではありません。

`get_run`には画像を含みません。`RunState.metadata.variables`はRunの
`VariableValueNames`に対応し、現在のカスタム変数値は`ContextState.custom_variables`で
取得します。SegmentごとにSplit時に記録された値は`AttemptSegment.custom_variables`です。

### Timer Control

`timer_operation`に`params.operation`を指定して、タイマーを操作します。

```json
{"request_id":1,"method":"timer_operation","params":{"operation":"start"}}
```

すべてControl権限が必要です。LiveSplitの`TimerModel`に操作を渡します。
現在の状態に適用できない操作は、LiveSplit側で何もせず戻る場合があります。

| `operation` | 現在の処理 |
| --- | --- |
| `start` | 未開始なら計測を開始する |
| `split` | 計測中かつ実時間が正なら現在のSplitを記録し、次へ進む。最後なら終了する |
| `skip` | 計測中または一時停止中に、最後以外のSegmentをスキップする |
| `undo` | 未開始以外でSplit位置が先頭より後なら1つ戻す。終了済みなら計測中へ戻る |
| `reset` | 未開始以外なら試行をリセットする。比較タイムや履歴を更新する場合がある |
| `pause` | `TimerModel.Pause()`を呼ぶ。下の状態表を参照 |
| `resume` | `pause`と同じ`TimerModel.Pause()`を呼ぶ |

現在の`pause`と`resume`は両方とも切り替え操作です。名前から状態を固定する操作と
解釈しないでください。

| 操作前のphase | どちらかを1回実行した後 |
| --- | --- |
| `not_running` | `running`。計測を開始する |
| `running` | `paused` |
| `paused` | `running` |
| `ended` | 変化しない |

### Game Time Control

`game_time_operation`に`params.operation`を指定して、ゲーム内時間を操作します。

```json
{"request_id":2,"method":"game_time_operation","params":{"operation":"set","ticks":-123456}}
```

すべてControl権限が必要です。

| `operation` | 意味 | `ticks` |
| --- | --- | --- |
| `initialize` | ゲーム内時間を初期化済みにする | 付けない |
| `set` | ゲーム内時間を指定値に設定する | 必須。100 ns単位の符号付き整数 |
| `pause` | ゲーム内時間を一時停止状態にする | 付けない |
| `resume` | ゲーム内時間を再開状態にする | 付けない |

Game Time専用のEventはありません。操作後や継続的な表示に必要な状態は
`get_timer_state`で確認してください。

## 5. Events

各Eventは`session_id`、`event_sequence`、`type`、`timer_state`を持ちます。
`timer_state: null`もフィールドを省略せず送ります。

```json
{"session_id":123456789,"event_sequence":1,"type":"timer_started","timer_state":{"phase":"running","split_index":0,"real_time_ticks":0,"game_time_ticks":null,"is_game_time_initialized":false,"is_game_time_paused":false}}
```

| `type` | 発行のきっかけ | `timer_state` |
| --- | --- | --- |
| `timer_started` | LiveSplitの開始通知 | 操作後の`TimerState` |
| `timer_split` | Split通知。最終Splitによる終了も含む | 操作後の`TimerState` |
| `timer_skipped` | Skip通知 | 操作後の`TimerState` |
| `timer_undo` | Undo通知 | 操作後の`TimerState` |
| `timer_phase_changed` | PauseまたはResume通知 | 操作後の`TimerState` |
| `timer_reset` | Reset通知 | 操作後の`TimerState`。取得に失敗した場合は`null` |
| `run_changed` | 画像以外の公開Run情報に差がある | `null` |
| `context_changed` | 観測したContextに差がある | `null` |

Runの画像だけが変わっても`run_changed`は発行されません。Contextの観測間隔の間に
起きて元へ戻った変更は、個別のEventにはなりません。
Eventは継続的な時刻配信ではなく、状態を取り直すきっかけとして扱ってください。

### sequenceと通信順序

`event_sequence`は同じSession内で`1`から増加し、最大値の次は`1`へ循環します。
`0`は使いません。値は接続単位でなくRuntime共通で、送信待ちキューへ入れる前に
割り当てます。購読開始時の最初の値が`1`とは限りません。

sequenceの割り当てとキューへの投入は直列化されます。ResetでRun情報も変わった場合は、
`timer_reset`、`run_changed`の順で投入します。ただし、両方を受信できる保証はありません。
RPC Responseとの前後関係から、どのQuery時点のEventかを判定することもできません。

### 配送上の保証

送信待ちキューは全接続共通で最大256件です。満杯の場合は新しいEventを破棄します。
履歴再送、受信確認、購読者ごとの配送補償、sequenceを指定した再開は提供しません。
最後のEventが欠落して後続が来ない場合、sequenceの確認だけでは欠落を検出できません。

クライアントは切断、Session変更、sequence不連続を検出したらQueryで再同期してください。
欠落を検出できない場合にも備え、用途に応じて定期的にQueryで現在値を確認します。
`get_run`などのResponseにEventの基準sequenceやrevisionはありません。
初期QueryとEvents購読を組み合わせても、全状態を同一時点に確定する保証はありません。

## 6. Data Types

構造・必須項目・null許容・数値範囲・Enumの全値はSchemaを参照してください。
プロパティ名と公開するEnum文字列はsnake_caseです。EnumをJSON数値として送ることはできません。

### 時刻とSplit位置

時刻は100 ns単位のticksです。1秒は`10000000` ticksで、負数も使用します。
JSON数値は符号付き64 bit範囲を扱うため、JavaScriptなどでは通常の浮動小数点数で
全範囲を正確に表せません。全範囲を扱うクライアントは精度を保つJSON処理を用いてください。
文字列に置き換えて送信することはできません。

`TimerState.split_index`はLiveSplitの現在位置です。未開始時は`-1`、計測中のSegmentは
0始まり、終了時はSegment数になります。`real_time_ticks`は実時間がない場合に`0`、
`game_time_ticks`や`TimeValue`の各時刻は値がない場合に`null`です。
phaseと計測方式の`unspecified`は、LiveSplitの値を対応付けられない場合の値です。

### 画像

`RunIcons.game_icon`と`segment_icons`の各要素はBase64のPNG文字列です。
画像がない場合は`null`で、`segment_icons`の配列自体は`null`になりません。
画像がないSegmentも`null`要素として残し、取得時のRunのSegment順を保ちます。

`get_run`と`get_run_icons`の間にRunが切り替わる場合があります。両者を結び付ける
専用revision、アイコン専用Event、画像内容の変更検出はありません。

## 7. 権限

QueryとEventsはOriginに関係なく利用できます。ControlはRPC接続開始時の
`Origin`ヘッダーで判定します。

| Origin | Control |
| --- | --- |
| ヘッダーなし、または空 | 許可 |
| HTTP/HTTPSでhostが`localhost`、`127.0.0.1`、`::1` | 許可 |
| その他。`Origin: null`、不正なURI、HTTP/HTTPS以外も含む | `permission_denied` |

Originによる判定はブラウザー起点のControlを制限する仕組みです。クライアントの
認証ではありません。拒否後も同じRPC接続でQueryを利用できます。
`::1`は許可するOriginのhostであり、Bridgeの待ち受けアドレスではありません。

## 8. 制限

| 対象 | 制限 |
| --- | --- |
| RPC Request | UTF-8で64 KiBまで。超過するとclose status `1009`で切断 |
| 断片化したRPC Request | 再構成したメッセージ全体に同じ上限を適用 |
| RPC Response | Requestと同じ上限は設けない。アイコンを含む応答は64 KiBを超える場合がある |
| Eventキュー | 全接続共通256件。満杯時は新しいEventを破棄 |
| 再送・冪等性 | Event履歴再送とControl重複排除は提供しない |

Requestのサイズ検査はWebSocketSharpがメッセージを再構成した後に行います。
受信中のメモリー使用量を64 KiB以下に抑える保証ではありません。

## 9. JSON Schema

公式SchemaはDraft 2020-12です。各ファイルはローカルの`$defs`と`$ref`を持ち、
ファイル単位で参照を解決できます。

| メッセージ | Schema |
| --- | --- |
| Request | [rpc-request.schema.json](../schemas/v4/rpc-request.schema.json) |
| Response | [rpc-response.schema.json](../schemas/v4/rpc-response.schema.json) |
| Event | [bridge-event.schema.json](../schemas/v4/bridge-event.schema.json) |

[C# DTO](../src/LiveSplit.Bridge.Json/Models.cs)は実装の通信モデルとSchema生成の入力です。
Schemaは公開する構造と値の制約、この文書はAPIの意味と通信上の規則を定義します。
Serializerが受理できる値すべてを、公開仕様として許容するわけではありません。

現時点では、Serializerは`session_id`・`event_sequence`の`0`、一部コレクション内の
`null`、参照DTOのルートの`null`を受理できますが、公開Schemaはこれらを許容しません。
Bridgeは非ゼロIDと、Schemaに従うコレクションを構築して送信します。
クライアントはSchemaの制約を使用してください。

反対に、Schemaは画像文字列のBase64・PNG内容まで検証しません。Serializerの
`byte[]`読み込みは不正なBase64を拒否しますが、PNG形式の保証は画像デコードでも
確認する必要があります。また、System.Text.Jsonが受理する大文字小文字の異なるEnum表記は
公開Schemaの値に含めません。クライアントはSchemaにある文字列を送信してください。

これらは実装の受理範囲と公開仕様の差です。この文書の整理では実装を変更していません。
生成方法と保守上の注意は[Schemaの生成・検証](schema-generation.md)を参照してください。
