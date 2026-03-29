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
   `Enable network debug`, or `Disable network debug`.
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

## Uninstalling

To restore the original game files:
```
ApotheonArenaMPpatch.exe restore
```

This restores `Lidgren.Network.dll` from the backup that was saved when
you first ran the patcher, and also restores the server-browser empty-list patch.
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

---

## For mod developers — building the patcher

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download).

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish
```

The distributable `ApotheonArenaMPpatch.exe` will be in the `publish/` folder.
