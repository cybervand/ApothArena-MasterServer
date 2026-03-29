# MasterServerProbe

`MasterServerProbe` is a small command-line tool for checking whether the
community master server is reachable over UDP and for asking it for a lightweight
status response.

It does not launch the game. It is useful for confirming that:

- UDP `14343` is reachable
- the master server process is responding
- the server currently has hosts registered

## Requirements

- .NET 8 SDK if running with `dotnet run`
- or a published binary if you choose to publish it first

## Commands

- `ping`: ask the master server for a simple reachability response
- `status`: ask the master server for uptime, host count, and optionally the
  current host snapshot

## Run From Source

### Windows PowerShell

```powershell
dotnet run --project MasterServerProbe -- ping 127.0.0.1
dotnet run --project MasterServerProbe -- status 127.0.0.1
```

### Linux / bash

```bash
dotnet run --project MasterServerProbe -- ping 127.0.0.1
dotnet run --project MasterServerProbe -- status 127.0.0.1
```

## Run Against A Real Public Master Server

Replace `your.public.master.server.ip` with the public IP or DNS name of your
master server.

### Windows PowerShell

```powershell
dotnet run --project MasterServerProbe -- ping your.public.master.server.ip
dotnet run --project MasterServerProbe -- status your.public.master.server.ip
```

### Linux / bash

```bash
dotnet run --project MasterServerProbe -- ping your.public.master.server.ip
dotnet run --project MasterServerProbe -- status your.public.master.server.ip
```

## Useful Options

- custom port:

### Windows PowerShell

```powershell
dotnet run --project MasterServerProbe -- ping your.public.master.server.ip 14343
```

### Linux / bash

```bash
dotnet run --project MasterServerProbe -- ping your.public.master.server.ip 14343
```

- longer timeout:

### Windows PowerShell

```powershell
dotnet run --project MasterServerProbe -- status your.public.master.server.ip --timeout=5000
```

### Linux / bash

```bash
dotnet run --project MasterServerProbe -- status your.public.master.server.ip --timeout=5000
```

- skip host details:

### Windows PowerShell

```powershell
dotnet run --project MasterServerProbe -- status your.public.master.server.ip --no-hosts
```

### Linux / bash

```bash
dotnet run --project MasterServerProbe -- status your.public.master.server.ip --no-hosts
```

## Example Output

`ping`:

```text
Target: 203.0.113.10:14343
Command: ping
Sent ping nonce=...
Reachable: yes
Round-trip: 62 ms
Server: ApothArena-masterserver
Server UTC: 2026-03-29T11:05:57.1359989Z
Server uptime: 2m 18s
Tracked hosts: 1
```

`status`:

```text
Target: 203.0.113.10:14343
Command: status
Sent status request nonce=... includeHosts=True
Reachable: yes
Round-trip: 67 ms
Server UTC: 2026-03-29T11:06:14.2011440Z
Server uptime: 2m 35s
Tracked hosts: 1
Host details included: True
Host 1: id=12345 ext=198.51.100.25:14242 int=192.168.1.50:14242 age=1s
  info={"IPAddress":"198.51.100.25",...}
```

## Publish A Binary

### Windows PowerShell

```powershell
dotnet publish MasterServerProbe\MasterServerProbe.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o MasterServerProbe\publish
```

Run it:

```powershell
.\MasterServerProbe\publish\MasterServerProbe.exe ping your.public.master.server.ip
```

### Linux / bash

```bash
dotnet publish MasterServerProbe/MasterServerProbe.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o MasterServerProbe/publish-linux
```

Run it:

```bash
chmod +x MasterServerProbe/publish-linux/MasterServerProbe
./MasterServerProbe/publish-linux/MasterServerProbe ping your.public.master.server.ip
```

## When To Use This Tool

Use `MasterServerProbe` when you want to answer:

- is the master server reachable at all?
- is UDP `14343` open and responding?
- does the master server currently think any hosts are registered?

If you want to simulate fake hosts, fake joiners, or bad internal-IP scenarios,
use `MasterServerLab` instead.
