# LiveSplit.Bridge JSON Schema PoC

Phase 1の検証基盤です。Bridge本体のProtobuf通信には組み込まず、C# RPC DTOからJSON Schemaを生成する動きを`net481`上で再実行できます。正式なJSON通信仕様やC# → Schema方式の採用を決めるものではありません。

## Requirements

- .NET SDK 10.0.302以上（PoCの実行確認は10.0.302）
- .NET Framework 4.8.1 Developer Pack / Targeting Pack
- NuGet接続（`System.Text.Json` 9.0.4の復元用）
- Draft 2020-12独立検証用: Node.js 20以上、npm
- npm接続（Ajv 8.17.1の復元用）。`package.json`はAjvのバージョンを固定しています。

## Build and generate schemas

Run from the repository root in PowerShell:

```powershell
dotnet restore tools/schema-poc/LiveSplit.Bridge.SchemaPoc.csproj
dotnet build tools/schema-poc/LiveSplit.Bridge.SchemaPoc.csproj --configuration Release --no-restore
dotnet run --project tools/schema-poc/LiveSplit.Bridge.SchemaPoc.csproj --configuration Release --no-build -- tools/schema-poc/out
```

`JsonSchemaExporter` writes `rpc-request.schema.json`, `rpc-response.schema.json`, `nullability-probe.schema.json`, and `session-id-probe.schema.json` to `tools/schema-poc/out/`. That directory and build outputs are ignored by Git. To refresh the two retained RPC results after generation:

```powershell
Copy-Item tools/schema-poc/out/rpc-request.schema.json tools/schema-poc/schema-results/rpc-request.schema.json
Copy-Item tools/schema-poc/out/rpc-response.schema.json tools/schema-poc/schema-results/rpc-response.schema.json
```

The two files in `schema-results/` are the actual Phase 1 output snapshots for review. They are informational PoC outputs, not the formal communication contract.

## Runtime behavior checks

The generator runs the C# deserialization and serialization cases and exits non-zero if any assertion fails. The expected current output is 26 PASS lines:

- Request discriminator dispatch, both methods, and `request_id` before `method` with `AllowOutOfOrderMetadataProperties = true`.
- The same reordered Request is rejected when the setting is false.
- Unknown method, method/params mismatch, missing or null params, missing or invalid operation, numeric enum, and unknown properties are rejected.
- The two Request variants serialize without adding a separate discriminator field; `method` is the discriminator. The get-timer request omits `params`.
- Query success, Control success with `result: null`, and error Response serialize without a type discriminator. The null-only converter rejects non-null input.
- `[JsonRequired]` distinguishes an omitted nullable property from a present `null`; non-nullable reference `null` is rejected.
- Root `null`, list `null` elements, and dictionary `null` values are accepted by the serializer despite the generated non-null Schema forms.
- Unknown properties are rejected.
- `uint` rejects values above its range but accepts zero. The Schema transform supplies the non-zero constraint.

## Independent Draft 2020-12 validation

The fixed cases in `samples/validation-cases.json` cover valid and invalid Request/Response values, including unknown method, method/params mismatch, result/error coexistence or omission, `result: true`, `error: null`, missing required fields, `session_id: 0`, and the maximum uint32 value.

Install the pinned validator and validate the freshly generated files:

```powershell
npm install --prefix tools/schema-poc --ignore-scripts --no-audit --no-fund
npm --prefix tools/schema-poc run validate
```

`validate-schemas.cjs` uses Ajv's Draft 2020-12 implementation. It compiles the actual `out/rpc-request.schema.json` and `out/rpc-response.schema.json`, which also checks the schema documents against Ajv's Draft 2020-12 meta-schema, then runs every fixed case. Expected result when Ajv is installed: `18/18 cases passed`.

### Result in the Phase 1 environment

Ajv was not present in the bundled Node modules; Python `jsonschema` was also absent, and no other suitable validator was available in the installed tool/package caches. Network access to package sources was unavailable, so the Ajv install and independent validation command could not be completed in this environment. The 18 Ajv results above are expected results from the designed cases, **not observed results**. The C# runtime checks did run successfully. Draft 2020-12 independent validation remains open.

## Observations and known mismatches

- The Request hierarchy produces a method-tagged `anyOf` schema with method-specific params and `additionalProperties: false`. The `request_id` field is `uint` only as a PoC implementation detail; its formal type and upper bound are undecided.
- The Response hierarchy omits a discriminator and exports three `anyOf` branches. Each branch requires either `result` or `error` and rejects other branch properties. Control's null-only value needs a small converter and `TransformSchemaNode` addition of `const: null`.
- `TreatNullObliviousAsNonNullable = true` excludes null at the exported root even though the serializer accepts root `null`.
- Generic nullability differs: `List<string>` items and `Dictionary<string,string>` values are emitted as non-null strings, while the .NET 9 serializer accepts nulls in these generic positions.
- Nullable `byte[]` is represented as a nullable string. The Schema does not constrain string content to Base64.
- The `uint` schema is emitted as `integer`; a small property transform adds `minimum: 1` and `maximum: 4294967295`.
- Existing `BridgeRuntime.GenerateSessionId` creates a non-zero 64-bit `ulong` from eight random bytes; it is not restricted to uint32. The Phase 1 PoC's uint32 model follows the supplied probe requirement and does not change Bridge code.
- Repeated Schema generation produced identical SHA-256 hashes for all four outputs.
- Mode A (C# polymorphic hierarchy → full RPC schema) worked for the tested Request and Response shapes. Mode B (separate DTO schemas plus Schema composition) was not built because the Phase 1 condition for it—a structural problem with `JsonPolymorphic`—did not occur.

## Phase 2 questions

- Run Ajv successfully and record actual meta-schema and positive/negative sample results.
- Decide formal JSON widths and limits for `request_id` and `event_sequence`.
- Reconcile nullability for root values, collection elements, dictionary values, and image Base64 representation.
- Determine whether a JSON protocol should preserve the current 64-bit session IDs or adopt a different width. Do not change the existing Protobuf contract in this Phase 1 PoC.
- Evaluate the complete RPC and Events model only in the next phase. This PoC does not implement a JSON endpoint or establish a final source-of-truth decision.
