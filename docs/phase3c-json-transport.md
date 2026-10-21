# Phase 3C JSON WebSocket status

## Implementation

The Component starts `JsonBridgeHost`, which owns `JsonBridgeRuntime` and a JSON-only WebSocket transport. The transport uses LiveSplit's WebSocketSharp 1.0.3-rc11, binding `127.0.0.1` at the configured port. It serves `/bridge/v4/rpc` and `/bridge/v4/events`; the Component does not start the legacy Protobuf runtime.

RPC accepts one Text Message per JSON object. Binary messages, malformed JSON, non-object roots, and missing or invalid `request_id` values receive no response. A valid uint32 ID is retained in `invalid_request` responses for duplicate properties, unknown methods, and malformed known requests. Duplicate property names are rejected recursively. Valid but unauthorized Control requests receive `permission_denied`; Query and Events remain available for external Origins.

WebSocketSharp assembles a message before delivering it to the RPC behavior. The behavior rejects payloads over 64 KiB with close status 1009 before JSON parsing. The library has no public streaming receive-size limit, so this is a limit on accepted RPC messages, not on fragment-buffer allocation. RPC responses have no matching size cap.

Events use one shared buffer bounded at 256 snapshots and one background publisher. Enqueue never waits for network writes; a full buffer drops new notifications. WebSocketSharp owns sessions, broadcast, and close handshakes. There are no per-subscriber queues, sender tasks, custom disconnect states, retries, or additional delivery-order guarantees. A slow connection can delay the shared publisher; clients recover current state with Query rather than relying on every notification.

The sections below record the earlier Phase 3C implementation and its verification, not the current transport. The obsolete standalone Runtime Probe has been removed; current RPC/Schema tests and the CLI TestHost cover its responsibilities.

## Verification in this checkout

