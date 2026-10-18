# Phase 3C JSON WebSocket status

## Implementation

The Component now starts `JsonBridgeHost`, which owns `JsonBridgeRuntime` and a JSON-only WebSocket transport. The transport uses `HttpListener` with the `System.Net.WebSockets` APIs and binds `127.0.0.1` at the configured port. It serves `/bridge/v4/rpc` and `/bridge/v4/events`; the Component does not start the legacy Protobuf runtime.

RPC accepts one Text Message per JSON object. Binary messages, malformed JSON, non-object roots, and missing or invalid `request_id` values receive no response. A valid uint32 ID is retained in `invalid_request` responses for duplicate properties, unknown methods, and malformed known requests. Duplicate property names are rejected recursively. Valid but unauthorized Control requests receive `permission_denied`; Query and Events remain available for external Origins.

RPC input is received in 8 KiB chunks and counted in UTF-8 bytes. The transport stops receiving and closes with status 1009 once the accumulated Text Message exceeds 64 KiB; the oversized payload is not passed to the JSON parser. RPC responses are not subject to that request limit. Events use a per-subscriber queue bounded at 256 entries. The queues hold references to shared event snapshots, so 256 entries bound each subscriber's reference storage to roughly 2 KiB plus queue overhead while allowing a short burst. Enqueue does not serialize or send; a full queue schedules disconnection of that subscriber instead of silently dropping an Event. Each subscriber has one ordered sender.

## Verification performed in this checkout

- Release build of `src/LiveSplit.Bridge/LiveSplit.Bridge.csproj` succeeded using temporary references to the official LiveSplit 1.8.37 assemblies because the `external/LiveSplit` source submodule is absent.
- A temporary focused test host compiled the current Bridge and the JSON WebSocket/component test sources. Loopback integration passed 11/11 selected Facts, including all five Query methods, all Timer and Game Time operation discriminators, malformed envelopes, nested duplicate properties, Binary drop, 64 KiB boundary and fragmented overflow, an oversized GetRun response containing a noisy PNG icon, the Origin matrix, two Event subscribers, Timer Reset then RunChanged ordering, latest GetRun state, and port reuse after disposal.
- Schema PoC Release build and execution passed 32/32 C# serializer checks. Ajv validated 3/3 Draft 2020-12 schemas, 80/80 fixtures, and 33/33 C# serialized messages. No Schema files changed in Phase 3C.
- The normal `dotnet test` project could not be restored here: the checkout has no LiveSplit source submodule or test `project.assets.json`; the SDK restore graph failed before producing assets. The focused host is not the full test suite.

## LiveSplit process and dependency status

The official LiveSplit 1.8.37 archive is available in the temporary test directory, and Phase 3A previously verified System.Text.Json DTO work inside that host. That earlier result does not verify the v4 production Component.

This Phase 3C attempt launched an isolated copy with the v4 layout, but the process had no observable main window, the configured port did not listen, and an RPC connection was refused. The Component was not proven to load in this process. Consequently, the Phase 3A assembly paths cannot be claimed as the actual load paths for this v4 runtime. No host configuration, installed LiveSplit, registry entry, or production distribution list was changed.

GitHub Actions passed for implementation commit `3a2626df6478291d20be6731818991ac6040a718`: the Windows tests job completed Restore, Release Build, .NET tests, and CLI tests successfully. This CI result does not establish LiveSplit GUI execution or slow-subscriber overflow behavior. The current follow-up commit changes documentation only.

## Remaining Phase 3C work

- Repeat the v4 layout test in an interactive LiveSplit 1.8.37 session and capture the Component load, RPC, Event, and assembly path evidence.
- The repository's full .NET tests and CLI tests passed in GitHub Actions; the local full test project remains unavailable because the LiveSplit source submodule is absent.
- Exercise Event queue overflow/slow-subscriber cleanup and additional Timer Event kinds in the complete Windows test environment.
- Decide the final runtime assembly deployment policy only after the v4 LiveSplit process records the assemblies it actually loads. No shared runtime DLL was added to the distribution allow-list.
- Phase 3D cleanup remains out of scope: remove Protobuf/v3 implementation and dependencies only after the JSON path is accepted and the remaining direct legacy tests are intentionally retired.

Phase 3C is **not complete**: real v4 operation in LiveSplit and slow-subscriber overflow behavior remain unverified. The implementation is an in-progress Draft PR and should not be treated as a release-ready communication migration.
