# JSON v4 integration TestHost

Build with .NET 10 SDK on Windows, after initializing `external/LiveSplit` and its
`lib/SpeedrunComSharp` submodule:

```powershell
dotnet build tests/LiveSplit.Bridge.TestHost/LiveSplit.Bridge.TestHost.csproj --configuration Release
tests/LiveSplit.Bridge.TestHost/bin/Release/net4.8.1/LiveSplit.Bridge.TestHost.exe --port 54000
```

The host prints `READY` after probing the real JSON v4 RPC endpoint. Write a newline
to stdin to stop it. The default Run contains two Segments (`First`, `Second`) with
no game or Segment icons.

Pass `--with-icons` to enable deterministic image fixtures:

| Image | Fixture |
| --- | --- |
| Game | 256 by 256 opaque noise pixels |
| First Segment | 16 by 16 opaque noise pixels, a distinct seed |
| Second Segment | No icon (`null`) |

The pixels use fixed xorshift32 seeds; PNG encoding is performed by the real Bridge
adapter. The game image is intentionally poorly compressible so the Base64 JSON
`get_run_icons` response exceeds 64 KiB. Consumers should assert the actual UTF-8
response size, decoded PNG bytes, Segment positions, and continued RPC use.
No external files or services are required for image generation.
