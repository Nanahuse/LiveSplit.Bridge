# JSON v4 自動生成移行・テスト整理

2026-10-21。基準はPR #35適用後の `v4` (`2877e44`)。`codex/json-v4-generation` でDTO、Schema、Runtime、テストを一つのPRとして移行する。

## 採用した構成

C# DTOを正本とし、JsonSchema.Net.Generation 7.3.11 / DataAnnotations 3.1.1の標準APIで3 Schemaを生成する。製品は.NET Framework 4.8.1、System.Text.Json 9.0.4、単一Bridge DLLのまま。生成ライブラリは.NET 10開発ツール内だけにある。

調査 `investigation/schema-generation` / `0966623` の案Aを再利用した。調査ファイル一式と大量変異ハーネスはPRに含めない。既存Run payloadのPascalCaseプロパティ、`byte[]`画像、snapshotの所有契約を保持し、試作のwire/state二重モデルと変換コピーは導入しない。

| 要素 | 製品の構成 |
| --- | --- |
| Query | `QueryRequest` ＋ `QueryMethod` 5値 |
| Timer | `TimerRequest` ＋ `TimerParams` / `TimerOperation` 7値。adapterはenumを直接受け取る |
| Game Time | `GameTimeRequest<GameControlParams>`（initialize/pause/resume）と `GameTimeRequest<GameSetParams>`（set、非nullable ticks） |
| 成功 | `Success<T>`で5種類の既存payloadを直接保持 |
| Control成功 / Error | `ControlSuccessResponse` の固定nullと `ErrorResponse` を独立 |
| Event | `TimerEvent`（5種・非null）、`ResetEvent`（nullable）、`ChangedEvent`（2種・固定null） |
| 共通payload | 既存 `TimerState`, `TimeValue`, `RunState`, `SegmentInfo`, `RunMetadata`, `AttemptState`, `ContextState` 等 |

## 生成処理と実行コード

生成ツールは [`tools/schema/Program.cs`](../tools/schema/Program.cs) の31行。標準 `FromType` / `OneOf` / `Id` で型familyを合成する。生成後のノード走査・補正、独自generator/refiner、独自Schema属性、生成物の手修正を削除した。子Schemaの `$id` はローカル `$defs` の基点で、3ファイルは単独で利用できる。

固定版の.NET 10用APIが `JsonRequired`、C# nullable情報、`JsonStringEnumMemberName` を扱うため、調査試作の重複した `Required` / 大部分の `Nullable` 属性も不要になった。Schema専用属性は閉じたobject、数値範囲、固定値、画像説明とgeneric payloadの非null制約に限る。int64範囲には標準DataAnnotationsの文字列端点を使い、doubleへの丸めを避ける。

`SCHEMA_GENERATION` 時だけ画像型 `PngImage` を `string`、通常ビルドでは `byte[]` にする。通常のC#型エイリアスであり、画像のBase64キャッシュ、独自converter、追加のRunコピーはない。生成側の画像説明はBase64 PNGを示す。従来のcontent annotation同様、画像内容の検証を強制するものではない。

実際のC#出力は、別の [`ContractSamples`](../tools/schema/ContractSamples/LiveSplit.Bridge.ContractSamples.csproj) がnet481・STJ 9.0.4・通常DTOから出力する。生成用のstring aliasで試料を作らない。33試料には全Query/操作、全Response/Event、空/実Run、PNG、nullable値を含む。

受信済みJSONのmethod/operationから通常の型へdeserializeし、送信は標準STJのobject型による実行時型解決を使う。重複キー、識別不能request_id、64KiB要求制限、Session、Event Sequence、Origin、WebSocket接続処理を保持した。Run取得は既存の公開snapshotをそのまま `Success<RunState>` に入れる。Controlは既存の同じ `controlGate` で直列化する。

標準enum変換には、非Flags enumでも `"start, reset"` を別の有効な値へ合成する動作があった。これは曖昧な要求から操作が実行され得るため、RPC境界で単一のmethod/operationだけを許可する。既存の実通信テストへ2入力を追加した。enum名の大文字小文字を独自に検査する処理はない。未知の値、必須、null、型、余剰fieldは標準serializerとDTOへ任せ、旧Runtimeの既知パラメータ型判定と内部不正DTO専用の検証を削除した。

