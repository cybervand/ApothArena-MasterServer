# Lidgren Networking Notes

This note is a working summary of how Lidgren networking behaves, focused on
the parts that matter for Apotheon Arena's master-server replacement and
internet-connectivity debugging.

## What Lidgren is

Lidgren is a message-oriented networking library built around a single UDP
socket. `NetPeer` is the base type; `NetClient` and `NetServer` are higher-level
roles built on top of it.

## Version/runtime note for this repo

The Lidgren package currently used by this repo is old relative to the runtime
targets in this solution.

- [MasterServer.csproj](/m:/develop/Apotheon%20Arena/MasterServer/MasterServer.csproj)
  targets `net8.0`
- [MasterServerProbe.csproj](/m:/develop/Apotheon%20Arena/MasterServerProbe/MasterServerProbe.csproj)
  targets `net8.0`
- the restored `Lidgren.Network` package does not provide a native `net8.0`
  build, so NuGet falls back to older .NET Framework assets

That is why the build emits `NU1701` warnings. In practical terms, the Lidgren
we are using "wants" old .NET Framework-era targets, even though it still builds
and runs under .NET 8 in our current tests.

This does not automatically mean Lidgren is the source of the current internet
join bug, but it does mean:

- compatibility issues are more plausible than they would be with a modern
  `net8.0`-targeted package
- behavior should be verified with real network tests instead of assuming modern
  runtime compatibility
- if we hit strange edge cases, moving to a newer/forked Lidgren build or
  vendoring the source may become worth considering

For this project, the important consequence is that there are two different
traffic styles:

- connected traffic, where Lidgren manages a `NetConnection`
- unconnected traffic, where the app sends and receives raw UDP payloads through
  Lidgren helpers

Our replacement master server currently uses unconnected traffic, not
connection-oriented gameplay traffic.

## Core building blocks

### `NetPeer`

`NetPeer.Start()` binds the UDP socket and starts Lidgren's networking thread.
`NetPeer.ReadMessage()` or `ReadMessages()` is then used to drain incoming
events/messages.

Relevant methods for us:

- `SendUnconnectedMessage(...)`: send a UDP packet to a host without creating a
  `NetConnection`
- `DiscoverKnownPeer(...)`: send a discovery probe directly to a known host
- `DiscoverLocalPeers()`: broadcast on the local subnet
- `Introduce(...)`: ask Lidgren to send NAT-introduction packets to two peers

### `NetPeerConfiguration`

Important settings:

- `AppIdentifier`: peers only connect to others with the same app identifier
- `Port`: local UDP port to bind to
- `LocalAddress`: explicit local bind address; default is `IPAddress.Any`
- `AcceptIncomingConnections`: true for `NetServer`, false for `NetClient`
- `EnableUPnP`: Lidgren can try UPnP for port forwarding / external IP support

For diagnostics, `NetPeerConfiguration` also exposes `EnableMessageType()` and
related APIs so the app can opt into additional incoming message categories.

## Incoming message model

Lidgren delivers everything as `NetIncomingMessage` objects. The important split
is:

- application payloads
- library/system events

In this project, the master server mostly cares about `UnconnectedData`, because
the game's master-server protocol appears to be carried over unconnected UDP
messages.

For troubleshooting, the useful non-payload message types are:

- `StatusChanged`
- `WarningMessage`
- `ErrorMessage`
- `DebugMessage`
- `VerboseDebugMessage`

Connection state is tracked with `NetConnectionStatus`, including:

- `None`
- `InitiatedConnect`
- `ReceivedInitiation`
- `RespondedAwaitingApproval`
- `RespondedConnect`
- `Connected`
- disconnect/failure states

This matters because a failed internet join may die before a real connected
state is ever reached. If that happens, the failure is likely in discovery,
master-server coordination, or NAT introduction rather than in gameplay traffic.

## Discovery and master-server style traffic

Lidgren supports two broad discovery paths:

1. local broadcast discovery with `DiscoverLocalPeers()`
2. directed discovery or other unconnected traffic to a specific endpoint with
   `DiscoverKnownPeer(...)` or `SendUnconnectedMessage(...)`

Apotheon Arena's master server appears to use the second model:

- host sends unconnected registration/heartbeat packets to the master server
- clients send an unconnected list request to the master server
- clients send an unconnected NAT-introduction request when trying to join

That matches our replacement server implementation in
[Program.cs](/m:/develop/Apotheon%20Arena/MasterServer/Program.cs).

## NAT introduction

Lidgren exposes `NetPeer.Introduce(...)` specifically for NAT introduction. The
official API docs describe it as sending a `NetIntroduction` to the host's
external endpoint and the client's external endpoint, introducing the client to
the host.

That has a very important implication:

