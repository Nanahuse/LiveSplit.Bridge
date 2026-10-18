# Phase 3C JSON WebSocket status

## Implementation

The Component starts `JsonBridgeHost`, which owns `JsonBridgeRuntime` and a JSON-only WebSocket transport. The transport uses `HttpListener` and .NET Framework 4.8.1 `System.Net.WebSockets`, binding `127.0.0.1` at the configured port. It serves `/bridge/v4/rpc` and `/bridge/v4/events`; the Component does not start the legacy Protobuf runtime.

RPC accepts one Text Message per JSON object. Binary messages, malformed JSON, non-object roots, and missing or invalid `request_id` values receive no response. A valid uint32 ID is retained in `invalid_request` responses for duplicate properties, unknown methods, and malformed known requests. Duplicate property names are rejected recursively. Valid but unauthorized Control requests receive `permission_denied`; Query and Events remain available for external Origins.

RPC input is received in 8 KiB chunks and counted in UTF-8 bytes. The transport closes with status 1009 once the accumulated Text Message exceeds 64 KiB. RPC responses have no matching size cap. Events use an ordered sender and queue bounded at 256 entries per subscriber. Enqueue only retains the shared event snapshot; it does not serialize or send. A full queue transitions that subscriber to a stopping state and schedules one disconnect. Other subscribers continue receiving events in order.

For an oversized RPC message, the server attempts the WebSocket close handshake with a one-second bound before disposing the connection. This reduces the chance of a TCP reset racing the client's observation of the required 1009 close status.

## Verification in this checkout

- Production Bridge Release build passed using temporary references to official LiveSplit 1.8.37 assemblies. The source submodule `external/LiveSplit` is absent, so the regular project restore/test graph is not available locally.
- A temporary focused test host passed **16/16** selected Facts: seven JSON RPC cases, three WebSocket Event cases, three deterministic subscription/overflow/dispose cases, port release/reconnect, the Origin matrix, and Component endpoint lifecycle. The Event tests cover all timer event kinds through the WebSocket contract and ContextChanged, including session IDs, ordering, timer state, and follow-up queries. The queue test blocks one sender, fills its 256 pending slots, verifies one slow-subscriber disconnect, and checks ordered delivery to a healthy subscriber.
- GitHub Actions run #107 first failed one existing 64 KiB overflow-close test after the client received an abrupt TCP reset. The same unchanged RPC boundary test also failed intermittently during local repeats with a WebSocket connection reset. After the bounded close-handshake fix, that test passed **8/8** repeated local runs and the focused suite passed **16/16**. GitHub Actions run #108 then passed the Windows build, CLI lint, **135/135** .NET tests, and **37/37** Python CLI tests.
- Schema PoC restore/build/run completed with **32/32** C# checks. `pnpm run validate` passed **3/3** Draft 2020-12 meta-schemas, **80/80** fixtures, and **33/33** actual C# serialized messages. The preceding `pnpm install --frozen-lockfile --ignore-scripts` did not finish: package-manager supply-chain policy verification could not access the configured network source. Validation used the existing installed Ajv dependencies. Schema output was regenerated and did not change.
- Runtime Probe Release build passed. Its log for process 18872 records all seven JSON checks as PASS, `JSON_CHECKS_COMPLETE`, and `UPDATE_HEARTBEAT`. It also records the actual loaded assembly paths listed below.
- The isolated LiveSplit process previously returned a production v4 Query response with `request_id: 700` echoed and a non-zero `session_id`. The earlier failed launch was caused by an incorrectly relative layout path; using the absolute layout path resolved that launch failure.
- A fresh UI recheck loaded the isolated `JsonBridgeV4` layout in LiveSplit and connected an Event client to port `54103`. The LiveSplit Control menu was used to Start, Split, and Reset. The live client captured `timer_started`, `timer_split`, and `timer_reset`, all with session ID `2065836812` and consecutive event sequences `1`, `2`, and `3`. The reset event contained `phase: not_running`, `split_index: -1`, and zeroed time values. The Runtime Probe log for process `14040` records all seven JSON checks as PASS and an `UPDATE_HEARTBEAT`; loaded assembly paths are recorded there as well. The event capture is `%CD%/.tmp/ui-test-events-retry.log` in the checkout, and the probe log is `%TEMP%/LiveSplit.JsonRuntimeProbe-14040-20261018T111010848Z.log`.
- This recheck confirms UI-originated Start/Split/Reset event delivery. A post-reset `get_timer_state` request returned `phase: not_running`, `split_index: -1`, and zero time values, matching the captured reset event; it echoed `request_id: 901` and the same session ID. Reconnecting a newly created Event client after disconnect and removing the Bridge component in the GUI to confirm port release remain **unverified**. Passing loopback lifecycle tests do not substitute for those live UI checks.
- The full local `dotnet test` suite is unavailable because the LiveSplit source submodule is absent. Local Python lint passed and 30 Python unit tests passed; all 7 E2E cases needed the unavailable local TestHost. The complete test suites passed in GitHub Actions run #108 for commit `c5f6c4f00f4e18eb61d271e9245ab78b2d5bba06`.

