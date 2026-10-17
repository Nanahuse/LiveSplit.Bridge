# Phase 3A runtime and boundary findings

## Status

The previous `Process.StartWithShellExecuteEx` cancellation was confirmed in the Application log inside `FiletypeRegistryHelper.RegisterFileFormatsIfNotAlreadyRegistered()`. LiveSplit 1.8.37 catches the helper failure; there is no evidence that the cancellation itself ended the process. Its actual exit cause in the previous run remains unknown. That run also lacked the required `ComponentFactoryAttribute`, and its test layout had not been validated, so its absence of probe logs did not establish a JSON dependency failure.

This rerun added the factory attribute and a repository layout based on the saved LiveSplit layout structure. A fresh Stage A extraction started and remained responsive. A separate Stage B extraction loaded the explicit layout, created the probe factory and component, passed every JSON check, recorded loaded assembly paths, and continued its update loop for at least five seconds. Thus System.Text.Json 9.0.4 works in the LiveSplit 1.8.37 / .NET Framework 4.8.1 process for the exercised DTO operations without adding dependency DLLs or changing host configuration. This proves the probe's runtime compatibility in this host distribution; it does not settle how a future Bridge deployment should handle assembly sharing with other components. No production Bridge, WebSocket, Protobuf, deployment setting, registry entry, or LiveSplit host configuration was changed.

The sandbox denied `Get-WinEvent` access during this rerun. The prior cancellation record remains documented above; no new event-log query result is claimed.

## Factory and layout verification

- The Release DLL was inspected with reflection against the extracted LiveSplit assemblies. It contains one `LiveSplit.UI.Components.ComponentFactoryAttribute` whose constructor argument is `Factory`.
- `JsonRuntimeProbe.lsl` was derived from the saved layout at `C:\Users\7huse\Documents\test.lsl`; it retains the full `Layout` settings and layout-level elements while replacing `Components` with one probe entry (`Path` = `LiveSplit.JsonRuntimeProbe.dll`). The committed file has no absolute user path and loaded successfully when explicitly passed with `-l`.
- Stage A used a fresh extraction of the official LiveSplit 1.8.37 archive with no probe or custom layout. Its `LiveSplit` window was responsive after 12 seconds.
- Stage B used a different fresh extraction of the same ZIP. Only the probe DLL was added to `Components`; the checked-in layout was placed beside `LiveSplit.exe`. No System.Text.Json dependencies were copied and the host config remained unchanged.
- Both launches ran without elevation. No registry or file association changes were made.

## Restore and runtime dependencies

The `net481` schema PoC directly references `System.Text.Json` 9.0.4. Its restored transitive packages and actual Release output are:

| Package | Assembly version in output | Why it is present |
| --- | --- | --- |
| System.Text.Json 9.0.4 | 9.0.0.4 | JSON serialization and schema exporter |
| System.Text.Encodings.Web 9.0.4 | 9.0.0.4 | JSON escaping support used by System.Text.Json |
| Microsoft.Bcl.AsyncInterfaces 9.0.4 | 9.0.0.4 | .NET Framework compatibility interfaces |
| System.IO.Pipelines 9.0.4 | 9.0.0.4 | Runtime dependency in the restored graph |
| System.Buffers 4.5.1 | 4.0.3.0 | Buffer APIs |
| System.Memory 4.5.5 | 4.0.1.2 | Span and memory APIs |
| System.Numerics.Vectors 4.5.0 | 4.1.4.0 | Vector APIs used by compatibility dependencies |
| System.Runtime.CompilerServices.Unsafe 6.0.0 | 6.0.0.0 | Low-level APIs used by compatibility dependencies |
| System.Threading.Tasks.Extensions 4.5.4 | 4.2.0.1 | Task compatibility APIs |
| System.ValueTuple 4.5.0 | 4.0.3.0 | Tuple compatibility types |

The package graph is not by itself a deployment list. Every listed runtime assembly appeared in the PoC's actual `bin/Release/net481` output. The official LiveSplit 1.8.37 root also contains those dependency DLLs. Their presence is not proof of binding; the Stage B process log below records actual loaded versions and paths. In this run, System.Text.Json and its loaded dependencies resolved from the LiveSplit root, except System.ValueTuple which resolved from the .NET GAC.