- Production Bridge Release build passed using temporary references to official LiveSplit 1.8.37 assemblies. The source submodule `external/LiveSplit` is absent, so the regular project restore/test graph is not available locally.
- A temporary focused test host passed **16/16** selected Facts: seven JSON RPC cases, three WebSocket Event cases, three deterministic subscription/overflow/dispose cases, port release/reconnect, the Origin matrix, and Component endpoint lifecycle. The Event tests cover all timer event kinds through the WebSocket contract and ContextChanged, including session IDs, ordering, timer state, and follow-up queries. The queue test blocks one sender, fills its 256 pending slots, verifies one slow-subscriber disconnect, and checks ordered delivery to a healthy subscriber.
- GitHub Actions run #107 first failed one existing 64 KiB overflow-close test after the client received an abrupt TCP reset. The same unchanged RPC boundary test also failed intermittently during local repeats with a WebSocket connection reset. After the bounded close-handshake fix, that test passed **8/8** repeated local runs and the focused suite passed **16/16**. GitHub Actions run #108 then passed the Windows build, CLI lint, **135/135** .NET tests, and **37/37** Python CLI tests.
- Schema PoC restore/build/run completed with **32/32** C# checks. `pnpm run validate` passed **3/3** Draft 2020-12 meta-schemas, **80/80** fixtures, and **33/33** actual C# serialized messages. The preceding `pnpm install --frozen-lockfile --ignore-scripts` did not finish: package-manager supply-chain policy verification could not access the configured network source. Validation used the existing installed Ajv dependencies. Schema output was regenerated and did not change.
- Runtime Probe Release build passed. Its log for process 18872 records all seven JSON checks as PASS, `JSON_CHECKS_COMPLETE`, and `UPDATE_HEARTBEAT`. It also records the actual loaded assembly paths listed below.
- The isolated LiveSplit process previously returned a production v4 Query response with `request_id: 700` echoed and a non-zero `session_id`. The earlier failed launch was caused by an incorrectly relative layout path; using the absolute layout path resolved that launch failure.
- The isolated LiveSplit 1.8.37 `JsonBridgeV4` layout was used for UI-originated Start/Split/Reset. At port `54103`, the client received `timer_started`, `timer_split`, and `timer_reset` with session `2065836812` and sequences `1`, `2`, `3`; Reset reported `phase: not_running`, `split_index: -1`, and zero time values. The event capture is `.tmp/ui-test-events-retry.log`. Runtime Probe process `14040` logged all seven JSON checks as PASS and `UPDATE_HEARTBEAT`; its dependency paths are recorded in `%TEMP%/LiveSplit.JsonRuntimeProbe-14040-20261018T111010848Z.log`.
- The UI observations used separate LiveSplit sessions: the Start/Split/Reset capture used `session_id: 2065836812`; the later Events-client reconnection capture used `session_id: 2208441326`. Their sequence numbers are not one continuous series and must not be compared across those session IDs. Events are ordered within a session; a new session starts its own sequence.
- A separate test loaded a data-bearing `TestRun.lss` into an isolated LiveSplit 1.8.37 copy. Loading it emitted `run_changed` with `timer_state: null`, `session_id: 3498860924`, `event_sequence: 1`. `get_run` returned game `Bridge Test Game`, category `Any%`, three ordered segments (`Intro`, `Middle`, `Finish`), and RealTime PB cumulative ticks `150000000`, `400000000`, and `700000000`. Best Segment RealTime ticks were `120000000`, `200000000`, and `250000000`; GameTime values were null. LiveSplit generated `Best Segments` and `Average Segments` comparisons in addition to `Personal Best`.
- The fixture uses `<Run id="" />`, the standard unset ID representation in the saved test file. The first capture exposed `run_id: ""`; the JSON adapter now normalizes an empty LiveSplit Run ID to `null`, matching the protocol's unset representation. The updated value was verified by a focused regression test. The test also exercised `TryUpdateRunTimings` and confirmed the normalized ID remains null.
- After loading the file-backed Run, UI Start and Reset emitted `timer_started` and `timer_reset` in the same session `3498860924`, with sequences `2` and `3`. Reset reported `not_running`, split index `-1`, zero real/game time, `is_game_time_initialized: true`, and `is_game_time_paused: false`. `get_run` before and after Reset returned the same run metadata, comparisons, segment order, and timing values. No split was performed, and the temporary fixture was not saved after the UI actions.
- Component lifecycle was checked through LiveSplit's Layout Editor with both an RPC socket and an Events socket held open on `54103`. RPC first returned the reset TimerState with `request_id: 902`; Events and RPC clients then both observed their sockets close when the Bridge component was removed. The clients reported an abrupt remote close without a WebSocket close handshake. A localhost TCP probe to `54103` was refused after removal. Re-adding Bridge and setting the same port showed `Status: Running` and accepted a new TCP connection. The temporary UI layout was restored with Bridge and Runtime Probe present. Logs: `.tmp/component-delete-events.log` and `.tmp/component-delete-rpc.log`.
- The restored component was queried once more on `54103`: `get_run` (`request_id: 900`) and `get_timer_state` (`request_id: 901`) returned the same current session (`2666394174`). TimerState was reset (`not_running`, split index `-1`, zero time values); the same empty test Run shape was returned. No personal Run or normal LiveSplit installation was used. The data-bearing Run validation above used only the temporary fixture in an isolated copy.
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

This probe proves the DTO serializer can run in the host and that the official LiveSplit root supplied the runtime dependencies in that process. UI-driven v4 Events and component lifecycle were later checked separately as recorded above. No production distribution allow-list or host configuration was changed.

## Remaining Phase 3C limitations

- The file-backed fixture's Run ID was empty, so a non-empty external Run ID was not exercised. GameTime values were absent in the fixture; only null GameTime serialization was observed in this UI pass.
- Full .NET and Python suites passed in Actions run #111 for the then-current documentation commit; local full-suite execution still requires the LiveSplit source submodule and CLI TestHost.
- Re-run frozen pnpm installation when the package policy source is available; Ajv validation itself passed using the already-restored dependencies.
- Keep the existing distribution list unchanged unless a production runtime capture demonstrates a missing dependency.
- Phase 3D cleanup remains out of scope: remove Protobuf/v3 implementation and dependencies only after the JSON path is accepted and legacy tests are intentionally retired.

Phase 3C's specified UI Event, Event-client reconnect, Bridge removal/re-add, and data-bearing Run checks are observed in isolated LiveSplit 1.8.37. The file-backed Run exposed an empty-string Run ID mismatch; the adapter now maps empty IDs to null, with a focused test. The remaining runtime-data limitations are that no non-empty Run ID or GameTime values were exercised. GitHub Actions run `39135338091` passed on commit `3214a7a`, including the Windows build, .NET tests, and CLI tests. PR #35 remains Draft, and this evidence does not decide adoption or release readiness.
