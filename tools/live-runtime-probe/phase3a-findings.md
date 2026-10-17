# Phase 3A runtime and boundary findings

## Status

The old startup failure cannot be attributed conclusively from the available records: the earlier disposable copy was not a clean baseline because its `settings.cfg` pointed to the probe layout. In this new run, a fresh official 1.8.37 extraction had a live, responsive process after eight seconds without custom files; the window title was empty, so full UI readiness was not independently established. In Stage B, LiveSplit showed a `LiveSplit` main window but had exited by the next process check before the probe factory produced a log. The Windows Application event in that interval records that `Process.StartWithShellExecuteEx` was canceled inside `FiletypeRegistryHelper.RegisterFileFormatsIfNotAlreadyRegistered()`. This precedes component loading and does not implicate System.Text.Json.

Stage B is still blocked on resolving the file-type registration consent in a disposable environment. No probe JSON check or in-process DLL load path was recorded, so runtime compatibility and dependency resolution remain unverified. No production Bridge, WebSocket, Protobuf, deployment setting, or LiveSplit host configuration was changed.

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

Clean release and host inventory:

- Fresh official release extracted to `C:\Users\7huse\.codex\worktrees\ea11\LiveSplit.Bridge\.tmp\LiveSplitOfficialClean-1.8.37-20261017` from `https://github.com/LiveSplit/LiveSplit/releases/download/1.8.37/LiveSplit_1.8.37.zip`; ZIP SHA-256 `14BC8EF8DED9EF4033FB2F0CB6A152386D393127DA18A4DE14F096C5347AA991`.
- Stage A executable: `C:\Users\7huse\.codex\worktrees\ea11\LiveSplit.Bridge\.tmp\LiveSplitOfficialClean-1.8.37-20261017\LiveSplit.exe`.
- ProductVersion: `1.0.0+683cd60a9f3fe2dc31a5054c6d10f5d71f2e6ae1`.
- .NET Framework registry version: `4.8.09221`, Release `533509`.
- Stage A had no probe, custom layout, or added dependencies. The process remained alive and responsive for the eight-second observation interval, but its window title was empty; this is evidence the process started, not full UI-readiness proof. No matching `LiveSplit`, `.NET Runtime`, or `Application Error` entry was returned during the immediate query.
- Stage B used a second extraction of the same ZIP, with only `LiveSplit.JsonRuntimeProbe.dll` added by this task to `Components` and a one-component test layout. The host's existing assemblies and `LiveSplit.exe.config` were unchanged. The process initially showed the `LiveSplit` main window but exited before a PID-specific TEMP log appeared.
- Windows Application event at `2026-10-17 17:36:25`: Provider `LiveSplit`, Event ID `0`, message `This operation was canceled by the user.` Stack: `System.Diagnostics.Process.StartWithShellExecuteEx(ProcessStartInfo startInfo)` → `LiveSplit.Options.FiletypeRegistryHelper.RegisterFileFormatsIfNotAlreadyRegistered()`. This is the B-run startup exception in the test interval and occurs before the component factory. The event message does not expose an exception type or show a System.Text.Json exception. The earlier turn's startup failure is not timestamp-correlated with this record.
- No `.NET Runtime` or `Application Error` record was found in the queried interval. A successful component load, JSON run, loaded assembly path, dependency isolation result, and update heartbeat have not yet been recorded.
- The original prior test copy had `settings.cfg` with `RecentLayouts\LayoutPath` pointing to `JsonRuntimeProbe.lsl`, so launching that copy without command-line arguments still loaded the probe layout. It was not a clean no-probe comparison.
- No `LiveSplit.exe.config` was edited. All release copies and test layouts are disposable workspace files.

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

PoC Release build and run passed (32/32 C# serializer checks); `pnpm run validate` passed (3/3 Draft 2020-12 schemas, 80/80 fixtures, and 33/33 serialized cases). Probe Release build succeeded; NuGet emitted `NU1900` because it could not reach the vulnerability feed. The required `pnpm install --frozen-lockfile --ignore-scripts` re-run stalled at supply-chain policy verification and was interrupted, so the installed lockfile dependencies were used for validation. Production Bridge restore skipped missing LiveSplit project references. `dotnet test tests/LiveSplit.Bridge.Tests/LiveSplit.Bridge.Tests.csproj --configuration Release --no-restore` could not run because the test project's assets file is absent; its restore did not produce one in this checkout. Stage A process observation succeeded but full UI readiness is unconfirmed; Stage B stopped before component factory due canceled file-type registration. No production files were changed.

## Decision

The Phase 2 DTOs and System.Text.Json 9.0.4 build against `net481`, and the official LiveSplit archive contains matching dependency assemblies. The clean host starts, but the probe test is blocked before its factory by the canceled file-type registration launch. System.Text.Json is therefore **undetermined** for the production component until registration completes and the probe confirms loaded assembly locations, all JSON checks, and continued timer operation.
