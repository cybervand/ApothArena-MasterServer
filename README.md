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

- Windows or Linux machine with .NET 8, or a Linux host with Docker and Docker Compose (Unraid, Ubuntu, Debian, etc.)
- UDP port **14343** open and reachable from the internet

### Deploying

```bash
git clone https://github.com/your-username/ApothArena-masterserver
cd ApothArena-masterserver/MasterServer
cp .env.example .env
docker compose up -d --build
```

The server listens on UDP port 14343 and requires `network_mode: host` (already
set in `docker-compose.yml`) so it can see real client IPs for NAT hole punching.
Do not run it behind Docker bridge networking — clients will fail to connect to
each other.

Docker Compose with `network_mode: host` is mainly a Linux and Unraid deployment
path. The master server application itself runs on both Windows and Linux, so if
you are hosting on Windows it is usually simpler to run the app directly with
`.NET 8` instead of Docker.

### Running directly on Windows or Linux

From the repo root:

```bash
dotnet run --project MasterServer/MasterServer.csproj
```

Or publish a standalone build:

```bash
dotnet publish MasterServer/MasterServer.csproj -c Release -o MasterServer/publish
```

Then run the published app:

**Windows**
```powershell
.\MasterServer\publish\MasterServer.exe
```

**Linux**
```bash
./MasterServer/publish/MasterServer
```

When the server starts for the first time, it creates a real `.env` file next to
the app if one does not exist already. Relative `DATA_DIRECTORY` values such as
`data` are resolved inside the app folder on both Windows and Linux.

### Configuration

The master server reads a small set of environment variables at startup. Copy
[`MasterServer/.env.example`](MasterServer/.env.example) to `.env` and adjust
values there before launching with Docker Compose.

- `MASTER_SERVER_PORT`: UDP port for the master server. Keep `14343` unless all
  game clients are patched to use a different master-server port.
- `HOST_TIMEOUT_SECONDS`: how long a host can miss heartbeats before it is
  removed from the active list.
- `LOG_MODE`: `player` for friendly startup and activity logs that read like a
  server dashboard. Use `debug` when troubleshooting networking, because it
  keeps the player-friendly logs and also adds packet-level Lidgren traces.
- `DATA_DIRECTORY`: path shown in the startup banner. Relative values such as
  `data` work on both Windows and Linux.
- `SERVER_DISPLAY_NAME`: name shown in the startup, shutdown, and error banners.

At startup the server prints the effective configuration and then switches to
player-friendly activity logs such as:

- `Server came online`
- `Server list checked`
- `Player is trying to join a server`
- `Server went offline`

### Updating

```bash
git pull
docker compose up -d --build
```

### Router / firewall

Forward **UDP 14343** to the machine running the master server. No other ports
need forwarding — players connect to each other via UDP hole punching without
port forwarding on their end.

### Unraid

A ready-made Docker template lives at
[`MasterServer/unraid-template.xml`](MasterServer/unraid-template.xml). It
points at the pre-built image on GitHub Container Registry
(`ghcr.io/cybervand/apotharena-masterserver:latest`), so Unraid's
"Check for Updates" will work out of the box.

Install:

1. Copy the template to the Unraid box:
   ```
   cp MasterServer/unraid-template.xml \
     /boot/config/plugins/dockerMan/templates-user/my-ApothArena-MasterServer.xml
   ```
2. In the Unraid Docker tab, click **Add Container** → pick
   `apotharena-masterserver` from the template dropdown → Apply. Unraid pulls
   the image from GHCR and starts it.

Updates: click **Check for Updates** in the Docker tab. When a newer digest is
available (pushed by the GitHub Actions workflow on every commit to `main`),
hit **Apply Update**.

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
