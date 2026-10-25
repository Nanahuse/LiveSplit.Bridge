# クライアント開発ガイド

LiveSplit.Bridgeへの接続から、状態取得、操作、変更通知と再同期までの実装手順です。
先に[導入・設定](installation.md)に従ってBridgeを有効にしてください。
厳密な入力条件と保証は[Protocol Reference](protocol.md)を参照してください。

JSON Protocol v4は旧Protobuf v3と互換性がありません。Bridgeとクライアントの両方を
JSON v4対応のものに揃えます。以降の例はブラウザーのJavaScriptで実行できます。

## 1. 接続

RPCは状態取得と操作、Eventsは変更通知に使用する別々のWebSocketです。
既定のポートを使う場合は次のように接続します。Eventsは接続だけで購読でき、
購読メッセージを送る必要はありません。

```javascript
const base = "ws://127.0.0.1:54000/bridge/v4";
function connect(path, onMessage) {
  return new Promise((resolve, reject) => {
    const socket = new WebSocket(`${base}/${path}`);
    if (onMessage) socket.addEventListener("message", onMessage);
    socket.addEventListener("open", () => resolve(socket), { once: true });
    socket.addEventListener("error", reject, { once: true });
  });
}

const events = await connect("events", event => {
  console.log("Event", JSON.parse(event.data));
});
const rpc = await connect("rpc");
```

ローカルHTMLやOBSのブラウザーソースでもQueryとEventsを利用できます。Controlを
使うWebページはlocalhostなど許可されたOriginから開いてください。
`Origin: null`を送るページは操作が拒否されます。

## 2. RPCによる状態取得

RequestをJSON文字列として送信し、Responseの`request_id`で照合します。次の例は
上で作った`rpc`接続を使います。ID `0`から始め、応答待ちとタイムアウトを管理します。

```javascript
let nextId = 0;
const pending = new Map();
rpc.addEventListener("message", event => {
  const response = JSON.parse(event.data);
  const request = pending.get(response.request_id);
  if (!request) return;
  pending.delete(response.request_id);
  clearTimeout(request.timeout);
  if ("error" in response) request.reject(new Error(response.error.code));
  else request.resolve(response); // session_idも再同期の判断に使う
});
rpc.addEventListener("close", () => {
  for (const request of pending.values()) {
    clearTimeout(request.timeout);
    request.reject(new Error("RPC disconnected; operation outcome may be unknown"));
  }
  pending.clear();
});

function call(method, params) {
  return new Promise((resolve, reject) => {
    if (rpc.readyState !== WebSocket.OPEN) {
      reject(new Error("RPC is not connected"));
      return;
    }
    let id = nextId;
    while (pending.has(id)) id = (id + 1) >>> 0;
    nextId = (id + 1) >>> 0;
    const timeout = setTimeout(() => {
      pending.delete(id);
      reject(new Error("RPC timeout; operation outcome may be unknown"));
    }, 3000);
    pending.set(id, { resolve, reject, timeout });
    const request = { request_id: id, method };
    if (params !== undefined) request.params = params;
    try {
      rpc.send(JSON.stringify(request));
    } catch (error) {
      clearTimeout(timeout);
      pending.delete(id);
      reject(error);
    }
  });
}

const response = await call("get_timer_state");
console.log(response.session_id, response.result.phase, response.result.split_index);
```

タイムアウト後のIDは遅延Responseとの混同を避けるため、すぐに再利用しないでください。
IDの割り当てと応答照合は再接続のたびに接続単位で管理します。

