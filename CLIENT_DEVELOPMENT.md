# LiveSplit.Bridge JSON v4 クライアント開発ガイド

この作業ブランチはJSON Protocol v4の開発候補です。公開Releaseと`main`はProtobuf Protocol v3のままの場合があり、このブランチとは通信互換性がありません。同梱Debug CLIはJSON v4に対応していますが、v3のReleaseや`main`には接続できません。v4はDraft PRで検証中であり、正式採用・Releaseは確定していません。

## 接続

既定ポートは`54000`です。LiveSplitのレイアウトで追加した`LiveSplit Bridge`コンポーネントの設定から変更できます。

| 用途 | エンドポイント |
| --- | --- |
| RPC | `ws://127.0.0.1:<port>/bridge/v4/rpc` |
| Events | `ws://127.0.0.1:<port>/bridge/v4/events` |

通信は1つのJSONオブジェクトを1つのWebSocket Text Messageとして送ります。RPC Requestの上限はUTF-8で64 KiBです。Bridgeは`127.0.0.1`だけで待ち受けるため、同じPC上のクライアントから接続してください。

## RPC Request

すべてのRequestは`request_id`と`method`を持ちます。`request_id`は`uint32`（`0`～`4294967295`）です。ID `0`も有効です。同じ接続の未完了Request間でIDを重複させないでください。Requestが完了した後は再利用できます。

| Method | `params` | 権限 |
| --- | --- | --- |
| `get_timer_state` | なし | Query |
| `get_run` | なし | Query |
| `get_attempt` | なし | Query |
| `get_context_state` | なし | Query |
| `get_completed_count` | なし | Query |
| `timer_operation` | `operation`を持つオブジェクト | Control |
| `game_time_operation` | `operation`と操作に応じた値 | Control |

Queryの例:

```json
{"request_id":0,"method":"get_timer_state"}
```

Run情報を取得する場合:

```json
{"request_id":3,"method":"get_run"}
```

Timer操作は`start`、`split`、`skip`、`undo`、`reset`、`pause`、`resume`です。

```json
{"request_id":1,"method":"timer_operation","params":{"operation":"start"}}
```

Game Time操作は`initialize`、`set`、`pause`、`resume`です。`set`では100ナノ秒単位の符号付き整数`ticks`が必須です。それ以外の操作に`ticks`を付けることはできません。

```json
{"request_id":2,"method":"game_time_operation","params":{"operation":"set","ticks":-123456}}
```

## RPC Response

Responseには`request_id`（Requestと同じ値）と、非ゼロ`uint32`の`session_id`が含まれます。成功結果は`result`、エラーは`error`に入り、両方を同時に返すことはありません。

Query成功の例:

```json
{"result":{"phase":"not_running","split_index":-1,"real_time_ticks":0,"game_time_ticks":null,"is_game_time_initialized":false,"is_game_time_paused":false},"request_id":0,"session_id":123456789}
```

戻り値のないControl成功は`result: null`です。

```json
{"result":null,"request_id":1,"session_id":123456789}
```

エラーの例:

```json
{"error":{"code":"permission_denied","message":"Control permission is required."},"request_id":1,"session_id":123456789}
```

エラーコードは`invalid_request`、`invalid_argument`、`operation_failed`、`internal_error`、`permission_denied`です。時間値は100ナノ秒単位のticks整数、Enumは小文字の文字列、画像はPNGのBase64文字列または`null`です。生成済みの通信Schemaは[`tools/schema-poc/schema-results`](tools/schema-poc/schema-results)にあります。

## Events

Events endpointは接続中のクライアントへJSON Text Messageをbroadcastします。各Eventは`session_id`、`event_sequence`、`type`、`timer_state`を持ちます。

| `type` | `timer_state` |
| --- | --- |
| `timer_started` | TimerState |
| `timer_split` | TimerState |
| `timer_skipped` | TimerState |
| `timer_undo` | TimerState |
| `timer_phase_changed` | TimerState |
| `timer_reset` | TimerStateまたは`null` |
| `run_changed` | 必須の`null` |
| `context_changed` | 必須の`null` |

例:

```json
{"type":"timer_started","timer_state":{"phase":"running","split_index":0,"real_time_ticks":0,"game_time_ticks":null,"is_game_time_initialized":false,"is_game_time_paused":false},"session_id":123456789,"event_sequence":1}
```

Runが変更された場合は`timer_state: null`を送ります。

```json
{"type":"run_changed","timer_state":null,"session_id":123456789,"event_sequence":2}
```

`event_sequence`は同一Session内で`1`から増加し、`4294967295`の次は`1`へ循環します。`0`は使用されません。session_idが変わった場合は新しい系列です。Eventの欠落、順序の飛び、切断を検出した場合はQueryで状態を再同期してください。

## OriginとControl権限

EventsとQuery RPCはOriginに関係なく利用できます。Control RPCはOriginヘッダーなし、または`localhost`、`127.0.0.1`、`::1`をhostとするHTTP/HTTPS Originから許可されます。その他のOrigin（`Origin: null`を含む）には`permission_denied`を返します。拒否後もRPC接続は維持されます。Origin判定はブラウザー起点のControl制限であり、認証ではありません。

## 不正な入力と再同期

Binary Message、JSON構文エラー、JSONオブジェクト以外、欠落または不正な`request_id`にはResponseを返しません。有効なIDがある未知のmethodや不正な既知Requestには、そのIDを保持した`invalid_request`を返します。重複プロパティ名と未知プロパティも拒否されます。64 KiBを超えるRequestではWebSocketをMessage Too Big（close status `1009`）で終了します。

接続後はまずEventsを購読し、その後RPCで必要なStateを取得してください。RPCとEventsは別接続なので、両者の到着順は保証されません。`session_id`の変更、Event sequenceの不連続、または切断後はQueryで現在値を取り直し、新しい基準からEvents処理を再開してください。Control Responseを受け取れず操作結果が不明な場合、同じControlを自動再送すると二重実行になる可能性があります。

## 関連資料

- JSON WebSocket実装と検証状況: [`docs/phase3c-json-transport.md`](docs/phase3c-json-transport.md)
- Protocol v4のC# DTOと生成Schema: [`tools/schema-poc/README.md`](tools/schema-poc/README.md)
- 同梱Debug CLI: [`tools/livesplit-bridge-cli/README.md`](tools/livesplit-bridge-cli/README.md)（JSON Protocol v4用）
