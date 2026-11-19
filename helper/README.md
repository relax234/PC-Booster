# PC Booster Helper (early preview)

Windows 10/11, .NET 8.

## Build
On Windows with .NET 8 SDK:
```powershell
dotnet publish .\PCBoosterHelper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Run the executable. It binds only to `127.0.0.1:47831`, prints a random session token, and exposes one read-only endpoint: `GET /diagnostics`.

The endpoint accepts requests only from the official PC Booster site origin and requires the token in `X-PC-Booster-Token`. The token is regenerated each run and is not written to disk. The helper currently reports basic CPU, RAM, OS and system-drive information. It does not modify Windows, start elevated, install a service, or execute commands.

This is an early integration milestone, not yet the finished installer/tray application. Do not expose the listener beyond loopback.
