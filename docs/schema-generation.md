# Schemaの生成・検証

[src/LiveSplit.Bridge.Json](../src/LiveSplit.Bridge.Json/)のC# DTOとSerializer設定が通信モデルの
実装上の正本です。生成ツールは同じDTOからDraft 2020-12のJSON Schemaを作成します。
公開済みの[schemas/v4](../schemas/v4/)は生成結果として管理し、手作業で編集しないでください。

Schemaはメッセージの構造・型・値の制約を公開します。APIの意味、通信順序、配送上の保証は
[Protocol Reference](protocol.md)が定義します。

## 必要なツール

Bridge開発と共通のSDK・Framework要件は[開発環境](development.md#開発環境)を参照してください。
Schema生成にLiveSplitのサブモジュール取得は不要です。

| 用途 | 依存関係の管理先 |
| --- | --- |
| 生成器 | [LiveSplit.Bridge.Schema.csproj](../tools/schema/LiveSplit.Bridge.Schema.csproj)と`packages.lock.json`。対象は.NET 10 |
| 実際のRuntime DTOのシリアライズ | [ContractSamples](../tools/schema/ContractSamples/LiveSplit.Bridge.ContractSamples.csproj)。対象は.NET Framework 4.8.1 |
| Node.js | 20以降。[package.json](../tools/schema/package.json)の`engines`を参照 |
| pnpm | `package.json`の`packageManager`で固定したバージョンを使用 |
| Ajv | `package.json`と`pnpm-lock.yaml`で固定 |

## 生成と検証

リポジトリルートのPowerShellで実行します。以下は生成結果と公開Schemaが一致するかを
検査する手順です。

```powershell
dotnet restore tools/schema/LiveSplit.Bridge.Schema.csproj --locked-mode
dotnet build tools/schema/LiveSplit.Bridge.Schema.csproj --configuration Release --no-restore
dotnet run --project tools/schema/LiveSplit.Bridge.Schema.csproj --configuration Release --no-build -- tools/schema/out
./tools/schema/Sync-Schemas.ps1 -Check
dotnet run --project tools/schema/ContractSamples/LiveSplit.Bridge.ContractSamples.csproj --configuration Release -- tools/schema/out
Push-Location tools/schema
pnpm install --frozen-lockfile --ignore-scripts
pnpm run validate
Pop-Location
```

`Sync-Schemas.ps1 -Check`は生成ファイルと`schemas/v4/`をバイト単位で比較します。
DTOの変更を公開Schemaへ反映する場合は、生成後に次を実行してから検証をやり直します。

```powershell
./tools/schema/Sync-Schemas.ps1
git diff -- schemas/v4
```

`tools/schema/out/`、ビルド結果、`node_modules`はGitの管理対象外です。
意図したDTO変更と生成差分を一緒にレビューしてください。Schema contract CIは上と同じ
生成・比較・検証を行います。BridgeとCLIの検査は[開発手順](development.md)を参照してください。

## 生成ファイル

| Schema | 入力となるルートDTO |
| --- | --- |
| [rpc-request.schema.json](../schemas/v4/rpc-request.schema.json) | `QueryRequest`、`TimerRequest`、`GameTimeRequest<T>` |
| [rpc-response.schema.json](../schemas/v4/rpc-response.schema.json) | `Success<T>`、`ControlSuccessResponse`、`ErrorResponse` |
| [bridge-event.schema.json](../schemas/v4/bridge-event.schema.json) | `TimerEvent`、`ResetEvent`、`ChangedEvent` |

## 生成器の保守

[Program.cs](../tools/schema/Program.cs)が`JsonSchema.Net.Generation`で3つのSchemaを
構成します。ルートの`oneOf`は`$ref`だけを持ち、各DTOとコレクションをルートの`$defs`に
配置します。ネストした`$id`スコープは作らず、参照を各ファイル内で解決できる構成です。

定義名はCLR型から作ります。通常のDTOはクラス名、`Success<T>`はpayload名に`Response`を
付けた名前です。Game Timeの2種類は`GameControlRequest`と`GameSetRequest`、Eventの
Enumは`TimerEventType`と`ChangedEventType`です。コレクションは`ListOf...`と
`DictionaryOf...`、ルートのtitleは`RpcRequest`、`RpcResponse`、`BridgeEvent`です。
コード生成で使う名前を変更する場合は、公開Schemaの差分も確認してください。

DTOの`JsonRequired`、null許容注釈、`JsonStringEnumMemberName`、snake_case命名を
生成に使います。数値制約のDataAnnotations `Range`はRuntimeと生成器の両方に含まれます。
ただし、Serializerが`Range`を実行時検証するわけではありません。

`SCHEMA_GENERATION`は生成器固有の属性を有効にし、`PngImage`を`string`へ切り替えます。
Runtimeの画像型は`byte[]`で、System.Text.JsonのBase64変換を使います。
生成器の依存パッケージはBridge DLLに組み込みません。ContractSamplesはRuntimeと同じ
DTO・SerializerソースをFramework向けにコンパイルして実際の出力を作ります。

### null許容の補正

現在固定している生成器には、次の補正が必要です。依存パッケージを更新した場合は、
補正を外す前に生成差分とnull許容の検証ケースを確認してください。

- null許容の参照DTOを生成器がインライン化するため、refinerで`anyOf[$ref, null]`に
  置き換えます。名前付きDTOへの参照とnull許容を両立させるための処理です。
- `RunIcons.segment_icons`では、要素への`Nullable`属性が配列自体にも作用します。
  DTOの`If`と条件付き`Nullable(false)`で、`null`の配列を拒否しながら`null`要素を許容します。
  この条件を型へ変換できないクライアント生成ツールでは、配列自体もnull許容になる場合が
  あります。その場合も公開Schemaの制約を検証してください。

## 検証範囲

`pnpm run validate`は3つのSchemaをAjvのDraft 2020-12実装でコンパイルし、
[固定ケース](../tools/schema/samples/validation-cases.json)と
[実際のC#シリアライズ結果](../tools/schema/ContractSamples/ContractSamples.cs)を検証します。

固定ケースはprotocol固有の許容・拒否条件を扱います。C#サンプルはすべてのQuery・操作、
Response・Event、空またはデータのあるRun、別取得する画像、null許容項目を扱います。
生成コマンドはSchemaだけを書き出し、C#のメッセージはContractSamplesが別に出力します。
実際のRPC振り分けやエラー応答はWindowsの通信テストで確認します。

## SchemaとSerializerの差

公開する値の判断は[Protocol Reference](protocol.md#9-json-schema)を参照してください。
現在の差は生成物との整合性検証とは分けて扱います。

| 差 | 公開仕様と現在の実装 |
| --- | --- |
| 非ゼロID | `uint`のSerializerは`0`を受理するが、SchemaはSessionとEvent sequenceの`0`を拒否する。Bridgeは非ゼロを生成する |
| コレクション内部のnull | `List<string>`の要素と`Dictionary<string, string>`の値は、Serializerがnullを受理する場合がある。Schemaは拒否し、Bridgeの構築処理もnullを出力しない |
| ルートnull | 参照DTOを直接Deserializeするとnullを受理できる。Schemaはオブジェクトを要求し、RPC Handlerもオブジェクト以外を応答なしで破棄する |
| 画像内容 | Schemaの説明はBase64 PNGだが内容を検証しない。Runtimeの`byte[]`読み込みは不正なBase64を拒否し、PNGの内容までは検証しない |
| Enum文字列 | Serializerが受理する大文字小文字の違いなどをSchemaは許容しない。公開値はSchemaの文字列とし、RPC Handlerはカンマで連結された操作も明示的に拒否する |

DTOの単体Deserializeと、RPC Handlerを通した入力検査は同じではありません。
受理条件を変更する場合はDTO・Handler・Schema・契約ケース・通信テストのうち影響する
箇所を揃え、Protocol Referenceも更新してください。
