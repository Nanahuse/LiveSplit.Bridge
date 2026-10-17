# LiveSplit in-process runtime probe

The component runs the Phase 2 DTOs and shared serializer settings inside LiveSplit. It does not start a WebSocket server or change Bridge behavior. Runtime logs are written to `%TEMP%\LiveSplit.JsonRuntimeProbe-{PID}-{UTC timestamp}.log`, for example `%TEMP%\LiveSplit.JsonRuntimeProbe-1234-20261017T012345678Z.log`. If file output fails, the component records the exception through `Debug.WriteLine` and treats the run as unverified.

## Build

Build against an official portable LiveSplit 1.8.37 extraction:

```powershell
dotnet restore tools/live-runtime-probe/LiveSplit.JsonRuntimeProbe.csproj -p:LiveSplitInstallPath='C:\path\to\LiveSplit_1.8.37'
dotnet build tools/live-runtime-probe/LiveSplit.JsonRuntimeProbe.csproj --configuration Release --no-restore -p:LiveSplitInstallPath='C:\path\to\LiveSplit_1.8.37'
```

## Staged run

Use a fresh extraction of the official [LiveSplit 1.8.37 release](https://github.com/LiveSplit/LiveSplit/releases/tag/1.8.37) for Stage A. Keep the ZIP hash and record the executable path, ProductVersion, .NET Framework version, and startup result. Do not add the probe, its dependencies, a test layout, or other components. Launch `LiveSplit.exe`, confirm it remains responsive, then close it. This release calls its file-type registration helper during startup; if Windows reports that launching the helper was canceled, resolve that consent in a disposable test environment before treating the launch as a clean-start failure or moving on.

For Stage B, make another clean extraction from the same release ZIP. Copy **only** `LiveSplit.JsonRuntimeProbe.dll` from this project's `bin/Release/net481/` to the copied install's `Components` folder. Leave all host DLLs and `LiveSplit.exe.config` untouched. Use a test layout containing only this component and launch it with:

```powershell
.\LiveSplit.exe -l .\LiveSplit.JsonRuntimeProbe.lsl
```

The layout needs a normal `<Layout version="1.6.1">` root, a `<Components>` element, and this component entry:

```xml
<Component>
  <Path>LiveSplit.JsonRuntimeProbe.dll</Path>
  <Settings><Version>1</Version></Settings>
</Component>
```

Check the PID-specific `%TEMP%` log. Required stage markers are `FACTORY_CREATE`, `COMPONENT_CONSTRUCTOR`, `JSON_OPTIONS_START`, `JSON_OPTIONS_SUCCESS`, `JSON_CHECKS_START`, `JSON_CHECKS_COMPLETE`, and `UPDATE_HEARTBEAT`. All individual JSON checks must say `PASS`; the log also lists each loaded assembly's identity and file path. A missing log is a failed/incomplete run, not a pass.

## Stage C: only when Stage B reports a missing dependency

Use the assembly load error or startup exception to name the missing DLL. Add only that DLL from the probe's Release output to the disposable install's `Components` folder, without replacing a host copy, then repeat Stage B. Do not add redirects, modify host config, or add custom assembly resolution during this diagnosis.

If startup fails before the component factory runs, inspect the Application event log and LiveSplit error text. For example:

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-15) } |
  Where-Object { $_.ProviderName -in @('LiveSplit', '.NET Runtime', 'Application Error') } |
  Select-Object TimeCreated, ProviderName, Id, Message | Format-List
```

LiveSplit 1.8.37 logs unhandled startup exceptions through `Options.Log.Error()` before showing its crash dialog. Record the exception type, message, stack, process path/version, and the test stage. Don't attribute a pre-factory startup exception to System.Text.Json without evidence. The current machine produced a `LiveSplit` Application event stating that `Process.StartWithShellExecuteEx` was canceled inside `FiletypeRegistryHelper.RegisterFileFormatsIfNotAlreadyRegistered()`; get the required approval for the registration helper before repeating Stage B.
