# Reverse Engineering Map

This note captures what we learned from the externally supplied `ApotheonMod`
plugin and the `Apotheon-SP` game install. The goal is to separate confirmed
facts from remaining unknowns, especially around the question of whether the
master server must keep using Lidgren.

## Artifacts inspected

- Zip provided by the user:
  `d:\User\Default\Downloads\ApotheonMod (1).zip`
- Extracted files from that zip:
  - `ApotheonMod.dll`
  - `settings.cfg`
- Installed modded game folder:
  [Apotheon-SP/Apotheon](/m:/develop/Apotheon%20Arena/Apotheon-SP/Apotheon)
- Installed plugin files:
  - [Apotheon-SP/Apotheon/BepInEx/plugins/ApotheonMod.dll](/m:/develop/Apotheon%20Arena/Apotheon-SP/Apotheon/BepInEx/plugins/ApotheonMod.dll)
  - [Apotheon-SP/Apotheon/BepInEx/plugins/settings.cfg](/m:/develop/Apotheon%20Arena/Apotheon-SP/Apotheon/BepInEx/plugins/settings.cfg)
- Runtime logs:
  - [Apotheon-SP/Apotheon/network_debug.log](/m:/develop/Apotheon%20Arena/Apotheon-SP/Apotheon/network_debug.log)
  - [Apotheon-SP/Apotheon/BepInEx/LogOutput.log](/m:/develop/Apotheon%20Arena/Apotheon-SP/Apotheon/BepInEx/LogOutput.log)

## Confirmed plugin behavior

By reading the plugin assembly with Cecil, the mod is confirmed to patch these
game and Lidgren methods:

- `Apotheon.Play.ApotheonGame.NetworkUpdate`
- `Lidgren.Network.NetUtility.Resolve(string)`
- `Lidgren.Network.NetUtility.GetNetworkInterface()`

The patch flow is:

1. `Load()` patches `ApotheonGame.NetworkUpdate`.
2. On the first `NetworkUpdate`, `PrefixNetworkUpdate()` loads settings and then
   applies the Lidgren patches.
3. `PrefixResolve()` intercepts Lidgren name resolution for the original master
   server.
4. `PostfixGetNetworkInterface()` overrides Lidgren's chosen network interface.

## What `PrefixResolve()` proves

`PrefixResolve()` only changes the destination when the game tries to resolve
the original hardcoded master server IP:

- original master server: `50.19.227.23`
- replacement value: first uncommented line in `settings.cfg`

This means the plugin is not rewriting arbitrary networking. It is narrowly
targeted at master-server resolution.

The installed plugin log confirms the config-driven override:

- [BepInEx/LogOutput.log](/m:/develop/Apotheon%20Arena/Apotheon-SP/Apotheon/BepInEx/LogOutput.log)
  contains `Using master server from config: ...`

The installed `settings.cfg` contains a single replacement host/IP, not a full
protocol configuration.

## What `network_debug.log` proves

[network_debug.log](/m:/develop/Apotheon%20Arena/Apotheon-SP/Apotheon/network_debug.log)
shows repeated entries like:

- `[master] <configured-host>`
- `[resolve] <configured-host>:14343`
- `[send-unconnected] -> <configured-host>:14343`

That confirms three important things:

1. Client traffic to the master server is sent to UDP port `14343`.
2. The client is using unconnected Lidgren traffic for master-server contact.
3. The master-server path is separate from gameplay traffic.

This lines up with the current replacement server in
[MasterServer/Program.cs](/m:/develop/Apotheon%20Arena/MasterServer/Program.cs).

## What the network-interface patch proves

The plugin does more than redirect the master-server IP. It also overrides how
Lidgren chooses the local network interface.

`NetworkInterfaceProvider.GetNetworkInterface()`:

- filters out loopback and unknown interfaces
- requires IPv4 support
- prefers interfaces that are operationally up
- strongly prefers the interface whose unicast address matches the detected
  default-route address
- otherwise prefers any interface with an IPv4 address

`ProbeDefaultRouteAddress()` opens a UDP socket to a hardcoded IPv4 endpoint on
port `12345` and then reads the socket's local endpoint. This is a standard
trick for asking Windows which local address it would actually use to reach the
outside world.

Implication:

- your friend's reverse engineering already covered one of the biggest practical
  issues we have seen in this project: Lidgren choosing the wrong local adapter
  or a bad LAN IP

## What this mod does not prove

Even though the plugin is clearly useful, it is still a client-side Harmony mod.
It does not by itself give us a complete standalone master-server protocol
specification.

From the inspected artifacts alone, we do **not** yet have:

- a full byte-level description of every master-server packet
- the exact Lidgren wire encoding rules for all field types in this game build
- the exact byte layout of Lidgren NAT-introduction packets expected by the
  game host and client
- proof that a plain UDP server can replace `NetPeer.Introduce(...)` without
  reproducing Lidgren's introduction format exactly

## What this means for removing Lidgren from the master server

### What now looks easy to replace

These parts appear likely to be straightforward to move off Lidgren, provided we
match the current packet encoding:

- register / heartbeat receive
- quit receive
- list request receive
- list response send
- diagnostic ping / status packets

These all look like ordinary unconnected request/response traffic.

### What still looks risky

The main risky area remains NAT introduction:

- today the server relies on `NetPeer.Introduce(...)`
- the host and joining client are still Lidgren peers
- a non-Lidgren master server would need to emit whatever introduction packets
  those peers expect

So the hard part is not "listing servers." The hard part is replacing Lidgren's
NAT-introduction behavior faithfully.

## Best next reverse-engineering targets

If we want a Lidgren-free master server, the highest-value next steps are:

1. Capture or document the exact payload format for:
   - register / heartbeat
   - quit
   - list request
   - list response
   - NAT-intro request
2. Identify the exact packets produced by `NetPeer.Introduce(...)` in the game's
   Lidgren build.
3. Verify whether the current game clients require true Lidgren intro packets or
   only equivalent endpoint data.
4. Preserve the interface-selection lessons from this plugin, because they are
   already solving a real-world host/client issue.

## Current conclusion

The `ApotheonMod` work confirms that:

- the game's master-server traffic is unconnected UDP to port `14343`
- the master-server override is a narrow and well-understood hook
- custom network-interface selection was necessary and already reverse
  engineered successfully

It does **not** yet eliminate Lidgren as a dependency for the master server by
itself, but it significantly narrows the unknowns. A future plain-UDP master
server now looks realistic, with NAT introduction as the main remaining
protocol-compatibility problem.
