# ApothArena-masterserver

The official Apotheon Arena master server is offline. This project revives online
multiplayer with a community-run replacement master server and a client patcher to
redirect the game to it.

---

## For players — patching the game

### Requirements

No extra software needed. The patcher is a standalone `.exe` (Windows) or binary (Linux).

### How to install

1. Download `ApotheonArenaMPpatch.exe` (Windows) or `ApotheonArenaMPpatch` (Linux)
   from the [Releases](../../releases) page.

2. Copy it into your **Apotheon Arena game folder**
   (the same folder as `ApotheonArena.exe` and `Lidgren.Network.dll`).

3. Run it:

   **Windows** — open a Command Prompt in the game folder and run:
   ```
   ApotheonArenaMPpatch.exe
   ```
   > Hold Shift, right-click inside the folder, select "Open PowerShell window here",
   > then type the command above.

   **Linux** — open a terminal in the game folder and run:
   ```
   chmod +x ApotheonArenaMPpatch
   ./ApotheonArenaMPpatch
   ```

4. Open `master_server.txt` in the game folder and replace the IP with the
   community server address.

5. Launch the game normally and go online.

### Changing the server

Edit `master_server.txt` in the game folder at any time. Put the IP address or
hostname on its own line — lines starting with `#` are treated as comments.
Restart the game after saving.

### Uninstalling

```
ApotheonArenaMPpatch.exe restore
```

This restores the original `Lidgren.Network.dll` from the backup made when you
first ran the patcher. You can then delete `ApotheonArenaMPpatch.exe` and
`master_server.txt`.

### Troubleshooting

**"Could not find Lidgren.Network.dll"**
Make sure the patcher is in the Apotheon Arena game folder, not a subfolder.

**"Already patched"**
You have already run the patcher. Just edit `master_server.txt` to change servers.
Run `restore` first if you need to re-patch.

**Servers not showing up in the browser**
- Check that `master_server.txt` contains the correct server IP
- Make sure the game is not blocked by your firewall (allow UDP on port 14242)
- The server browser may take a few seconds to populate — wait a moment

---

## For server operators — running the master server

### Requirements

- Linux host with Docker and Docker Compose (Unraid, Ubuntu, Debian, etc.)
- UDP port **14343** open and reachable from the internet

### Deploying

```bash
git clone https://github.com/your-username/ApothArena-masterserver
cd ApothArena-masterserver/MasterServer
docker compose up -d --build
```

The server listens on UDP port 14343 and requires `network_mode: host` (already
set in `docker-compose.yml`) so it can see real client IPs for NAT hole punching.
Do not run it behind Docker bridge networking — clients will fail to connect to
each other.

### Updating

```bash
git pull
docker compose up -d --build
```

### Router / firewall

Forward **UDP 14343** to the machine running the master server. No other ports
need forwarding — players connect to each other via UDP hole punching without
port forwarding on their end.

### Logs

```bash
docker logs apotharena-masterserver -f
```

### Diagnostic probe

You can test whether the master server is reachable without launching the game by
using the `MasterServerProbe` tool.

Build and run:

```bash
dotnet run --project MasterServerProbe -- ping your-server.example.com
dotnet run --project MasterServerProbe -- status your-server.example.com
```

What it does:

- `ping` checks that UDP `14343` is reachable and reports round-trip time
- `status` returns uptime, tracked host count, and the current registered hosts

Useful options:

- Pass a custom port after the host: `status your-server.example.com 14343`
- Increase timeout: `--timeout=5000`
- Skip host details: `--no-hosts`

### Protocol lab

You can also simulate a lightweight host, browser, or joiner without launching
the full game by using `MasterServerLab`.

Examples:

```bash
dotnet run --project MasterServerLab -- host your-server.example.com --duration=30 --host-id=12345 --name="Lab Host"
dotnet run --project MasterServerLab -- browse your-server.example.com --timeout=3000
dotnet run --project MasterServerLab -- join your-server.example.com --host-id=12345 --timeout=5000
```

Useful options:

- `--local-ip=<ip>` to bind the simulated client to a specific local address
- `--report-ip=<ip>` and `--report-port=<port>` to deliberately lie about the
  internal endpoint and reproduce bad-adapter scenarios
- `--heartbeat-ms=<ms>` to change host heartbeat frequency
- `--token=<value>` to force a known NAT-introduction token during tests
- `--scenario=<preset>` to quickly replay common endpoint cases

List built-in presets:

```bash
dotnet run --project MasterServerLab -- scenarios
```

Preset examples:

```bash
dotnet run --project MasterServerLab -- host your-server.example.com --scenario=bad-link-local --host-id=12345
dotnet run --project MasterServerLab -- host your-server.example.com --scenario=tailscale --host-id=12345
dotnet run --project MasterServerLab -- join your-server.example.com --scenario=private-lan --host-id=12345
```

---

## Building from source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download).

**Patcher (Windows x64):**
```
cd Patcher
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```

**Patcher (Linux x64):**
```
cd Patcher
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ./publish-linux
```

**Master server:**
```
cd MasterServer
docker compose up --build
```

---

## How it works

Apotheon Arena uses [Lidgren.Network](https://github.com/lidgren/lidgren-network-gen3)
for multiplayer. The game has a hardcoded master server IP baked into
`Lidgren.Network.dll`. Since that server is gone, the patcher injects new IL code
into the DLL that reads the master server address from `master_server.txt` instead,
supporting any IP or hostname of any length.

The replacement master server handles host registration, server list requests, and
NAT introduction so players can connect directly to each other without port
forwarding.