## Schemaの変化

2スペース・UTF-8・LF・末尾改行で比較。JSONの圧縮による削減ではない。生成設定と `.gitattributes` の双方でLFを固定し、Windowsチェックアウト後も再生成のバイト一致を確認する。

| Schema | 行数 前→後 | bytes 前→後 | root分岐 前→後 |
| --- | ---: | ---: | ---: |
| bridge-event.schema.json | 484 → 196 | 12,034 → 5,023 | 8 → 3 |
| rpc-request.schema.json | 235 → 152 | 5,191 → 3,660 | 7 → 4 |
| rpc-response.schema.json | 522 → 522 | 14,096 → 14,061 | 7 → 7 |

合計 1,241 → 870 行（29.9%減）、31,321 → 22,744 bytes（27.4%減）。Responseの展開量はほぼ同じだが、人が編集するC#の成功envelopeは1定義となる。

## テスト整理の取り込み

[`v4-test-cleanup.md`](v4-test-cleanup.md) の86テスト定義・26境界チェック・80 fixtureの棚卸しを取り込んだ。serializer自己検査、MSBuild XML構造検査、Pythonの手書きSchema検証、Abort回数/Task状態の検査を復活させていない。

DTO更新では既存テストの組立だけを調整し、応答や通知をwireの値で観測する。新DTOに不要な内部不正パラメータ例を削除した。追加は既存RPCテスト中の曖昧な複数操作/複数methodの拒否2入力だけで、新しいFact/Theoryは増やさない。Runの24変更ケース、公開済みsnapshotの保護、候補の新旧競合、Resetと候補完了の競合を維持した。

| 実行件数 | v4基準 | 最終 |
| --- | ---: | ---: |
| .NET | 96 | 87 |
| CLI単体 | 62 | 31 |
| CLI E2E | 12 | 12 |
| Schema内C#境界チェック | 26 | 0 |
| 固定Schema fixture | 80 | 21 |
| 実C#通信試料 | 33 | 33 |

CIはSchema契約とWindows実動作の2ジョブを維持する。Schema jobは.NET 10 SDKと固定lockで生成、再生成一致、net481試料とAjv適合を確認する。Windows jobは全solution build、実配布ZIP、.NET/CLI実通信を確認する。調査用の11,029件比較は移行時に一度だけ再利用し、製品CIへ追加しない。

## コード量

基準v4から、UTF-8・LFへ正規化して集計。コメント/空行を含む。文書、build設定、生成Schema、package lockはこのソース行数に含めない。DTO行はC#製品コードの内数。

| 対象 | 行数 前→後 | bytes 前→後 |
| --- | ---: | ---: |
| C# DTO（class数54→28） | 256 → 425 | 10,065 → 12,383 |
| C#製品コード全体（DTOを含む） | 2,184 → 2,363 | 89,203 → 92,521 |
| CLI製品コード | 754 → 533 | 27,842 → 19,483 |
| C#テストと補助コード | 2,169 → 1,980 | 97,148 → 90,597 |
| CLIテスト（E2E含む） | 856 → 685 | 27,541 → 21,966 |
| Schema生成・実試料出力・Ajvコード | 362 → 208 | 22,085 → 10,982 |
| Schema固定fixture | 82 → 23 | 16,178 → 3,524 |
| 配布スクリプト | 103 → 52 | 3,960 → 1,914 |

DTOの生ソースは、明示的なwire名とSchema専用属性で増えた。一方でclass数は半減し、独自生成補正・null converter・派生型登録・型ごとの成功envelope・Python検証の重複が減った。行数だけで改善とはせず、通常のRunを直接使えること、定義と制約を同じDTOで編集できること、実行依存が増えないことを評価する。

## 検証結果

