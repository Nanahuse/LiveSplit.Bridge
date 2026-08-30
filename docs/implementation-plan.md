# LiveSplit.Bridge 実装方針

## 1. 目的

`LiveSplit.Bridge` は、LiveSplit の状態取得・操作・イベント通知を外部プロセスへ公開するための汎用コンポーネントである。

初期版では外部 Auto Splitter に必要な機能だけを実装する。ただし、通信方式・プロトコル・内部構造は、将来的に LiveSplit の外部公開可能な機能全体を扱えるように設計する。

### 初期版の対象

- タイマー状態の取得
- Start / Split / Skip / Undo / Reset
- Pause / Resume
- Game Time の初期化・設定・停止・再開
- LiveSplit 側で発生した操作イベントの通知
- 各イベント発生後の状態スナップショット
- 初期接続時および明示要求時のスナップショット取得

### 将来の対象

- Run / Segment
- Comparison
- Attempt History / Segment History
- Custom Variables / Metadata
- Split ファイル操作
- Layout
- Hotkey
- LiveSplit が公開するその他の操作・状態

---

## 2. 基本方針

### 2.1 Auto Splitter 専用APIにはしない

外部APIは、ゲーム固有の Auto Splitter ロジックを含めない。

```text
Game-specific Auto Splitter
          │
          │ LiveSplit Bridge Protocol
          ▼
LiveSplit.Bridge Component
          │
          ▼
LiveSplitState / TimerModel
```

コンポーネントは LiveSplit と外部アプリケーションの間の汎用ブリッジに徹する。

### 2.2 LiveSplit の内部オブジェクト構造を直接公開しない

次のような内部構造を、そのまま外部APIへ反映しない。

```text
state.Run[index].SplitTime.RealTime
state.Settings...
TimerModel...
```

代わりに、外部向けに安定した意味単位のAPIを定義する。

```text
timer.get_snapshot
timer.start
timer.split
timer.skip
timer.undo
timer.reset
game_time.set
segments.list
```

LiveSplit 内部APIの変更は、`LiveSplitAdapter` 層で吸収する。

### 2.3 LiveSplit をタイマー状態の正とする

タイマー状態、現在の Split Index、現在の Attempt、Game Time 状態については LiveSplit を唯一の正とする。

Auto Splitter は独自に Split Index を進めず、受信したイベントとスナップショットに追従する。

---

## 3. 全体構成

```text
┌──────────────────────────┐
│ External Auto Splitter   │
│                          │
│  SUB: event receiver     │
│  REQ: RPC client         │
└─────────────┬────────────┘
              │
              │ ZeroMQ ipc://
              │ Protobuf
              │
┌─────────────▼────────────┐
│ LiveSplit.Bridge         │
│                          │
│  PUB: event publisher    │
│  REP: RPC server         │
│                          │
│  EventObserver           │
│  SnapshotBuilder         │
│  RequestDispatcher       │
│  LiveSplitAdapter        │
└─────────────┬────────────┘
              │
              ▼
     LiveSplitState / TimerModel
```

通信経路は役割ごとに分ける。

| 方向 | ZeroMQ | 用途 |
|---|---|---|
| LiveSplit → 外部アプリ | PUB/SUB | イベントとスナップショットの配信 |
| 外部アプリ → LiveSplit | REQ/REP | 操作要求、状態照会、初期同期 |

---

## 4. 複数LiveSplitへの対応

複数の LiveSplit を同時起動する場合、各 LiveSplit と Auto Splitter の組は別々の IPC エンドポイントを使用する。

複数インスタンスを同じソケットで集約・識別する機能は実装しない。

```text
LiveSplit A PUB/REP ↔ Auto Splitter A SUB/REQ
LiveSplit B PUB/REP ↔ Auto Splitter B SUB/REQ
```

例:

