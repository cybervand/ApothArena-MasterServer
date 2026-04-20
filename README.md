# ApothArena-MasterServer

The original Apotheon Arena master server is offline. This repo provides:

- a replacement community master server
- a Harmony-based game patcher that redirects the game to that server
- the bundled `NetworkMod` payload used by the patcher

## For Players

### What the patcher does

The current patcher is no longer just a tiny direct binary edit.

It now:

- injects a lightweight Harmony bootstrap into the game executable
- installs the required payload into `mods/`
- writes network settings to `mods/networkmod.config`
- supports both `Apotheon Arena` and `Apotheon (SP)`
- can `patch`, `restore`, or `repair`

### Current release assets

The current Windows release asset is:

- `ApotheonArenaMPpatch-harmony.exe`

Note:

- the Linux patcher build has not been compiled and uploaded yet

### Supported game executables

The patcher can target:

- `ApotheonArena.exe`
- `Apotheon.exe`

If both are present in the same folder, the patcher can prompt you to choose.

### Installation

1. Download `ApotheonArenaMPpatch-harmony.exe` from the [Releases](../../releases) page.
2. Copy it into the game folder next to the game executable.
3. Run it.
4. Choose `Patch game`.
5. Edit `mods/networkmod.config` if you want to change the master server or networking overrides.

The patcher will:

- inject the Harmony bootstrap
- create `mods/`
- extract:
  - `0Harmony.dll`
  - `ApotheonArena.NetworkMod.dll`
- create `mods/networkmod.config` if it does not already exist
- migrate legacy sidecar files if they are present

### Config file

After patching, the main config is:

- `mods/networkmod.config`

Important fields:

- `masterServer`
- `hostIp`
- `clientIp`
- `publicHostIp`
- `showConsole`

`showConsole=true` opens a separate console window mirroring the mod log output.

### Commands

Interactive menu:

- `Patch game`
- `Restore original files`
- `Repair (re-extract mods/payload)`

CLI examples:

```powershell
ApotheonArenaMPpatch-harmony.exe patch
ApotheonArenaMPpatch-harmony.exe restore
ApotheonArenaMPpatch-harmony.exe repair
```

Advanced commands:

```powershell
ApotheonArenaMPpatch-harmony.exe diagnose
ApotheonArenaMPpatch-harmony.exe undiagnose
```

### Restore

`restore` removes the Harmony payload and restores the original executable behavior.

It also cleans up:

- `mods/0Harmony.dll`
- `mods/ApotheonArena.NetworkMod.dll`
- `mods/networkmod.config`
- legacy root-level payload files if they exist
- the added probing path in the game `.config`

### Repair

`repair` is useful if:

- the mod files were deleted
- the payload needs to be refreshed
- you want to keep the patched executable but restore the bundled payload

It re-extracts the payload and refreshes probing/config setup without doing a full restore/repatch cycle.

## Included NetworkMod Behavior

The bundled `NetworkMod` currently handles:

- master-server redirect
- host/client LAN IP override support
- optional public host endpoint override
- auto-detection and persistence of local IP choices
- server browser hooks
- crash logging through the mod logger
- optional mirrored console output

## For Server Operators

### What the master server does

The replacement master server handles:

- host registration
- host heartbeats
- server list requests
- NAT introduction for join attempts
- basic diagnostic ping/status packets

### Requirements

- Windows or Linux with .NET 8
- or Linux/Unraid with Docker
- UDP port `14343` open to the internet

### Docker deployment

```bash
git clone https://github.com/cybervand/ApothArena-MasterServer
cd ApothArena-MasterServer/MasterServer
cp .env.example .env
docker compose up -d --build
```

The Compose setup uses `network_mode: host` because the master server needs to see real client endpoints for NAT introduction behavior.

### Direct run

```bash
dotnet run --project MasterServer/MasterServer.csproj
```

Or publish:

```bash
dotnet publish MasterServer/MasterServer.csproj -c Release -o MasterServer/publish
```

### Master server config

Copy [`MasterServer/.env.example`](MasterServer/.env.example) to `.env` and adjust as needed.

Main settings:

- `MASTER_SERVER_PORT`
- `HOST_TIMEOUT_SECONDS`
- `LOG_MODE`
- `DATA_DIRECTORY`
- `SERVER_DISPLAY_NAME`

`LOG_MODE` values:

- `player` = friendly operator-facing logs
- `debug` = player logs plus packet-level/network tracing

### Unraid

An Unraid template is included at:

- [`MasterServer/unraid-template.xml`](MasterServer/unraid-template.xml)

It points to:

- `ghcr.io/cybervand/apotharena-masterserver:latest`

## Tools

### MasterServerProbe

Quick UDP reachability and status checks:

```bash
dotnet run --project MasterServerProbe -- ping your-server.example.com
dotnet run --project MasterServerProbe -- status your-server.example.com
```

### MasterServerLab

Simulates host, browser, and join traffic without launching the game:

```bash
dotnet run --project MasterServerLab -- host your-server.example.com --duration=30 --host-id=12345 --name="Lab Host"
dotnet run --project MasterServerLab -- browse your-server.example.com --timeout=3000
dotnet run --project MasterServerLab -- join your-server.example.com --host-id=12345 --timeout=5000
```

## Building From Source

Requires .NET 8 SDK.

Windows patcher:

```bash
dotnet publish Patcher/Patcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o Patcher/publish
```

Linux patcher:

```bash
dotnet publish Patcher/Patcher.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o Patcher/publish-linux
```

Master server image:

```bash
docker build -f MasterServer/Dockerfile .
```

## How It Works

Older versions of this project focused on directly patching Lidgren or swapping simple text files.

The current patcher instead:

1. injects a bootstrap call into the game executable
2. loads the bundled Harmony-based `NetworkMod`
3. reads settings from `mods/networkmod.config`
4. redirects the hardcoded master server through managed patch logic

That makes the patcher easier to maintain and gives the project a cleaner place to add future networking fixes.
