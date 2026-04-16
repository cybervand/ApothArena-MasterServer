# Apotheon Arena — Community Master Server Patcher

The official Apotheon Arena master server is offline. This patcher redirects
the game to a community-run replacement so online multiplayer works again.

---

## Requirements

No extra software needed. The patcher is a standalone `.exe`.

---

## How to install

1. Copy `ApotheonArenaMPpatch.exe` into your **Apotheon Arena game folder**
   (the same folder as `ApotheonArena.exe` and `Lidgren.Network.dll`)

2. Open a **Command Prompt** in that folder and run:
   ```
   ApotheonArenaMPpatch.exe
   ```
   This opens a small menu where you can choose `Patch game`, `Restore`,
   `Enable network debug`, or crash-watch help.
   > To open Command Prompt here: hold Shift, right-click inside the folder,
   > select **"Open PowerShell window here"** or **"Open command window here"**

3. Open the `master_server.txt` file that was created in the game folder
   and replace the IP with the community server address you were given.

4. Launch the game normally.

---

## Changing the server

Edit `master_server.txt` in the game folder at any time.
Put the IP address or hostname on its own line — any length is supported.
Restart the game after saving.

---

## Hosting over the internet

When you host, the master server normally uses the sender address it observed
on your register packet as your "public endpoint". That fails when the master
runs behind the same router as the host, because router hairpin NAT typically
preserves the LAN source — so the master records a private 192.168.x address
and remote players get told to connect to an unroutable IP.

To fix this, put your real WAN endpoint in `public_host_ip.txt` (created in
the game folder on patch). Format:

```
# ip only (port defaults to 14242)
203.0.113.42

# or ip:port
203.0.113.42:14242
```

Leave the file blank to keep the old behavior (master uses observed sender).

The game sends this value with the register/heartbeat packet. A compatible
master server prefers it over the sender address when present.

---

## Uninstalling

To restore the original game files:
```
ApotheonArenaMPpatch.exe restore
```

This restores the game's original patched files from the backups that were
saved when you first ran the patcher, including the server-browser patch,
network debug layer, and any older in-process diagnose patch.
You can safely delete `ApotheonArenaMPpatch.exe` and
`master_server.txt` afterwards.

---

## Troubleshooting

**"Could not find Lidgren.Network.dll"**
Make sure `ApotheonArenaMPpatch.exe` is in the Apotheon Arena game folder, not a subfolder.

**"Already patched"**
You've already run the patcher. Just edit `master_server.txt` to change servers.

**Servers not showing up in the browser**
- Check that `master_server.txt` contains the correct server IP
- Make sure the game is not blocked by your firewall (allow UDP on port 14242)
- The server browser may take a few seconds to populate — wait a moment

**Where are the logs?**
- `Logs\\network_debug.log` for the patcher's networking trace
- `Logs\\diagnose_monitor.log` or `DiagnoseTrace` console output for external crash watching
- the game's own built-in `Crash_*.log` files may still appear in its separate user-data `Logs` folder

**How do I debug crashes safely now?**
- In-process `diagnose` patching is disabled because it was destabilizing the game.
- Use the external watcher instead:
  `.\DiagnoseTrace\bin\Debug\net8.0\DiagnoseTrace.exe --launch --procdump`

---

## For mod developers — building the patcher

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download).

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```

The distributable `ApotheonArenaMPpatch.exe` will be in the `publish/` folder.