```text
ipc://C:/Users/<user>/AppData/Local/LiveSplit.Bridge/a.rpc
ipc://C:/Users/<user>/AppData/Local/LiveSplit.Bridge/a.events

ipc://C:/Users/<user>/AppData/Local/LiveSplit.Bridge/b.rpc
ipc://C:/Users/<user>/AppData/Local/LiveSplit.Bridge/b.events
```

エンドポイントはコンポーネント設定としてレイアウトへ保存する。

---

## 5. Transport

### 5.1 ZeroMQ

native `libzmq` を使用する。

初期版のソケット構成:

```text
LiveSplit.Bridge:
  REP bind <rpc endpoint>
  PUB bind <event endpoint>

External client:
  REQ connect <rpc endpoint>
  SUB connect <event endpoint>
```

`bind` と `connect` の担当は将来変更可能だが、初期版では LiveSplit コンポーネントをサーバー側とする。

### 5.2 IPC

同一PC内でのみ使用するため、`ipc://` を使用する。

ZeroMQ の `ipc://` は Windows の Win32 Named Pipe ではなく、libzmq が提供する IPC transport である。外部APIの仕様では transport を抽象化し、IPCパスの生成・設定は `Transport` 層へ閉じ込める。

### 5.3 ZeroMQソケットの所有スレッド

ZeroMQソケットは、それぞれ専用の通信スレッドだけから操作する。

```text
LiveSplit thread
  └─ event/data queue
          │
          ▼
ZeroMQ transport thread
```

LiveSplit のイベントハンドラーから PUB ソケットを直接操作しない。

---

## 6. シリアライズ

通信データには Protobuf を使用する。

採用理由:

- 多言語クライアントのコード生成
- 型付きの通信契約
- API拡張時の互換性管理
- `oneof` による要求・応答・イベント型の明確化
- JSONよりもフィールド型と存在有無を厳密に管理できる

性能や通信量は主目的ではない。

### 6.1 基本ルール

- 公開済みのフィールド番号を変更しない
- 削除した番号と名前は `reserved` にする
- 同じフィールド番号を別の意味で再利用しない
- enum の `0` は `UNSPECIFIED`
- 値が存在しない可能性があるスカラーは `optional`
- LiveSplit の時間値は `sint64 ticks` で表す
- `1 tick = 100 ns`

---

## 7. Protobufの構成

初期構成:

```text
proto/
└─ livesplit/
   └─ bridge/
      └─ v1/
         ├─ common.proto
         ├─ snapshot.proto
         ├─ timer.proto
         ├─ game_time.proto
         ├─ event.proto
         └─ rpc.proto
```

package:

```proto
syntax = "proto3";

package livesplit.bridge.v1;

option csharp_namespace = "LiveSplit.Bridge.Protocol.V1";
```

---

## 8. スナップショット

スナップショットは、その時点での LiveSplit の同期に必要な状態を表す。

初期版の例:

```proto
message TimerSnapshot {
  uint64 state_revision = 1;

  TimerPhase phase = 2;
  int32 split_index = 3;
  int32 split_count = 4;

  optional sint64 real_time_ticks = 5;
  optional sint64 game_time_ticks = 6;

  bool game_time_initialized = 7;
  bool game_time_paused = 8;

  TimingMethod timing_method = 9;

  uint64 attempt_revision = 10;
  uint64 run_revision = 11;
}
```

### 8.1 `state_revision`

意味のある状態変更時に増加する。

例:

- Start
- Split
- Skip
- Undo
- Reset
- Pause / Resume
- Game Time状態変更
- Run構成変更

定期スナップショットを送信するだけでは増加させない。

### 8.2 `attempt_revision`

新しい Attempt へ移行したことを識別する。

Reset直後に再Startされ、外部側が途中の `NotRunning` を受信できなかった場合でも、新しい Attempt であることを検出できるようにする。

### 8.3 `run_revision`

Segment構成、ゲーム名、カテゴリなど、Runに関係するデータが変化した場合に増加する。