The current `BridgeDistributionFiles` are only `LiveSplit.Bridge.dll`, `LiveSplit.Bridge.Protocol.dll`, and `Google.Protobuf.dll`. The probe demonstrates that a component DLL in `Components` can use System.Text.Json resolved from the official LiveSplit root in this host without additional copies or a config change. It does not establish safe deployment for installations without these root DLLs, or collision behavior with different shared versions. No unconditional copy/overwrite or binding redirect is recommended from this probe. The existing portable `LiveSplit.exe.config` contains redirects for System.Memory and Unsafe; it was not changed.

The production Bridge restore could not include the LiveSplit project references because `external/LiveSplit/src/LiveSplit.Core` and `UpdateManager` are absent in this checkout. Its observed direct/transitive package graph is consequently incomplete for production-build conclusions. The current package list includes WebSocketSharp 1.0.3-rc11, Google.Protobuf 3.24.1, Buffers 4.4.0, Memory 4.5.3, Numerics.Vectors 4.4.0, and Unsafe 4.5.2.

## Probe and host execution

`LiveSplit.JsonRuntimeProbe.csproj` targets `net481`, references the available host's `LiveSplit.Core.dll` and `UpdateManager.dll`, links the Phase 2 DTOs and shared serializer options, and builds successfully. The checks cover Request method and operation resolution, signed Game Time ticks, Query response IDs, Control `result: null`, error response, Timer and reset Events, and null-only run/context Events. The probe also records loaded dependency locations and an update-loop heartbeat.

The official release ZIP was `https://github.com/LiveSplit/LiveSplit/releases/download/1.8.37/LiveSplit_1.8.37.zip`, SHA-256 `14BC8EF8DED9EF4033FB2F0CB6A152386D393127DA18A4DE14F096C5347AA991`. This rerun extracted fresh Stage A and Stage B copies under `.tmp/Phase3A-FactoryLayout-20261017/`. LiveSplit ProductVersion was `1.0.0+683cd60a9f3fe2dc31a5054c6d10f5d71f2e6ae1`; .NET Framework 4.8.1 registry release was `533509` (`4.8.09221`), CLR `4.0.30319.42000`.

- Stage A had no probe, custom layout, or added dependencies. After 12 seconds, PID `6684` was alive, its `LiveSplit` window handle was nonzero, and the window was responsive.
- Stage B ran `LiveSplit.exe -l .\JsonRuntimeProbe.lsl` from a separate fresh extraction. PID `6532` had a responsive `LiveSplit` window after 15 seconds. The host DLLs and `LiveSplit.exe.config` were untouched; no JSON dependency was added.
- The PID-specific log was `%LOCALAPPDATA%\Packages\sandbox.{b1b6419b-3913-4bcd-a018-500124793b83}\AC\Temp\LiveSplit.JsonRuntimeProbe-6532-20261017T100418115Z.log`. It recorded all required stage markers through `UPDATE_HEARTBEAT` and seven individual JSON `PASS` entries.
- The seven successful checks covered Request method/Timer Params resolution; Game Time `set` with negative ticks; Request/Response uint32 boundary preservation; Control `result: null`; Error Response; Timer Started and nullable Timer Reset Events; and null-only Run/Context changed Events.

Loaded assemblies recorded from the actual Stage B process:

