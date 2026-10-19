# JSON v4 Schema contract

`src/LiveSplit.Bridge.Json/` is the single source of truth for the JSON v4 DTOs and serializer options. This tool generates Draft 2020-12 schemas from those DTOs with `System.Text.Json` 9.0.4. The checked-in schemas are reviewable generated output; do not edit them by hand.

## Requirements and commands

- .NET SDK 10.0.302 or later and the .NET Framework 4.8.1 Developer Pack
- Node.js 20 or later
- pnpm 12.9.1, pinned in `package.json`
- Ajv 8.17.1, pinned in `pnpm-lock.yaml`

Run from the repository root in PowerShell:

```powershell
dotnet restore tools/schema/LiveSplit.Bridge.Schema.csproj
dotnet build tools/schema/LiveSplit.Bridge.Schema.csproj --configuration Release --no-restore
dotnet run --project tools/schema/LiveSplit.Bridge.Schema.csproj --configuration Release --no-build -- tools/schema/out
./tools/schema/Sync-Schemas.ps1
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
| [`rpc-request.schema.json`](../../schemas/v4/rpc-request.schema.json) | `RpcRequest` | Five query methods and timer/game-time operations, including method and operation parameter rules |
| [`rpc-response.schema.json`](../../schemas/v4/rpc-response.schema.json) | `RpcResponse` | Query results, `result: null` control responses, and exclusive error responses |
| [`bridge-event.schema.json`](../../schemas/v4/bridge-event.schema.json) | `BridgeEvent` | All eight events and their `timer_state` rules |

The DTO hierarchy and standard polymorphism attributes define method, operation, and event discrimination. Small exporter transforms add integer bounds, image content annotations, and `const: null` for fields that only permit explicit null.

## Validation coverage

`pnpm run validate` uses Ajv 8.17.1 to validate each generated schema against the Draft 2020-12 meta-schema, then checks the fixed positive and negative cases in `samples/validation-cases.json` and every message serialized by the C# generator.

The C# program also checks communication behavior that Ajv cannot observe, such as Request type resolution, operation round-trips, response exclusivity, and event nullability. These checks focus on the DTO contract rather than duplicating tests of generic `System.Text.Json` behavior.

## Known Schema and serializer differences

- `session_id` and `event_sequence` are `uint`; the serializer accepts zero, while the schemas reject it. The Bridge creates and sends nonzero values.
- `List<string>` elements and `Dictionary<string, string>` values are non-null in the schema, but `System.Text.Json` 9.0.4 can deserialize null elements and values. The Bridge's current outgoing state construction does not emit them.
- A reference DTO root may deserialize from JSON `null`, while the generated root schema requires an object.
- `contentEncoding: base64` and `contentMediaType: image/png` describe the image representation. Ajv's default validation does not verify Base64 or PNG contents; `System.Text.Json` rejects invalid Base64 when reading a `byte[]`.

These differences are recorded separately from schema conformance. The Schema Contract job checks the generated contract and intended valid/invalid fixtures on every pull request.