外部側は `run_revision` が変化した場合だけ、RunやSegmentの詳細を再取得する。

---

## 9. イベント配信

イベントはイベントとして配信する。状態変更イベントと定期スナップショットには、
イベント処理後のスナップショットを含める。生存確認用のハートビートは例外で、
スナップショットを含めない。

```proto
message BridgeEvent {
  uint32 protocol_version = 1;
  bytes session_id = 2;
  uint64 event_sequence = 3;

  EventOrigin origin = 4;
  optional uint64 request_id = 5;

  TimerSnapshot snapshot = 6;

  oneof detail {
    TimerStartedEvent timer_started = 20;
    TimerSplitEvent timer_split = 21;
    TimerSkippedEvent timer_skipped = 22;
    TimerUndoSplitEvent timer_undo_split = 23;
    TimerResetEvent timer_reset = 24;
    TimerPausedEvent timer_paused = 25;
    TimerResumedEvent timer_resumed = 26;
    StateSnapshotEvent state_snapshot = 27;
  }
}
```

### 9.1 初期イベント

```text
timer.started
timer.split
timer.skipped
timer.undo_split
timer.reset
timer.paused
timer.resumed
state.snapshot
```

Game Time固有イベントが必要になった段階で追加する。

ハートビートは1秒周期で既存のPUBストリームへ配信する。ハートビート自身は
`event_sequence`を増加させず、最後に送信成功または失敗が確定したsequence対象イベントの
番号を通知する。状態変更イベント、定期スナップショット、ハートビートの送信は、単一の
publisherループで全順序を付ける。

### 9.2 イベント発生後の状態

イベントに含めるスナップショットは、操作後の状態でなければならない。

例:

- Split: Split Indexが進んだ後
- Skip: Split Indexが進んだ後
- Undo: Split Indexが戻った後
- Reset: PhaseがNotRunning、Split Indexが-1になった後

### 9.3 イベントの送信元

`origin` により操作経路を示す。

```proto
enum EventOrigin {
  EVENT_ORIGIN_UNSPECIFIED = 0;
  EVENT_ORIGIN_USER = 1;
  EVENT_ORIGIN_HOTKEY = 2;
  EVENT_ORIGIN_RPC = 3;
  EVENT_ORIGIN_COMPONENT = 4;
  EVENT_ORIGIN_PERIODIC = 5;
}
```

LiveSplit側で操作経路を厳密に区別できない場合は、無理に推定しない。初期版では `RPC` と `UNSPECIFIED` の区別だけでもよい。

RPC操作により発生したイベントには、対応する `request_id` を設定する。

---

## 10. 定期スナップショット

状態変更イベントとは別に、定期的に `state.snapshot` イベントを送信する。

目的:

- SUB接続開始前に失われたイベントからの回復
- 一時切断後の現在状態への回復
- PUB/SUBの取りこぼし後の収束
- 外部クライアントの同期確認

配信周期:

```text
30秒
```

接続の生存確認とイベント欠落検出は1秒周期のハートビートが担当する。定期スナップショットは、
Bridgeが監視していない状態変更やイベントフック漏れから収束するための安全網とする。

---

## 11. イベント履歴

初期版ではイベント履歴を実装しない。

理由:

- 各イベントに操作後の完全スナップショットを含める
- 定期スナップショットを配信する
- Auto Splitterが必要とする中心情報は現在状態である
- 実装を小さく保つ

イベントを取りこぼした場合、イベントの種類そのものは復元できないが、次のスナップショットで現在状態へ復旧できる。

将来、次の用途が必要になった場合にイベント履歴を追加する。

- 正確な操作ログ
- イベント単位の再送
- リプレイ
- Skipなど特定イベントの確実な処理
- 外部監査

---

## 12. PUB/SUBの購読開始

SUBの接続と購読伝達は非同期であるため、購読成立前のイベントは失われる可能性がある。

