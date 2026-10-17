# LiveSplit in-process runtime probe

This diagnostic component reuses the Phase 2 DTOs and serializer options inside LiveSplit 1.8.37. It has the assembly-level `ComponentFactoryAttribute` required by LiveSplit's component discovery. It does not change Bridge communication.

The checked-in `JsonRuntimeProbe.lsl` preserves the full `Settings` and layout-level structure from a LiveSplit-saved layout, with its `Components` section reduced to the probe alone. It contains no machine-specific path.

## Build

Extract the official [LiveSplit 1.8.37 release](https://github.com/LiveSplit/LiveSplit/releases/tag/1.8.37), then build against that directory:

```powershell
dotnet restore tools/live-runtime-probe/LiveSplit.JsonRuntimeProbe.csproj -p:LiveSplitInstallPath='C:\path\to\LiveSplit_1.8.37'
dotnet build tools/live-runtime-probe/LiveSplit.JsonRuntimeProbe.csproj --configuration Release --no-restore -p:LiveSplitInstallPath='C:\path\to\LiveSplit_1.8.37'
```

## Stage A and Stage B

Use two separate fresh extractions of the same official ZIP. Keep the archive's SHA-256, executable path/version, and test time with the results.

**Stage A:** add no probe, custom layout, or dependency. Run `LiveSplit.exe` normally, verify the `LiveSplit` window is responsive, then close it.

**Stage B:** in the second extraction, copy only `LiveSplit.JsonRuntimeProbe.dll` from `tools/live-runtime-probe/bin/Release/net481/` into `Components`. Copy this `JsonRuntimeProbe.lsl` beside `LiveSplit.exe`. Do not copy dependency DLLs or change `LiveSplit.exe.config`. Launch from that directory:

```powershell
.\LiveSplit.exe -l .\JsonRuntimeProbe.lsl
```

Administrator rights and file association or registry changes are not required for this check. The host may log a canceled file-type registration helper launch; that alone is not evidence that LiveSplit exited or that this probe failed.

## Success evidence and diagnosis

Logs are written under `Path.GetTempPath()` as `LiveSplit.JsonRuntimeProbe-{PID}-{UTC timestamp}.log`. On the verification host that resolved to `C:\Users\7huse\AppData\Local\Packages\sandbox.{b1b6419b-3913-4bcd-a018-500124793b83}\AC\Temp`; the component records the exact path in each log. Check the same PID as the launched LiveSplit process. The directory varies by host.

A successful run contains `FACTORY_CREATE`, `COMPONENT_CONSTRUCTOR`, `JSON_OPTIONS_START`, `JSON_OPTIONS_SUCCESS`, `JSON_CHECKS_START`, all individual `PASS` entries, `JSON_CHECKS_COMPLETE`, loaded assembly identities and paths, and `UPDATE_HEARTBEAT`. A missing log or any missing stage/PASS is incomplete.

If no log appears, check in order: that the explicit layout loaded, its single Component `Path` is correct, the built DLL contains `ComponentFactoryAttribute` referencing `Factory`, and LiveSplit's component/assembly load error. Query the Application event log where permissions allow. Do not infer the cause from log absence or the file-type registration cancellation alone. Only add a dependency DLL if a concrete load failure identifies it, and do not replace a host DLL during this probe.

For component source/build details see `RuntimeProbeComponent.cs` and `LiveSplit.JsonRuntimeProbe.csproj`; observed host results and limitations are in `phase3a-findings.md`.
