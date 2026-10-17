# LiveSplit.Bridge C# → JSON Schema PoC

`net481`の独立PoCで、C#通信DTOから`System.Text.Json` 9.0.4の`JsonSchemaExporter`を使いDraft 2020-12 Schemaを生成します。Bridge本体のProtobuf通信は変更せず、JSON方式の採用も決めません。

## 実行環境とコマンド

- .NET SDK 10.0.302以上、.NET Framework 4.8.1 Developer/Targeting Pack
- `System.Text.Json` 9.0.4（NuGet）
- Node.js 20以上、pnpm 12.9.1、Ajv 8.17.1

リポジトリルートから実行します。

```powershell
dotnet restore tools/schema-poc/LiveSplit.Bridge.SchemaPoc.csproj
dotnet build tools/schema-poc/LiveSplit.Bridge.SchemaPoc.csproj --configuration Release --no-restore
dotnet run --project tools/schema-poc/LiveSplit.Bridge.SchemaPoc.csproj --configuration Release --no-build -- tools/schema-poc/out
Push-Location tools/schema-poc
pnpm install --frozen-lockfile --ignore-scripts
pnpm run validate
Pop-Location
```

Schema生成先はGit無視対象の`tools/schema-poc/out/`です。独立検証はそこに生成した3ファイルを読みます。`schema-results/`にはレビュー用の生成物を固定保存します。`out/`、`bin/`、`obj/`、`node_modules/`はGit管理しません。

## 全通信モデルとSchema

| Schema | C#ルート型 | 収録内容 |
| --- | --- | --- |
| `rpc-request.schema.json` | `RpcRequest` | 5 Query、Timer操作、Game Time操作のmethod別anyOf |
| `rpc-response.schema.json` | `RpcResponse` | 5 Query結果、Controlのnull、BridgeErrorのanyOf |
| `bridge-event.schema.json` | `BridgeEvent` | 6 Timer Event、Run/Context変更Eventのtype別anyOf |

Requestは`JsonPolymorphic` / `JsonDerivedType`で`method`を判別子にし、Paramsを持たないQueryでは`params`を出しません。Timer操作Paramsは`operation`を判別子にします。Game Time操作は`set`だけ`GameTimeSetParams.ticks`を必須にし、他の操作型では未知プロパティ拒否によってticksを拒否します。この制約は派生Params型と標準ポリモーフィズムで表現でき、追加Converterは不要でした。`AllowOutOfOrderMetadataProperties = true`により、`request_id`が`method`より前のJSONも受け入れます。

Responseは型判別子なしの成功型6種とError型に分けます。各成功分岐は`result`、失敗分岐は`error`を必須とし、`additionalProperties: false`で両方の併存と両方の欠落を排除します。Controlの`result: null`とnullのみを持つEventフィールドは共通の`JsonNullValue` converterで出力・読込し、Schemaへ`const: null`を加えます。Responseを基底型にデシリアライズすることは要件にしていません。

Eventは`type`を判別子にします。`timer_started`、`timer_split`、`timer_skipped`、`timer_undo`、`timer_phase_changed`は必須の非null `TimerState`、`timer_reset`は必須の`TimerState|null`です。`run_changed`と`context_changed`は必須の`timer_state:null`のみを許します。型構成・共通null converter・生成Schema・Ajv fixturesで各制約を確認します。

## State、時間、画像の確認

- `TimerState.real_time_ticks`は現在タイマー値で、LiveSplit側に値がなくても現行Adapterは0を出します。`TimeValue.real_time_ticks` / `game_time_ticks`は未設定を`null`にし、TimerStateのgame timeもnullableです。
- Attempt / Runの配列と辞書は空の`[]` / `{}`を出します。`AttemptSegment.custom_variables`、`ContextState.custom_variables`、`RunMetadata.variables`は別の値です。LiveSplitAdapterではRunMetadata.variablesは変数名、ContextState.custom_variablesは現在値です。
- Runの`file_path`、`layout_path`、`metadata`、画像は不在時`null`です。`offset_ticks`は負数を許します。
- `byte[]?`はSystem.Text.JsonによりBase64文字列または`null`になります。Schemaには`contentEncoding: base64`と`contentMediaType: image/png`を付けました。これはannotationであり、Ajvの既定設定では不正Base64文字列もSchema適合と判定します。一方、System.Text.Jsonのbyte[] readerは不正Base64を拒否します。Bridgeの送信側はbyte[]から常にBase64を生成します。PNG内容検証用Converterは追加していません。
- Enumはsnake_case文字列で、未知の値と整数表現は拒否します。JSON向け`BridgeErrorCode`から`unspecified`と`unsupported_protocol_version`を削除しました（独立したprotocol versionはありません）。`TimerPhase`と`TimingMethod`は未知状態をAdapterが`unspecified`へ写像するため値を維持します。Operation discriminatorは実行可能な操作だけを列挙します。Protobuf Enumは変更していません。
- `[JsonRequired]`で省略と`null`を分け、`RespectNullableAnnotations = true`、`UnmappedMemberHandling = Disallow`を設定しています。