初期同期はPUB/SUBへ依存しない。

外部クライアントの接続手順:

```text
1. SUBソケットを作成
2. SUBSCRIBEを設定
3. event endpointへconnect
4. REQソケットを作成
5. rpc endpointへconnect
6. system.get_snapshotをREQ
7. REPで初期スナップショットを受信
8. 以後SUBイベントを処理
```

初期スナップショット応答には、その時点の `session_id`、`event_sequence`、`state_revision` を含める。外部側は、それ以前のイベントを古いものとして無視できる。

---

## 13. RPC

REQ/REPは次の用途に使用する。

- タイマー操作
- Game Time操作
- 初期スナップショット取得
- 明示的な再同期
- Run / Segmentなどの照会
- 将来の全API

### 13.1 Request

```proto
message Request {
  uint32 protocol_version = 1;
  uint64 request_id = 2;

  oneof operation {
    GetSnapshotRequest get_snapshot = 10;

    StartTimerRequest start_timer = 20;
    SplitRequest split = 21;
    SkipSplitRequest skip_split = 22;
    UndoSplitRequest undo_split = 23;
    ResetTimerRequest reset_timer = 24;
    PauseTimerRequest pause_timer = 25;
    ResumeTimerRequest resume_timer = 26;

    InitializeGameTimeRequest initialize_game_time = 30;
    SetGameTimeRequest set_game_time = 31;
    PauseGameTimeRequest pause_game_time = 32;
    ResumeGameTimeRequest resume_game_time = 33;
  }
}
```

### 13.2 Response

```proto
message Response {
  uint32 protocol_version = 1;
  uint64 request_id = 2;

  oneof outcome {
    Error error = 10;
    GetSnapshotResponse get_snapshot = 20;
    TimerOperationResponse timer_operation = 21;
    GameTimeOperationResponse game_time_operation = 22;
  }
}
```

### 13.3 操作結果

成功応答では単なる `ok` だけでなく、操作が実際に適用されたかを返す。

```proto
message TimerOperationResponse {
  bool applied = 1;
  OperationRejectReason reject_reason = 2;
  TimerSnapshot snapshot = 3;
}
```

例:

- RunningでないためSplitできない
- 最終SegmentなのでSkipできない
- Undo対象が存在しない

---

## 14. 初期API

### System

```text
system.hello
system.get_capabilities
system.get_snapshot
```

### Timer

```text
timer.start
timer.split
timer.skip
timer.undo
timer.reset
timer.pause
timer.resume
```

### Game Time

```text
game_time.initialize
game_time.set
game_time.pause
game_time.resume
```

### Run / Segmentの最小読み取り

Auto Splitterで必要になった段階で追加する。

```text
run.get_summary
segments.list
```

---

## 15. Capability

クライアントが実装済み機能を判定できるようにする。

```proto
message GetCapabilitiesResponse {
  uint32 protocol_version = 1;

  map<string, uint32> modules = 2;
}
```

例:

```text
timer: 1
game_time: 1
run: 0
segments: 0
comparisons: 0
layout: 0
```

コンポーネントの製品バージョンではなく、機能単位で利用可能性を判定する。

---

## 16. コンポーネント内部構造

```text
src/LiveSplit.Bridge/
├─ Component.cs
├─ Factory.cs
├─ ComponentSettings.cs
│
├─ Api/
│  ├─ RequestDispatcher.cs
│  ├─ TimerApi.cs
│  ├─ GameTimeApi.cs
│  └─ RpcError.cs
│
├─ LiveSplit/
│  ├─ LiveSplitAdapter.cs
│  ├─ SnapshotBuilder.cs
│  └─ EventObserver.cs
│
├─ Transport/
│  ├─ BridgeServer.cs
│  ├─ RpcServer.cs
│  ├─ EventPublisher.cs
│  ├─ ZmqContext.cs
│  └─ EndpointConfiguration.cs
│
└─ Threading/
   └─ LiveSplitDispatcher.cs
```

