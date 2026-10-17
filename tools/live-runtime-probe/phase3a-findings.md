# Phase 3A runtime and boundary findings

## Status

This phase produced a reusable in-process probe and an evidence-based dependency inventory. The required LiveSplit execution did not complete: the available LiveSplit copy showed a top-level `Error` window before the probe component loaded, and no probe log was written. Therefore in-process compatibility and deployment-layout resolution remain unverified. No production Bridge, WebSocket, Protobuf, or deployment setting was changed.

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

The package graph is not by itself a deployment list. All nine listed runtime assemblies appeared in the PoC's actual `bin/Release/net481` output. Their presence in build output confirms resolved runtime assets, not that each assembly is loaded by the exercised JSON paths. The portable LiveSplit 1.8.37 directory already contains all nine names, with the same assembly versions observed in the PoC output, including System.Text.Json 9.0.0.4. This is evidence of matching files, not proof of which copy the LiveSplit process binds at runtime. The probe records each loaded assembly's version and `Location` to answer that once the host reaches the component.

The current `BridgeDistributionFiles` are only `LiveSplit.Bridge.dll`, `LiveSplit.Bridge.Protocol.dll`, and `Google.Protobuf.dll`. If the Bridge directly uses System.Text.Json, its deployment declaration would not carry the nine PoC runtime DLLs to `Components`. The LiveSplit root currently has same-version copies, which may satisfy load requests from that host, but `System.Text.Json.dll` resolution from a component path and compatibility with other installed components have not been demonstrated. No unconditional copy/overwrite or binding redirect is recommended from this incomplete evidence. The existing portable `LiveSplit.exe.config` already contains redirects for System.Memory and Unsafe; it was not changed.

The production Bridge restore could not include the LiveSplit project references because `external/LiveSplit/src/LiveSplit.Core` and `UpdateManager` are absent in this checkout. Its observed direct/transitive package graph is consequently incomplete for production-build conclusions. The current package list includes WebSocketSharp 1.0.3-rc11, Google.Protobuf 3.24.1, Buffers 4.4.0, Memory 4.5.3, Numerics.Vectors 4.4.0, and Unsafe 4.5.2.

## Probe and host execution

`LiveSplit.JsonRuntimeProbe.csproj` targets `net481`, references the available host's `LiveSplit.Core.dll` and `UpdateManager.dll`, links the Phase 2 DTOs and shared serializer options, and builds successfully. The checks cover Request method and operation resolution, signed Game Time ticks, Query response IDs, Control `result: null`, error response, Timer and reset Events, and null-only run/context Events. The probe also records loaded dependency locations and an update-loop heartbeat.

Available host inventory:

- Install directory: `C:\Users\7huse\Downloads\LiveSplit_1.8.37`.
- ProductVersion: `1.0.0+683cd60a9f3fe2dc31a5054c6d10f5d71f2e6ae1`.
- .NET Framework registry version: `4.8.09221`, Release `533509`.
- The copied app displayed a top-level window titled `Error` both with the probe layout and with default startup. No probe log appeared. The exact dialog cause could not be obtained, so the requested JSON cases, actual loaded assembly paths, isolation from NuGet/build folders, and normal LiveSplit operation are not verified.
- No `LiveSplit.exe.config` file was edited. Probe attempts used a disposable copy of the portable install.

Re-run instructions and the copy-layout limitation are in this directory's `README.md`.

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

PoC package graph and both probe restore/build succeeded; NuGet emitted `NU1900` because it could not reach the vulnerability feed. Production Bridge restore skipped missing LiveSplit project references. `dotnet test tests/LiveSplit.Bridge.Tests/LiveSplit.Bridge.Tests.csproj --configuration Release --no-restore` could not run because the test project's assets file is absent; its restore did not produce one in this checkout. The actual LiveSplit process test is incomplete as described above. No production files were changed.

## Decision

The Phase 2 DTOs and System.Text.Json 9.0.4 build against `net481`, and the available LiveSplit installation contains matching dependency assemblies. That is not sufficient evidence to approve System.Text.Json for the production component: the actual LiveSplit in-process probe never loaded. Repeat in a functioning LiveSplit 1.8.37 process and confirm assembly locations, dependency-only deployment layout, all JSON checks, and continued timer operation before making an adoption decision.