- the introducer must know both peers' reachable external endpoints
- it also helps to know each peer's internal endpoint for same-LAN cases
- if the introducer has the wrong "external" endpoint, hole punching will fail

In practical terms, the master server needs a good record of:

- host external endpoint
- host internal endpoint
- client external endpoint
- client internal endpoint

If any "external" endpoint is really a private address, NAT introduction for
internet players is likely broken before the join even starts.

## Why private or link-local addresses are dangerous

There are three address classes that matter in our logs:

- public internet IPs: usable for internet joins
- private RFC1918 IPs such as `192.168.x.x` or `10.x.x.x`: usable only inside a
  LAN/NAT boundary
- link-local APIPA addresses such as `169.254.x.x`: usually a bad sign for
  peer-to-peer internet play

For Apotheon Arena specifically:

- if the game reports `169.254.x.x` as its internal IP, Lidgren likely chose the
  wrong local adapter or the machine has no proper LAN address on that adapter
- if the master server sees a host's sender endpoint as `192.168.x.x`, then the
  server is not learning the host's real public internet endpoint

That second case is exactly what breaks internet NAT introduction.

## What our current logs suggest

From the master-server log we captured:

- the host reported internal `169.254.83.107:14242`
- our server sanitized that to `192.168.1.1:14242`
- the sender endpoint was also `192.168.1.1:14242`

That means the master server currently believes:

- host internal endpoint = `192.168.1.1:14242`
- host external endpoint = `192.168.1.1:14242`

For internet play, that is not a valid public endpoint. `192.168.1.1` is a
private LAN address, typically the router itself.

### Likely interpretation

The host appears to be reaching the master server from the same LAN or through a
hairpin/NAT-loopback path, so the master server never observes the host's real
public internet endpoint.

If that is true, then:

- server listing can still work
- remote players can still see the match
- NAT introduction will likely fail because the introducer gives out a private
  "external" endpoint

## Debugging checklist for this project

### Master server

- confirm registration heartbeats arrive from a real public IP when the host is
  truly remote
- confirm list requests appear when a client opens the server browser
- confirm NAT-intro requests appear when a client clicks Join
- confirm the host and client endpoints logged in the NAT request are plausible

### Host

- log the local IP Lidgren selects
- log unconnected sends to the master server
- log connection status changes
- test with UDP `14242` explicitly forwarded to the host PC as a control case

### Joining client

- log connection status changes
- log any NAT-related or unconnected traffic around the Join click
- verify whether the join attempt ever leaves `InitiatedConnect`

## Practical hypotheses to test next

1. The host is not truly remote from the master server, so the master server is
   learning a private address as the host's "external" endpoint.
2. Lidgren on the host is selecting a bad local adapter and reporting a
   `169.254.x.x` address.
3. UDP hole punching is failing, and explicit forwarding of UDP `14242` on the
   host would make joins succeed.
4. The game's join flow depends on additional Lidgren message types or client
   behavior that we have not logged yet.

## Best next measurements

The highest-value test is:

1. run the master server on a machine that is truly public or at least on a
   different network than the host
2. host a game from a separate home network
3. verify the master server logs a real public host endpoint
4. have a third machine on another internet connection attempt the join

If the host still registers with a private "external" address in that setup,
then we likely still have a protocol or endpoint-selection bug. If it registers
with a real public IP but joins still fail, the problem shifts toward NAT type,
host firewalling, or gameplay-port reachability.

## Sources

- Official Lidgren repository README:
  https://github.com/lidgren/lidgren-network-gen3
- Lidgren API docs, `NetServer Methods`:
  https://documentation.help/Lidgren.Network/9584e17f-5c71-cf7f-d200-dbe4ad6f4e3b.htm
- Lidgren API docs, `NetClient Methods`:
  https://documentation.help/Lidgren.Network/52c4a305-4d31-17cc-a6d5-608ebc8da30d.htm
- Lidgren API docs, `NetPeerConfiguration Properties`:
  https://documentation.help/Lidgren.Network/50c5a272-124c-2f72-4e59-ee6c581b7452.htm
- Lidgren API docs, `ReadMessages Method`:
  https://documentation.help/Lidgren.Network/f4ce37a3-ea46-e1ff-b171-a7b9b1c144c8.htm
- Lidgren source/docs for `NetConnectionStatus` enum:
  https://github.com/lidgren/lidgren-network-gen3/blob/master/Lidgren.Network/NetConnectionStatus.cs

## Notes on confidence

- The high-level description of `NetPeer`, discovery, unconnected traffic,
  `Introduce(...)`, and configuration comes directly from Lidgren's official
  repository/docs.
- The Apotheon Arena protocol details are inferred from this repo's patched
  client behavior and the replacement master server code.
- The diagnosis about private endpoints breaking internet joins is an inference
  from the Lidgren API plus our captured logs.