### 16.1 `Component`

責務:

- LiveSplitコンポーネントのライフサイクル
- 各サービスの生成と破棄
- 設定の読み書き
- イベント購読の開始と解除

通信やLiveSplit操作の詳細を直接実装しない。

### 16.2 `LiveSplitAdapter`

責務:

- LiveSplitState / TimerModelの操作
- LiveSplit内部型とBridge内部型の変換
- 操作可能条件の判定
- LiveSplit内部API変更の吸収

### 16.3 `SnapshotBuilder`

責務:

- LiveSplitの現在状態を一貫したスナップショットへ変換
- 時間値・enum・null値の変換
- `state_revision` 等の管理

### 16.4 `EventObserver`

責務:

- LiveSplitイベントの購読
- イベント種別と操作後状態の取得
- Transport非依存のイベントデータ生成
- イベントキューへの追加

ZeroMQソケットを直接操作しない。

### 16.5 `RpcServer`

責務:

- REP受信
- Protobufデシリアライズ
- RequestDispatcherの呼び出し
- Protobufシリアライズ
- REP送信

### 16.6 `EventPublisher`

責務:

- キューからイベントを取得
- Protobufへ変換
- PUB送信
- 1秒周期のハートビート送信
- 定期スナップショットの送信

---

## 17. スレッドモデル

LiveSplitの状態へのアクセスは、LiveSplitのUIスレッド上で行う。

```text
ZeroMQ thread
    │
    ▼
LiveSplitDispatcher
    │
    ▼
LiveSplit UI thread
    │
    ▼
LiveSplitAdapter
```

イベント発生時:

```text
LiveSplit UI thread
    │
    ├─ EventObserver
    │
    ├─ SnapshotBuilder
    │
    └─ thread-safe queue
               │
               ▼
        PUB transport thread
```

イベントハンドラー内では、ブロッキングI/Oや重いシリアライズを行わない。

---

## 18. 設定

初期設定項目:

```text
RPC endpoint
Event endpoint
Enable server
Periodic snapshot interval
```

将来追加候補:

```text
Endpoint template
Instance name
Logging level
Allowed API modules
```

設定はLiveSplitのレイアウト設定に保存する。

複数LiveSplitで同じレイアウトを共有する場合、IPC endpointが衝突しないようにユーザーが変更できる必要がある。

---

## 19. リポジトリ構成

```text
LiveSplit.Bridge/
├─ .vscode/
│  ├─ extensions.json
│  ├─ settings.json
│  ├─ tasks.json
│  └─ launch.json
│
├─ docs/
│  └─ implementation-plan.md
│
├─ external/
│  └─ LiveSplit/                  # Git submodule
│
├─ proto/
│  └─ livesplit/bridge/v1/
│
├─ src/
│  ├─ LiveSplit.Bridge/
│  └─ LiveSplit.Bridge.Protocol/
│
├─ tests/
│  └─ LiveSplit.Bridge.Tests/
│
├─ Directory.Build.props
├─ LiveSplit.Bridge.slnx
├─ README.md
└─ LICENSE
```

---

## 20. ビルドと配置

LiveSplitはsubmoduleとして保持し、`ProjectReference`で参照する。

Bridgeの通常出力:

```text
src/LiveSplit.Bridge/bin/Debug/net4.8.1/
```

ビルド後に、必要なファイルだけLiveSplitのComponentsへコピーする。

```text
external/LiveSplit/bin/debug/Components/
├─ LiveSplit.Bridge.dll
├─ LiveSplit.Bridge.pdb
├─ LiveSplit.Bridge.Protocol.dll
└─ LiveSplit.Bridge.Protocol.pdb
```

`OutputPath`自体をComponentsへ変更しない。LiveSplitの依存DLLを大量にComponentsへコピーしないため、Componentsはデプロイ先として扱う。

