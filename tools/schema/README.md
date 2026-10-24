# JSON v4 Schema contract

`src/LiveSplit.Bridge.Json/` is the single source of truth for the JSON v4 DTOs and serializer options. This tool generates Draft 2020-12 schemas from those DTOs with `JsonSchema.Net.Generation` 7.3.11 on .NET 10. The checked-in schemas are reviewable generated output; do not edit them by hand.

## Requirements and commands

- .NET SDK 10.0.302 or later and the .NET Framework 4.8.1 Developer Pack
- Node.js 20 or later
- pnpm 12.9.1, pinned in `package.json`
- Ajv 8.17.1, pinned in `pnpm-lock.yaml`

Run from the repository root in PowerShell:

```powershell
dotnet restore tools/schema/LiveSplit.Bridge.Schema.csproj --locked-mode
dotnet build tools/schema/LiveSplit.Bridge.Schema.csproj --configuration Release --no-restore
dotnet run --project tools/schema/LiveSplit.Bridge.Schema.csproj --configuration Release --no-build -- tools/schema/out
./tools/schema/Sync-Schemas.ps1
dotnet run --project tools/schema/ContractSamples/LiveSplit.Bridge.ContractSamples.csproj --configuration Release -- tools/schema/out
Push-Location tools/schema
pnpm install --frozen-lockfile --ignore-scripts
pnpm run validate
Pop-Location
```

`Sync-Schemas.ps1` copies the generated files to `schemas/v4/`. Pass `-Check` to compare generated and checked-in schema files byte-for-byte instead. Generated files, build output, and `node_modules` are ignored by Git.

The Schema Contract CI job uses pnpm 12.9.1, runs the frozen install, generates the schemas, checks byte equality, and validates the contracts. The Windows Tests job independently builds and tests the Bridge, CLI, and TestHost; it also builds and inspects the same distribution ZIP assembly script used by Release. Both jobs must pass for CI to pass.

## Generated schemas

| Schema | Root DTO | Contract |
| --- | --- | --- |
| [`rpc-request.schema.json`](../../schemas/v4/rpc-request.schema.json) | `QueryRequest`, `TimerRequest`, `GameTimeRequest<T>` | Six query methods and timer/game-time operations, including method and operation parameter rules |
| [`rpc-response.schema.json`](../../schemas/v4/rpc-response.schema.json) | `Success<T>`, `ControlSuccessResponse`, `ErrorResponse` | Query results including separate `RunState` and `RunIcons`, `result: null` control responses, and exclusive error responses |
| [`bridge-event.schema.json`](../../schemas/v4/bridge-event.schema.json) | `TimerEvent`, `ResetEvent`, `ChangedEvent` | All eight events and their `timer_state` rules |

## Definition layout

Each schema is a standalone Draft 2020-12 document. The root `oneOf` contains only `$ref` entries, DTOs are emitted under the root `$defs`, and nested DTOs use local `#/$defs/...` references. Generation 7.3.11 inlines a nullable reference property instead of attaching `null` to a `$ref`; a small refiner rewrites those properties as `anyOf[$ref, null]` so clients keep both the named DTO and its nullability. The generated files have no nested `$id` scopes, so consumers can resolve and validate each file from its root without rebuilding the reference graph.

Definition names come from the CLR types. Non-generic DTOs keep their class name. Closed generic responses use the payload name plus `Response`, so `Success<TimerState>` becomes `TimerStateResponse`, `Success<RunState>` becomes `RunStateResponse`, and so on. The two `GameTimeRequest<T>` specializations become `GameControlRequest` and `GameSetRequest`, and the event kind enums are exposed as `TimerEventType` and `ChangedEventType`. Collection shapes use `ListOf...` and `DictionaryOf...` names so all references remain local. Enum definitions include `type: "string"`, which lets datamodel-code-generator emit string enums such as `StrEnum` without extra configuration. The root union carries a title (`RpcRequest`, `RpcResponse`, or `BridgeEvent`) so generators that use titles can name the family wrapper.

