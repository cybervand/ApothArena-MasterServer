# MasterServerLab

`MasterServerLab` is a lightweight protocol sandbox for the Apotheon Arena
master-server flow. It lets you simulate parts of the game's network behavior
without launching the real game.

It can act as:

- `host`: sends register/heartbeat packets to the master server
- `browse`: requests the server list
- `join`: sends a NAT-introduction request for a host
- `scenarios`: lists built-in test presets

This is the cheap and fast test harness for iterating on master-server behavior.

## Requirements

- .NET 8 SDK if running with `dotnet run`
- or a published binary if you prefer

## Quick Start

### List available scenarios

#### Windows PowerShell

```powershell
dotnet run --project MasterServerLab -- scenarios
```

#### Linux / bash

```bash
dotnet run --project MasterServerLab -- scenarios
```

## Run From Source

### Start a fake host

#### Windows PowerShell

```powershell
dotnet run --project MasterServerLab -- host your.public.master.server.ip --host-id=12345 --duration=30 --name="Lab Host"
```

#### Linux / bash

```bash
dotnet run --project MasterServerLab -- host your.public.master.server.ip --host-id=12345 --duration=30 --name="Lab Host"
```

### Request the server list

#### Windows PowerShell

```powershell
dotnet run --project MasterServerLab -- browse your.public.master.server.ip --timeout=3000
```

#### Linux / bash

```bash
dotnet run --project MasterServerLab -- browse your.public.master.server.ip --timeout=3000
```

### Request NAT introduction for a host

#### Windows PowerShell

```powershell
dotnet run --project MasterServerLab -- join your.public.master.server.ip --host-id=12345 --timeout=5000
```

#### Linux / bash

```bash
dotnet run --project MasterServerLab -- join your.public.master.server.ip --host-id=12345 --timeout=5000
```

## Real-World Usage

To test with real endpoints instead of `127.0.0.1`:

- run `host` on the machine/network acting as the host player
- run `browse` on the machine/network acting as the browsing player
- run `join` on the machine/network acting as the joining player
- point all of them at the real public IP or DNS name of your master server

Example:

### Host PC

#### Windows PowerShell

```powershell
dotnet run --project MasterServerLab -- host your.public.master.server.ip --host-id=12345 --scenario=good-default
```

#### Linux / bash

```bash
dotnet run --project MasterServerLab -- host your.public.master.server.ip --host-id=12345 --scenario=good-default
```

### Remote browser PC

#### Windows PowerShell

```powershell
dotnet run --project MasterServerLab -- browse your.public.master.server.ip
```

#### Linux / bash

```bash
dotnet run --project MasterServerLab -- browse your.public.master.server.ip
```

### Remote joiner PC

#### Windows PowerShell

```powershell
dotnet run --project MasterServerLab -- join your.public.master.server.ip --host-id=12345
```

#### Linux / bash

```bash
dotnet run --project MasterServerLab -- join your.public.master.server.ip --host-id=12345
```

## Built-In Scenarios

List them with:

### Windows PowerShell

```powershell
dotnet run --project MasterServerLab -- scenarios
```

### Linux / bash

```bash
dotnet run --project MasterServerLab -- scenarios
```

Current presets:

- `good-default`
- `bad-link-local`
- `tailscale`
- `private-lan`

### Simulate a link-local / wrong-adapter host

#### Windows PowerShell

```powershell
dotnet run --project MasterServerLab -- host your.public.master.server.ip --scenario=bad-link-local --host-id=12345
```

#### Linux / bash

```bash
dotnet run --project MasterServerLab -- host your.public.master.server.ip --scenario=bad-link-local --host-id=12345
```

### Simulate a Tailscale-style internal IP

#### Windows PowerShell

```powershell
dotnet run --project MasterServerLab -- host your.public.master.server.ip --scenario=tailscale --host-id=12345
```

#### Linux / bash

```bash
dotnet run --project MasterServerLab -- host your.public.master.server.ip --scenario=tailscale --host-id=12345
```

### Simulate a normal private-LAN report

#### Windows PowerShell

```powershell
dotnet run --project MasterServerLab -- host your.public.master.server.ip --scenario=private-lan --host-id=12345
```

#### Linux / bash

```bash
dotnet run --project MasterServerLab -- host your.public.master.server.ip --scenario=private-lan --host-id=12345
```

## Useful Options

- `--master-port=14343`
- `--game-port=14242`
- `--timeout=3000`
- `--duration=30`
- `--heartbeat-ms=1000`
- `--host-id=<long>`
- `--name=<server name>`
- `--map=<map id>`
- `--players=<n>`
- `--max-players=<n>`
- `--local-ip=<ip>`
- `--report-ip=<ip>`
- `--report-port=<port>`
- `--token=<value>`
- `--scenario=<preset>`

## What The Important Options Mean

- `--local-ip=<ip>`:
  force the local interface/address used by the simulated client

- `--report-ip=<ip>` and `--report-port=<port>`:
  deliberately lie about the internal endpoint the fake client reports to the
  master server

- `--host-id=<id>`:
  the unique host identifier used by register/list/join flows

- `--token=<value>`:
  force a known NAT-introduction token so you can correlate it in logs

## Example: Deliberately Reproduce A Bad Registration

This is useful for checking how the master server behaves when the client claims
to have a broken adapter/IP.

### Windows PowerShell

```powershell
dotnet run --project MasterServerLab -- host your.public.master.server.ip --host-id=12345 --report-ip=169.254.83.107 --report-port=14242
```

### Linux / bash

```bash
dotnet run --project MasterServerLab -- host your.public.master.server.ip --host-id=12345 --report-ip=169.254.83.107 --report-port=14242
```

## Publish A Binary

### Windows PowerShell

```powershell
dotnet publish MasterServerLab\MasterServerLab.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o MasterServerLab\publish
```

Run it:

```powershell
.\MasterServerLab\publish\MasterServerLab.exe scenarios
.\MasterServerLab\publish\MasterServerLab.exe host your.public.master.server.ip --host-id=12345
```

### Linux / bash

```bash
dotnet publish MasterServerLab/MasterServerLab.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o MasterServerLab/publish-linux
```

Run it:

```bash
chmod +x MasterServerLab/publish-linux/MasterServerLab
./MasterServerLab/publish-linux/MasterServerLab scenarios
./MasterServerLab/publish-linux/MasterServerLab host your.public.master.server.ip --host-id=12345
```

## When To Use This Tool

Use `MasterServerLab` when you want to answer:

- what does the master server do when a host reports a bad internal IP?
- how does the server list get rewritten for a specific client?
- what happens when a joiner asks for NAT intro?
- can we reproduce a bug without launching the real game?

If you only want to check whether the master server is reachable and alive, use
`MasterServerProbe` instead.