Run名やSegmentを表示する場合は`await call("get_run")`、現在の比較対象や計測方式には
`await call("get_context_state")`を使います。Queryには`params`を付けません。
全Queryの用途は[Methods](protocol.md#4-methods)を参照してください。

## 3. タイマー操作

```javascript
await call("timer_operation", { operation: "start" });
await call("timer_operation", { operation: "split" });
await call("game_time_operation", { operation: "initialize" });
await call("game_time_operation", { operation: "set", ticks: 123450000 });
```

このゲーム内時間は12.345秒です。`set`以外のGame Time操作には`ticks`を付けません。
Controlの成功Responseは`result: null`です。表示する状態はQueryやEventで確認します。

タイマーの`pause`と`resume`は現実装では同じ切り替え処理です。未開始なら開始し、
計測中なら停止、一時停止中なら再開します。状態を固定する操作として繰り返し送信しないで
ください。[Timer Control](protocol.md#timer-control)に状態ごとの挙動を記載しています。

## 4. Eventsの購読

Eventsの`type`に応じて表示を更新するか、必要なQueryを再実行します。

| 受信したEvent | クライアントで行うことの例 |
| --- | --- |
| タイマー操作のEvent | `timer_state`を表示する。試行の詳細も必要なら`get_attempt`を取り直す |
| `timer_reset` | 必要なStateを再取得する。`timer_state`が`null`の場合にも対応する |
| `run_changed` | `get_run`を取り直し、画像を使っている場合はアイコンも再取得する |
| `context_changed` | `get_context_state`を取り直す |

Eventの全種類は[Eventsの仕様](protocol.md#5-events)を参照してください。
タイマーはEventの間にも進み、Game Time専用Eventもありません。時刻を継続表示する場合は、
用途に合わせて`get_timer_state`の定期取得などを組み合わせます。

## 5. 状態同期と再接続

Eventは欠落する可能性があり、履歴を再取得できません。実用クライアントでは
次の流れで、EventからQueryによる再取得につなげてください。

1. Eventsの受信処理を登録して接続し、その後RPCを接続します。初期同期が終わるまでは、
   Eventを状態へ直接適用せず、再取得が必要になったことを記録します。
2. 必要なStateをQueryします。各Responseと受信Eventの`session_id`を確認し、
   Sessionが一致しなければ、結果を混在させず両接続を作り直します。
3. 同期中にEventを受信した場合は、関連するQueryを再実行します。通常処理中も、
   前回sequenceの次が来ているか確認します。最大値の次は`1`です。
4. sequence不連続、Session変更、片方の接続の切断を検出したら、保存した状態を古いものと
   扱い、再接続後に必要なStateをQueryします。再接続は待機時間を増やしながら再試行します。
5. 後続Eventがなくても現在値を確認できるよう、必要なStateを定期的にQueryします。

最初に受信するsequenceは`1`とは限らないため、それだけを欠落と判断しません。
Sessionが変わったら前のsequenceとの比較をやめ、新しい系列として追跡します。

RPCとEventsには共通のsnapshot revisionや購読開始の基準sequenceがありません。
同期中にEventが来た場合の再取得は状態を追い直すための手順であり、全状態が同じ時点の
ものになる保証ではありません。Query開始前に発生したEventが遅れて届く場合もあるため、
Query後に届いたEventを常に新しい状態と見なさないでください。単一の受信ループで
再取得をまとめると、古い応答による表示の巻き戻りを扱いやすくなります。

Controlの応答を受信できなかった場合は、実行済みか不明です。タイムアウトや切断後に
同じControlを自動再送しないでください。まずQueryで状態を確認し、必要なら利用者の操作で
次の要求を出します。同じ`request_id`で送っても二重実行を防げません。

## 6. Schemaの利用

公式の[Request](../schemas/v4/rpc-request.schema.json)、
[Response](../schemas/v4/rpc-response.schema.json)、
[Event](../schemas/v4/bridge-event.schema.json)の各Schemaを、入力検証やモデル生成に使えます。
Draft 2020-12とローカルの`$defs`・`$ref`に対応したツールを使用してください。
Bridgeへの接続にSchema生成ツールの実行は不要です。

Schemaはメッセージの構造を検証します。Sessionやsequenceの追跡、欠落への対応、
Controlの再送判断はクライアントで実装します。受理条件の違いは
[SchemaとSerializerの差](protocol.md#9-json-schema)を参照してください。

## 7. 実装上の注意

上のコードはRPCの基本を示す例です。実用クライアントには受信データの検証、Sessionと
sequenceの追跡、再同期と再接続を組み込んでください。

- 64 KiBはRequestの上限です。アイコンを含むResponseに同じ上限を設定しないでください。
- ticksは100 ns単位で負数も使います。JavaScriptの`Number`では64 bit整数の全範囲を
  正確に扱えません。上の例は安全に表せる値だけを使っています。
- アイコンは`get_run_icons`で必要なときにまとめて取得します。`segment_icons`は画像が
  ない位置にも`null`を残します。`get_run`との間のRun切り替えに対応するrevisionはありません。
- アイコンだけの変更通知はありません。再取得時期はクライアントで決めてください。
- `permission_denied`を受信してもQueryを使えます。Originによる制限は認証ではありません。

通信を手早く確認する場合は[Debug CLI](../tools/livesplit-bridge-cli/README.md)も利用できます。