## Schema補完とSerializerの差

ExporterはC#整数型の上下限をSchemaに自動出力しないため、`TransformSchemaNode`で`uint`、`int`、`long`の境界を追加します。全IDを`uint`とし、共通補完で`0..4294967295`、Session IDとEvent sequenceだけは`minimum: 1`を重ねます。Controlのnull固定も局所的に`const: null`で補います。

- IDはJSON整数で、32bit符号なしの範囲内にあります。これらの値はJavaScript `Number`で正確に表現できます。
- System.Text.JsonはSession IDとEvent sequenceの`0`を`uint`として受け入れますが、Schemaは拒否します。Event sequenceの0禁止はEvent生成側で保証します。Request IDのSchemaとSerializerはいずれも0を受け入れます。
- `List<string>`の要素、`Dictionary<string,string>`の値はSchemaでは非nullですが、System.Text.Json 9.0.4はnull要素・値を受け入れます。型注釈だけでgeneric要素内部のnullは実行時強制できません。
- Reference DTOのroot `null`もSerializerは受け入れますが、`TreatNullObliviousAsNonNullable = true`のSchema rootは非nullです。
- 現行`BridgeRuntime.GenerateSessionId()`は非ゼロ64-bit `ulong`を生成します。JSONの確定仕様は非ゼロ32bitであり、本番生成処理の更新はPhase 3に残しています。
- Event sequenceは同一セッション内の発行順を示し、1から始まります。`uint.MaxValue`の次は1へ戻り、セッション変更時は新しい系列です。Event連番の生成・循環とクライアント側の連続性判定はPhase 3で実装します。Reset時に`timer_reset`の後に`run_changed`を送る場合があります。

## ID表現の評価

| ID | C# / JSON | 有効範囲 | 規則 |
| --- | --- | --- | --- |
| `request_id` | `uint` / integer | 0..4294967295 | クライアント採番。未完了Request間で一意。未完了Requestと衝突しなければ再利用可能。Responseは同じ値を返す。 |
| `session_id` | `uint` / integer | 1..4294967295 | BridgeRuntimeセッションID。セッション開始時に非ゼロ乱数から生成する。 |
| `event_sequence` | `uint` / integer | 1..4294967295 | セッション内Event順序。1から増加し、最大値の次は1。循環を考慮して連続性を判断する。 |

32bit値はJavaScript `Number`で正確に表現できます。Event sequenceはセッション切替で新しい系列になり、連番の循環時は`previous == 4294967295`なら期待値1、それ以外は`previous + 1`です。欠落を検出した場合、クライアントは必要に応じてQueryで状態を再同期します。

## 検証方法・結果

32bit ID更新後の結果: C#実行時チェック`32/32 PASS`、Ajv Draft 2020-12 meta-schema検査`3/3 PASS`、固定正常/異常fixture`80/80 PASS`、C#生成JSON`33/33 PASS`。Request/Responseのrequest_idは0、1、4294967295をシリアライズ・デシリアライズで確認し、Responseも同じIDを保持します。SchemaではIDごとの32bit範囲と0禁止を検証しました。Schema固定保存3点は再生成元とSHA-256が一致しました。`pnpm install --frozen-lockfile --ignore-scripts`も成功しました。`dotnet restore`とBuildは成功しましたが、NuGet vulnerability feedへ接続できず`NU1900`警告が出ています。

`pnpm run validate`は生成した全3 SchemaをAjv 8.17.1のDraft 2020-12 meta-schemaで検査した後、固定の正常/異常fixtureとC#で実際にシリアライズしたJSONを検証します。C#の実行時チェックの詳細は無視対象の`out/runtime-validation.txt`、Ajvは検証ごとの結果と合計件数を標準出力に出します。content annotationの検査は通常のSchema適合性とは分けています。

## 設計評価と残課題

方式A（C#のポリモーフィック階層から各ルートSchemaを生成）はRequestのmethod/params、Response排他、Event type/stateを表現できました。Game Time `set`条件も派生Params型だけで表現できています。独自Schema生成フレームワークやDTO外の大規模な通信定義は不要で、補完はnull固定・整数境界・ID上限に限られました。

ExporterはSchemaファイル間で共通型を共有`$defs`へ分けず、TimerState等が3文書内に重複します。各Schemaは単独で検証可能で、生成処理は単純です。Schema分割だけを目的とする独自参照生成は導入していません。

IDのJSON表現と値域は確定しました。Session IDの非ゼロ32bit生成、Event sequenceの32bit採番・wraparound、request_idの対応付けなど、本番通信境界の更新はPhase 3で対応します。Generic nullabilityと画像Base64内容保証は残課題です。JSON通信実装、CI/Release、Protobuf削除は対象外です。