| Assembly | Version | Loaded location |
| --- | --- | --- |
| `LiveSplit.JsonRuntimeProbe` | 1.0.0.0 | `StageB\Components\LiveSplit.JsonRuntimeProbe.dll` |
| `System.Text.Json` | 9.0.0.4 | `StageB\System.Text.Json.dll` |
| `System.Text.Encodings.Web` | 9.0.0.4 | `StageB\System.Text.Encodings.Web.dll` |
| `Microsoft.Bcl.AsyncInterfaces` | 9.0.0.4 | `StageB\Microsoft.Bcl.AsyncInterfaces.dll` |
| `System.IO.Pipelines` | 9.0.0.4 | `StageB\System.IO.Pipelines.dll` |
| `System.Memory` | 4.0.1.2 | `StageB\System.Memory.dll` |
| `System.Buffers` | 4.0.3.0 | `StageB\System.Buffers.dll` |
| `System.Numerics.Vectors` | 4.1.4.0 | `StageB\System.Numerics.Vectors.dll` |
| `System.Runtime.CompilerServices.Unsafe` | 6.0.0.0 | `StageB\System.Runtime.CompilerServices.Unsafe.dll` |
| `System.Threading.Tasks.Extensions` | 4.2.0.1 | `StageB\System.Threading.Tasks.Extensions.dll` |
| `System.ValueTuple` | 4.0.0.0 | .NET GAC under `C:\WINDOWS\Microsoft.Net\assembly\GAC_MSIL\` |
| `LiveSplit`, `LiveSplit.Core`, `LiveSplit.View` | 1.0.0.0 | Stage B executable and host DLLs in the extraction root |

All JSON dependencies used in the run were already present in the official LiveSplit distribution. `System.ValueTuple` resolved from the GAC. Thus no extra DLL was needed for this probe on this host. This does not guarantee identical binding behavior for all installed components or user installations. The prior file-type registration event confirms a canceled helper launch, but the helper catches the failure; it does not prove process termination. Its actual cause remains unknown. `Get-WinEvent` access was denied in this rerun, so no new event-log result is claimed. The earlier test copy's `settings.cfg` contained a RecentLayouts pointer to the probe layout and was not a clean comparison. No host config, registry, or file association was changed.

## RPC receive-boundary proposal

The following preserves the specified distinction between an unusable correlation ID and an identifiable malformed request:

| Input | Handling |
| --- | --- |
| WebSocket Binary Message | Drop the message without a JSON response; leave the connection open. The endpoint expects one Text Message per request. |
| Invalid JSON syntax or valid JSON whose root is not an object | Drop without a response. No reliable request envelope exists from which to identify a response. |
| Missing, null, non-integer, negative, or greater-than-`uint.MaxValue` `request_id` | Drop without a response; never substitute zero. |
| Valid `request_id`, unknown `method` | Return `invalid_request` with that exact ID. |
| Valid `request_id`, known method with missing, malformed, mismatched, or unknown `params`/operation | Return `invalid_request` with that exact ID. |
| Valid Request shape for a Control method on a connection without Control permission | Return `permission_denied` with that exact ID and do not execute it. |

For a well-formed known Control Request from a disallowed connection, permission denial is evaluated before invoking the operation. Structural validation precedes authorization, so a malformed control payload remains `invalid_request`; it cannot reach the adapter. Parse the ID independently from the discriminator so a valid ID survives an unknown method. Duplicate JSON property names should be rejected as malformed to avoid different ID/method interpretations between parsers. Response `request_id` echoes the parsed value, including zero.

The current WebSocketSharp code receives complete message bytes in `e.RawData`; it has no Bridge-level request byte cap. The WebSocketSharp receive implementation also accumulates fragmented frames into a `MemoryStream`, so a JSON parser-side check alone would occur after message memory has already been allocated. A future JSON endpoint should enforce a byte limit while receiving frames or add an equivalent transport-level limit before buffering the entire message. The RPC schema has only a uint32 ID, a short fixed method name, and either a small operation enum or a signed ticks integer; it has no arbitrary strings, collections, or binary fields. The largest defined compact envelope is the `game_time_operation` `set` request; even with maximum-width uint32 and signed int64 values it is under 250 UTF-8 bytes. As a starting bound, 64 KiB per assembled Text Message provides over 250x headroom while limiting whitespace padding and pre-parse memory use. Reject an oversized message and close that connection with a message-too-big protocol close; do not try to return a JSON error for an envelope that was not fully accepted. Confirm the exact limit against client formatting and WebSocketSharp's pinned version before implementation.

Current implementation observation: the Bridge has no application-level size limit. WebSocketSharp's public `WebSocket` API reflected from the installed copy exposes no message-length cap; the internal `FragmentLength` is a send-fragment size, not an inbound maximum. The library's upstream source shows continuation payloads appended to a `MemoryStream` until the final fragment, which supports the need for an earlier transport cap. This assessment does not modify the current Binary endpoint.

## Origin permissions to preserve

`WebSocketRpcBehavior.OnOpen` computes `allowControl` once per RPC connection using the current `WebSocketOriginValidator`. Keep this separate from JSON parsing and request validation.

| Origin | Query | Control | Events |
| --- | --- | --- | --- |
| Missing / empty | Allow | Allow | Allow |
| `http(s)://localhost`, `127.0.0.1`, or `[::1]` | Allow | Allow | Allow |
| External origin | Allow | Deny | Allow |
| `Origin: null` | Allow | Deny | Allow |

