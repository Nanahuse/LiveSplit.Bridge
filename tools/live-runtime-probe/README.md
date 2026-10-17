# LiveSplit in-process runtime probe

This component loads the Phase 2 JSON DTOs and serializer settings inside a LiveSplit process, then writes `LiveSplit.JsonRuntimeProbe.log` beside `LiveSplit.exe`. It does not start a WebSocket server or change Bridge behavior.

Build against a portable LiveSplit 1.8.37 directory:

```powershell
dotnet restore tools/live-runtime-probe/LiveSplit.JsonRuntimeProbe.csproj -p:LiveSplitInstallPath='C:\path\to\LiveSplit_1.8.37'
dotnet build tools/live-runtime-probe/LiveSplit.JsonRuntimeProbe.csproj --configuration Release --no-restore -p:LiveSplitInstallPath='C:\path\to\LiveSplit_1.8.37'
```

Copy `LiveSplit.JsonRuntimeProbe.dll` and the runtime DLLs from `bin/Release/net481/` beside the component in a disposable LiveSplit installation. In a test layout, select **LiveSplit JSON Runtime Probe**. The log records JSON checks, framework and process information, loaded assembly versions and paths, and a five-second update-loop heartbeat.

## Current machine result

The component builds for `net481`. The available portable install is `C:\Users\7huse\Downloads\LiveSplit_1.8.37`; the machine reports .NET Framework `4.8.09221` (Release key `533509`). Its executable reports ProductVersion `1.0.0+683cd60a9f3fe2dc31a5054c6d10f5d71f2e6ae1`.

The disposable copy reached a top-level window titled `Error` before the component produced a log. The same happened when starting the copy without the probe layout. The specific startup error could not be read in this session, so no in-process JSON check, loaded-assembly path, dependency-isolation result, or normal-operation heartbeat is claimed. Repeat the procedure on a LiveSplit installation that completes startup and inspect the generated log before using this probe as an acceptance result.