---

## 21. デバッグ

現在確認済み:

- LiveSplit本体のビルド
- Bridgeのビルド
- Componentsへの自動配置
- LiveSplitからFactoryの検出
- レイアウトへのBridge追加
- LiveSplitイベントの受信
- `Debug.WriteLine`によるイベント確認

日常的な確認:

- DebugViewで `Debug.WriteLine` を確認
- 必要に応じてログファイルへ出力し、VS Code統合ターミナルから監視
- 通信・変換ロジックはxUnitで単体テスト

---

## 22. テスト方針

### Unit Test

LiveSplit内部APIから分離できる処理を重点的にテストする。

- Snapshot変換
- Protobuf serialize / deserialize
- RequestDispatcher
- RPCのエラー変換
- Capability
- revision管理
- Endpoint validation

### Integration Test

- REQ → REP
- PUB → SUB
- 初期スナップショット取得
- 操作後イベントの配信
- 定期スナップショット
- SUB再接続後の同期
- 複数の独立IPC経路

### LiveSplit上での確認

- Start
- Split
- Skip
- Undo
- Reset
- Pause / Resume
- Game Time
- レイアウトから削除した際のDispose
- LiveSplit終了時の通信スレッド停止

---

## 23. 実装順序

### Phase 1: Protocolの基礎

- `common.proto`
- `snapshot.proto`
- `event.proto`
- `rpc.proto`
- C#コード生成
- serialize / deserializeテスト

### Phase 2: Snapshot

- `LiveSplitAdapter`
- `SnapshotBuilder`
- `system.get_snapshot`
- REQ/REPの最小実装

### Phase 3: Event PUB/SUB

- `EventObserver`
- `EventPublisher`
- Start / Split / Skip / Undo / Reset
- 各イベントへのスナップショット添付
- `state.snapshot`の定期配信

### Phase 4: Timer RPC

- Start
- Split
- Skip
- Undo
- Reset
- Pause / Resume
- 操作結果とスナップショット応答
- RPC由来イベントへの `request_id` 付与

### Phase 5: Game Time

- Initialize
- Set
- Pause
- Resume
- Snapshot項目の完成

### Phase 6: Settings

- RPC endpoint
- Event endpoint
- 配信周期
- 有効・無効
- レイアウト設定への保存

### Phase 7: Auto Splitterクライアント

- SUBイベント受信
- REQ操作
- 初期同期
- スナップショット追従
- Reset / Skip / Undoへの追従

### Phase 8以降: 汎用APIの拡張

- Run
- Segment
- Comparison
- Attempt / History
- Metadata
- Files
- Layout
- Hotkeys
- その他LiveSplit機能

---

## 24. 初期版で実装しないもの

- 複数LiveSplitの単一ソケットへの集約
- ROUTER / DEALER
- 外部Broker
- イベント履歴と再送
- HTTP / WebSocket
- JSON通信
- ゲーム固有のAuto Splitterロジック
- LiveSplit内部オブジェクトの直接公開
- リモートPCからの接続
- 認証・暗号化

これらは実際の要求が発生した場合に追加する。

---

## 25. 現在の到達点

2026-07-31時点で、次の開発基盤が動作している。

- `LiveSplit.Bridge.slnx`
- `LiveSplit.Bridge` (`net4.8.1`)
- `LiveSplit.Bridge.Protocol` (`netstandard2.0`)
- `LiveSplit.Bridge.Tests`
- LiveSplitのGit submodule
- LiveSplit.Core / UpdateManagerへのProjectReference
- LiveSplit本体と標準コンポーネントのビルド
- Bridge DLLのComponentsへの自動配置
- LiveSplit上でのコンポーネント追加
- Start / Split / Skip / Undo / Reset等のイベント確認

次に着手する項目は、**Protobuf定義とSnapshotBuilderの実装**とする。