Existing Origin tests cover the RPC Query/Control distinction. The Events endpoint has no Origin restriction. Do not add an authentication mechanism in the JSON migration.

## Phase 3B implementation map

| Area | Expected migration work |
| --- | --- |
| `BridgeRuntime.cs` | Replace Protobuf request dispatch with DTO dispatch; preserve `request_id`; define JSON success/error mapping; use nonzero uint32 Session ID; implement event sequence starting at 1 and wrapping max to 1 under `eventGate`. |
| `LiveSplitAdapter.cs` | Map LiveSplit state to JSON DTOs; preserve ticks, nulls, metadata variable semantics, comparison/segment ordering and image bytes; implement state cloning/equality without Protobuf generated types. |
| `WebSocketTransport.cs` | Send/receive Text messages on RPC and Events; add a transport-level message-size cap; preserve loopback binding, queue ordering, session cleanup, and event publication behavior. |
| `WebSocketBehaviors.cs` | Parse and validate one JSON object; keep per-connection Origin-derived Control permission; distinguish invalid envelopes from identifiable invalid requests; serialize text errors/responses. |
| `LiveSplit.Bridge.csproj` | Add JSON DTO/source and System.Text.Json package reference; settle DLL deployment only after the host probe proves resolution and collision behavior. |
| `DeploymentProjectTests.cs` | Update expected distribution files and assert only the justified runtime closure is deployed, without replacing host-shared DLLs blindly. |
| JSON DTOs / Schema PoC | Move or share the validated DTOs/options as appropriate; keep runtime dependencies out of Schema generation-only code if possible; maintain Schema parity. |

## Commands and evidence

Commands run in this checkout:

```powershell
dotnet list tools/schema-poc/LiveSplit.Bridge.SchemaPoc.csproj package --framework net481 --include-transitive
dotnet restore tools/live-runtime-probe/LiveSplit.JsonRuntimeProbe.csproj -p:LiveSplitInstallPath='C:\Users\7huse\Downloads\LiveSplit_1.8.37'
dotnet build tools/live-runtime-probe/LiveSplit.JsonRuntimeProbe.csproj --configuration Release --no-restore -p:LiveSplitInstallPath='C:\Users\7huse\Downloads\LiveSplit_1.8.37'
```

PoC Release build and run passed (32/32 C# serializer checks); `pnpm run validate` passed (3/3 Draft 2020-12 schemas, 80/80 fixtures, and 33/33 serialized cases). `pnpm install --frozen-lockfile --ignore-scripts` did not complete within the 30-second command window and remained at supply-chain policy verification; validation used the already-present lockfile dependencies. Probe Release build succeeded; NuGet emitted `NU1900` because it could not reach the vulnerability feed. Production Bridge restore skips missing LiveSplit project references in this checkout. `dotnet test tests/LiveSplit.Bridge.Tests/LiveSplit.Bridge.Tests.csproj --configuration Release --no-restore` remains unavailable because its assets file is absent. Both host stages launched responsively; Stage B reached all component, JSON, assembly-path, and heartbeat milestones. No production files were changed.

## Decision

System.Text.Json 9.0.4 is **runtime-compatible for the exercised DTO operations inside LiveSplit 1.8.37 on .NET Framework 4.8.1**: LiveSplit discovered the factory, all seven checks passed, loaded dependency paths were recorded, and the update loop remained active. Only the probe DLL was added to the official distribution. This supports using System.Text.Json in the LiveSplit host. The production Bridge packaging policy still needs to account for shared assemblies and other user installations; this probe does not establish that distribution policy. Phase 3B was not started.