The DTOs use `JsonRequired`, nullable annotations, `JsonStringEnumMemberName`, and standard snake_case naming. Standard DataAnnotations `Range` attributes express numeric constraints and are included in both runtime and schema builds. Generator-specific `Const` and `AdditionalProperties` attributes express other constraints not implied by CLR types. `SchemaGenerationContextCache.Get`, `Apply`, `OneOf`, `Defs`, `Ref`, and `Title` compose the three documents; an `ITypeNameGenerator` supplies the `$defs` keys. The only custom refiner is the nullable-DTO rewrite described above; it uses the standard intents and applies only to nullable reference DTO properties. Without it, datamodel-code-generator merged an inline nullable schema with the non-null definition and dropped `None` from the generated field type.

A title-only layout was evaluated first with datamodel-code-generator 0.86.0. It left closed generic roots and nested inline types without stable default names even with `--use-title-as-name`. The `$defs` layout produces those names directly, so the checked-in schemas use `$defs`; titles are only used for the family wrapper. datamodel-code-generator was used only for evaluation and is not a build dependency.

For evaluation, datamodel-code-generator 0.86.0 was run with the standard `--use-title-as-name --use-type-alias` options. The resulting models use the schema names above, string enums, `RunMetadata` for `RunState.metadata`, and list/dict aliases for collection definitions. No JSON Pointer resolution, reference normalization, or schema rewriting is needed on the client side.

`SCHEMA_GENERATION` enables generator-specific attributes, including `Nullable` and `Description`, only in the .NET 10 generation tool. It also aliases `PngImage` to `string`; the actual net481 model uses `byte[]` and standard STJ Base64 serialization. The separate net481 sample project compiles the runtime DTOs with STJ 9.0.4. The net481 projects reference the framework's `System.ComponentModel.DataAnnotations` assembly for `Range`; neither the generator nor its dependencies are referenced by the Bridge DLL.

`RunIcons.segment_icons` is a required non-null array of nullable PNG strings. Generation 7.3.11 marks the collection nullable as well when applying the nested `Nullable(true, GenericParameter = 0)` attribute, and putting the collection constraint first makes the items non-nullable instead. The DTO therefore keeps the standard `If` and conditional `Nullable(false)` attributes, which reject a null collection without rejecting null entries. No additional refiner is added for this single field. datamodel-code-generator exposes the field as `list[str | None] | None` because it does not translate the conditional null exclusion into a type; the schema itself still rejects a null array. PNG data is only returned by `get_run_icons`, never inside `RunState`.

## Validation coverage

`pnpm run validate` uses Ajv 8.17.1 to validate each generated schema against the Draft 2020-12 meta-schema, then checks the protocol constraint cases in `samples/validation-cases.json` and every message serialized by the separate net481 sample project (`ContractSamples/ContractSamples.cs`).

The generation command only writes schemas. The net481 sample project writes actual C# messages covering every query and operation, every response and event type, empty/populated runs, separate icons, and nullable event/state fields. Ajv checks those serialized messages; the fixed fixtures retain protocol-specific rejection rules. Actual RPC dispatch and errors belong to the Windows communication tests.

## Known Schema and serializer differences

- `session_id` and `event_sequence` are `uint`; the serializer accepts zero, while the schemas reject it. The Bridge creates and sends nonzero values.
- `List<string>` elements and `Dictionary<string, string>` values are non-null in the schema, but `System.Text.Json` 9.0.4 can deserialize null elements and values. The Bridge's current outgoing state construction does not emit them.
- A reference DTO root may deserialize from JSON `null`, while the generated root schema requires an object.
- The image property description states Base64-encoded PNG. The schema does not verify Base64 or PNG contents; `System.Text.Json` rejects invalid Base64 when reading a `byte[]`.

These differences are recorded separately from schema conformance. The Schema Contract job checks the generated contract and intended valid/invalid fixtures on every pull request.