## Runtime Probe assembly evidence

The log is `%TEMP%\LiveSplit.JsonRuntimeProbe-18872-20261018T100757620Z.log` on the test host. It records LiveSplit's .NET Framework 4.8.1 process and these loaded assemblies:

| Assembly | Assembly version | Observed load path |
| --- | --- | --- |
| `LiveSplit` | 1.0.0.0 | `<LiveSplit root>\LiveSplit.exe` |
| `LiveSplit.Core` | 1.0.0.0 | `<LiveSplit root>\LiveSplit.Core.dll` |
| `LiveSplit.View` | 1.0.0.0 | `<LiveSplit root>\LiveSplit.View.dll` |
| `LiveSplit.Bridge` | 1.0.0.0 | `<LiveSplit root>\Components\LiveSplit.Bridge.dll` |
| `LiveSplit.JsonRuntimeProbe` | 1.0.0.0 | `<LiveSplit root>\Components\LiveSplit.JsonRuntimeProbe.dll` |
| `System.Text.Json` | 9.0.0.4 | `<LiveSplit root>\System.Text.Json.dll` |
| `System.Text.Encodings.Web` | 9.0.0.4 | `<LiveSplit root>\System.Text.Encodings.Web.dll` |
| `System.Memory` | 4.0.1.2 | `<LiveSplit root>\System.Memory.dll` |
| `System.Buffers` | 4.0.3.0 | `<LiveSplit root>\System.Buffers.dll` |
| `System.Numerics.Vectors` | 4.1.4.0 | `<LiveSplit root>\System.Numerics.Vectors.dll` |
| `Microsoft.Bcl.AsyncInterfaces` | 9.0.0.4 | `<LiveSplit root>\Microsoft.Bcl.AsyncInterfaces.dll` |
| `System.IO.Pipelines` | 9.0.0.4 | `<LiveSplit root>\System.IO.Pipelines.dll` |
| `System.Runtime.CompilerServices.Unsafe` | 6.0.0.0 | `<LiveSplit root>\System.Runtime.CompilerServices.Unsafe.dll` |
| `System.Threading.Tasks.Extensions` | 4.2.0.1 | `<LiveSplit root>\System.Threading.Tasks.Extensions.dll` |
| `System.ValueTuple` | 4.0.0.0 | .NET Framework GAC |

This probe proves the DTO serializer can run in the host and that the official LiveSplit root supplied the runtime dependencies in that process. It does not yet prove the UI-driven v4 Event flow or establish a final Bridge distribution policy. No production distribution allow-list or host configuration was changed.

## Remaining Phase 3C work

- Reconnect a new Event client after disconnect.
- Remove the Bridge Component in the interactive LiveSplit layout and verify that the WebSocket port is released.
- Full .NET and Python suites passed in Actions run #108; local full-suite execution still requires the LiveSplit source submodule and CLI TestHost.
- Re-run frozen pnpm installation when the package policy source is available; Ajv validation itself passed using the already-restored dependencies.
- Keep the existing distribution list unchanged unless a production runtime capture demonstrates a missing dependency.
- Phase 3D cleanup remains out of scope: remove Protobuf/v3 implementation and dependencies only after the JSON path is accepted and legacy tests are intentionally retired.

Phase 3C is **not complete**: the real LiveSplit UI Event sequence now passes, as do the local deterministic queue tests, WebSocket integration tests, schema checks, and Runtime Probe JSON checks. Event-client reconnect, live Reset/GetRun consistency, GUI component removal and port release, and a latest full CI run remain outstanding. PR #35 remains Draft; this evidence does not decide adoption or release readiness.