| 検証 | 結果 |
| --- | --- |
| Release全solution build | 成功、error 0 |
| .NET | 87/87合格、skip 0 |
| CLI | 43/43合格（単体31＋E2E12） |
| 公式アセンブリによる既存CLI E2E | 最終DLLで12/12合格 |
| ruff check / format --check | 合格 |
| Schema generator build / locked restore | 成功 |
| 再生成一致 | 3ファイルすべてバイト一致 |
| Schema契約 | 固定21/21、C#実出力33/33、3 Schema compile成功 |
| 旧新のC#実出力 | 33件のJSON内容に差0（property順序を除く） |
| 移行時の旧新Schema比較 | 正確整数.NET / Ajvそれぞれ11,029件、判定差0 |
| 公式LiveSplit.exe実通信 | 34確認成功、実メッセージ34件すべてSchema適合 |
| 配布ZIP | 非空の `LiveSplit.Bridge.dll` 一つだけ、SHA256SUMS生成 |

公式ZIPはLiveSplit 1.8.37（SHA-256 `14bc8ef8ded9ef4033fb2f0cb6a152386d393127da18a4de14f096c5347aa991`）。隔離フォルダーで製品DLLをComponentsへ追加し、元77ファイルの変更/欠落0を確認した。共有System.Text.Json、Core、exe.configを入れ替えていない。製品DLLの参照アセンブリに生成ライブラリはなく、実行依存は従来どおり。

公式exeでは全Query、全11操作、7種の実Event、Session/順序、Run更新、Origin拒否、RPC再接続を確認した。ContextChangedの通知とContext変更抑止、コンポーネントApply/破棄/再試行は既存.NETテストで確認する。画面クリックは `GetCursorPos: access denied (0x80070005)` により実行できず、GUI操作の追加確認には限界がある。更新提案への同意や関連付け登録は行っていない。

公式exeの通常通信確認後、RPCの単一操作チェックと不要内部検証の整理を加えた。最終DLLは既存TestHostを未変更の公式アセンブリで起動して既存12件を再実行した。TestHostとその専用configは検証用の追加ファイルで、配布ZIPには入らない。元77ファイルはこの確認後も変更0。

GitHub Actionsの結果はPRのチェック欄に集約する。ローカルログ/TRX/JUnitと測定用一時コードはignored `.tmp/migration-results/` 等へ保存した。検証・測定の集計は [`json-v4-generation-results.json`](json-v4-generation-results.json) に保存する。

## 性能

同じマシン、net481、Release、STJ 9.0.4。処理をwarm-upしてから計測。Run送信は50segment・4comparison・PNG入り、wireサイズ27,377 bytesで、旧新とも同一のpayload。1,000回×5試行の中央値。実RPCは旧新TestHostを交互に起動し、各500往復×3試行の中央値。Runの内部モデル→wire変換コピーは加えていない。

| 経路 | 旧版 | 新版 |
| --- | ---: | ---: |
| Run ResponseのUTF-8出力 | 100.6 µs | 100.6 µs |
| 実WebSocket `get_timer_state` 往復 | 95.5 µs | 98.6 µs |
| 実WebSocket `get_run` 往復 | 262.6 µs | 278.9 µs |
| Bridge DLL | 69,632 bytes | 68,096 bytes |

Run送信のGen0 GC回数は1,000回ごとに旧新とも4回。実Run Queryの往復には十数µsの増分があるが、今回の測定で著しい悪化は見つからなかった。ネットワークやUI負荷、segment数で変わる小規模測定であり、性能上限を保証するものではない。

## 残る事項

- 有限比較はSchemaの完全な論理同値性の証明ではない。required/null、閉じたobject、排他的Response、単独のmethod/operation、3分類Eventと固定値を構造でも確認した。
- 整数1.0、JavaScriptのint64丸め、重複キー、配列/辞書内null、Base64内容、enum入力の表記についての標準serializerとの差は今回の仕様変更として扱わない。enumのcase検査を追加せず、wire出力名は固定した。
- PNG aliasは生成時/実行時でCLR型が異なる明示的な約束である。画像wire表現を変えるときはaliasと実出力契約を併せて変更する。
- 既存CS8632とxUnit1030のwarningは残る。
- マージ、Release、タグ発行はこの作業に含めない。

標準APIの参照： [JSON Schema生成](https://docs.json-everything.net/schema/schemagen/schema-generation/)、[使用した固定版ソース](https://github.com/json-everything/json-everything/tree/ff430467e33e54d56537954fae73dbdda2c95246/src/JsonSchema.Generation)。
