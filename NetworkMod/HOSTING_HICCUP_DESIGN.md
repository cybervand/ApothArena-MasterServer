# Master Server Hiccup Design

## Problem

When the game talks to the master server, the networking path appears to re-resolve the master server and re-select the local network interface too aggressively. In practice, this produces repeated log lines like:

```text
[Info   :ApotheonMod] Changing master server ip to '84.210.67.16'.
[Info   :ApotheonMod] Modifying retrieved NetworkInterface.
[Info   :ApotheonMod] Modifying retrieved NetworkInterface.
```

The visible symptom is a small gameplay hitch during master-server activity. It has been observed:

- while hosting
- while joining

That makes this look less like a host-only heartbeat problem and more like a general "master-server contact path is doing too much synchronous work" problem.

## Confirmed Issues

### Issue 1: `NetUtility.Resolve(...)` Is Called Every `ServerUpdate` Tick

While hosting, `Apotheon.Network.ServerUpdate()` calls:

```text
NetUtility.Resolve("50.19.227.23", 14343)
```

every frame.

The current prefix patch rewrites the string and returns `true`, which means Lidgren still performs the full resolve every tick.

That makes the redirect patch correct, but too expensive for a frame-sensitive host update loop.

#### Preferred Fix

Short-circuit the prefix:

- resolve the redirected master target once
- cache the resulting `IPEndPoint`
- on later calls, set `__result` directly
- return `false` so Lidgren's resolve is skipped entirely

#### Validation Result

This fix was tested and did reduce repeated resolve work, but the repeated calls still happen because the game itself continues to invoke `NetUtility.Resolve(...)` in the update path.

That means:

- short-circuiting `Resolve(...)` is still worthwhile
- but it is not enough by itself to remove the hitch completely
- remaining hitch sources likely live in other work still attached to the same repeated master-server path

### Issue 2: Logging Uses Synchronous Disk I/O on the Hot Path

Current logging performs synchronous file writes on the caller thread. If the logger uses `File.AppendAllText(...)` under a lock, then repeated resolve and send-path logs become repeated disk I/O in frame-sensitive code.

Two especially suspicious cases:

- redirect logging in `MasterServer.Redirect(...)`
- send-path logging such as `SendUnconnectedMessage(...)`

#### Preferred Fixes

##### Best immediate fix

Log once:

- only emit the redirect line when the `(input, output)` pair changes
- make repeated identical redirects silent

##### Better hot-path fix

Do not log inside the `Resolve(...)` prefix at all.

Instead:

- log the resolved master target once at mod startup
- log again only at config reload or if the configured target changes

##### Heavier but strongest fix

Move file writes off the caller thread:

- use a `ConcurrentQueue<string>`
- drain it on a background thread
- keep the gameplay thread limited to enqueue operations

## Why This Likely Happens

The current networking patch strategy targets global networking helpers that are called by both setup-time and steady-state network traffic:

- `NetUtility.Resolve(string)`
- `NetUtility.Resolve(string, int)`
- `NetUtility.GetMyAddress(...)`
- host role wrappers around:
  - `Network.ServerStart()`
  - `Network.ServerUpdate()`
  - `Network.ServerQuit()`

This means the mod logic is not only affecting initial host setup. It is affecting any flow that touches these helpers during:

- browsing
- joining
- hosting
- heartbeats or refresh loops

Several expensive things are especially suspicious:

1. Repeated full master-server resolution
   - per-frame calls into `NetUtility.Resolve(...)`
   - repeated resolve work for the same endpoint

2. Repeated interface selection
   - Enumerating network adapters
   - Inspecting gateway information
   - Picking the preferred IPv4 address

3. Repeated master-server redirect work and logging
   - Rewriting the master server target every time `Resolve(...)` is called
   - Logging each redirect and interface rewrite on the main thread

4. Synchronous file I/O on hot paths
   - lock + append logging in frame-sensitive code
   - potential frame hitch every time disk I/O is forced

Even if the actual redirect string replacement is cheap, repeated resolve work, repeated network-interface probing, and repeated synchronous logging can easily produce stalls in an older game loop.

## Traffic Model

The important distinction is that multiple gameplay flows can legitimately contact the master server:

- Browse flow
  - fetch the current server list

- Join flow
  - resolve the master server
  - request NAT introduction / join metadata

- Host flow
  - resolve the master server
  - register the hosted match
  - send periodic heartbeats

Talking to the master server is normal in all three cases. The likely bug is not "the game contacted the master server." The likely bug is "the patch performs expensive address/interface work every time that happens, even when the answer has not changed."

## Current Patch Behavior

Based on the current `NetworkMod` code:

- `NetUtility.Resolve(...)` is patched globally and redirects the original hardcoded master server IP to the configured community server.
- `NetUtility.GetMyAddress(...)` is patched globally and chooses an override or auto-detected LAN IP.
- Role flags (`IsHosting`, `IsJoining`) are set around host/join methods so `GetMyAddress(...)` can behave differently for hosting vs joining.
- Auto-detected host/client IPs are persisted back into config when blank.

This design is functionally correct, but it is too eager for the steady-state hosting path.

## Design Goals

- Remove visible hitches during master-server interactions.
- Keep master-server redirection working.
- Keep correct LAN IP selection for both hosting and joining.
- Avoid repeated full resolves for the same master-server endpoint.
- Avoid repeated expensive network-interface scans during repeated master-server traffic.
- Avoid repeated noisy logs for operations that have already been decided.
- Keep file I/O off frame-sensitive paths.
- Preserve compatibility with the current Harmony-based approach.

## Non-Goals

- Replacing Lidgren.
- Changing master-server protocol behavior.
- Rewriting the full networking stack.
- Fixing every join or NAT edge case in the same pass.

## Proposed Design

### 1. Split Discovery From Steady-State Use

Treat address/interface selection as a session decision, not a "do it every time we talk to the master server" decision.

Recommended approach:

- Compute the preferred host LAN IP once when hosting starts.
- Cache the result for the hosting session.
- Reuse the cached value during `ServerUpdate()` heartbeats instead of re-enumerating interfaces.

Likewise for joining:

- Compute the preferred client LAN IP once when joining starts.
- Cache it for the active join attempt.

Likewise for browsing / generic master contact:

- cache the redirected master server target
- avoid repeated identical resolution/logging when the configured target has not changed

### 2. Add Explicit Session Caches

Introduce lightweight cached state, for example:

- `CachedHostIp`
- `CachedClientIp`
- `CachedMasterServerTarget`
- `CachedMasterServerEndpoint`
- `LastHostDiscoveryUtc`
- `LastClientDiscoveryUtc`

These values should be invalidated only when needed:

- hosting starts
- hosting quits
- joining starts
- join completes/fails
- config changes
- explicit manual refresh

### 3. Short-Circuit `Resolve(...)` for the Master Server

For the hardcoded original master server target:

- resolve the redirected target once
- cache the resulting `IPEndPoint`
- on future calls, set `__result` directly
- return `false` from the prefix

This is more important than merely rewriting the input string, because it skips the repeated full resolve work entirely.

### 4. Make `GetMyAddress(...)` a Fast Path

The patched `GetMyAddress(...)` should follow this order:

1. If a configured override exists, return it immediately.
2. If a valid cached session value exists, return it immediately.
3. Only then perform adapter discovery.
4. Store the discovered result for reuse.

This turns repeated master-server traffic into a cheap read instead of a repeated full adapter walk.

### 5. Debounce or Eliminate Repeated Redirect Logging

Master-server redirect logging should not fire every time the same redirect is applied.

Instead:

- log once per session when redirect becomes active
- log again only if the target actually changes
- optionally expose a verbose mode for per-call tracing

Example:

- Good default:
  - `Master server redirect active: 50.19.227.23 -> 84.210.67.16`
- Debug-only:
  - `Resolve redirect applied during host heartbeat`

### 6. Debounce or Eliminate Repeated Interface Logging

Likewise, "modifying retrieved network interface" style logs should not appear on every master-server touch.

Instead:

- log once when the host IP is first chosen
- log again only if the chosen IP changes
- optionally log cache hits only in verbose mode

### 7. Keep Role-Aware Behavior, But Move Role Entry Earlier

The role flags are still useful, but the expensive discovery should happen at role-transition time instead of during repeated utility calls.

Recommended pattern:

- `Network.ServerStart()`:
  - mark hosting role
  - resolve and cache host LAN IP once
  - optionally resolve and cache redirected master-server target once

- `Network.ServerUpdate()`:
  - keep role context if needed
  - avoid re-running expensive discovery unless cache is missing or invalid

- `Network.ServerQuit()`:
  - clear host cache

- `Network.RequestNATIntroduction(long hostid)`:
  - mark joining role
  - resolve and cache client LAN IP once for that join flow

- Browser refresh / generic master-server contact:
  - use cached redirected master target
  - avoid repeated address/interface rediscovery unless a role-specific cache is missing

### 8. Avoid Main-Thread File Writes During Hot Paths

If config persistence or log writes are happening during repeated master-server calls, they should be moved off the hot path.

Specifically:

- only save detected IPs the first time they are learned
- do not save repeatedly during heartbeats
- avoid append-to-log spam in tight or periodic loops
- prefer enqueue-and-flush logging over immediate file appends

## Recommended Implementation Plan

### Phase 1: Instrumentation

Before changing behavior, confirm which calls are recurring during the hitch:

- count calls to `Resolve(...)` while hosting
- count calls to `Resolve(...)` while joining
- count calls to `GetMyAddress(...)` while hosting
- count calls to `GetMyAddress(...)` while joining
- count repeated identical redirect events
- measure how long adapter selection takes
- measure how often file logging occurs during browse/join/host flows

This should be lightweight and temporary.

### Phase 2: Short-Circuit Master Resolve

Implement a cache for the redirected master-server endpoint and make the Harmony prefix return the cached `__result` directly.

Expected result:

- repeated full resolves disappear from hot paths
- host update hitches should drop

Validation note:

- this reduces the cost of the repeated calls
- it does not stop the game from making those calls
- if hitches remain, the next suspects are `GetMyAddress(...)`, interface discovery, synchronous logging, and send-path work

### Phase 3: Cache Host/Client IP Selection

Implement session caches for:

- host LAN IP
- client LAN IP

Then make `GetMyAddress(...)` return cached values in the common case.

Expected result:

- repeated interface enumeration disappears from the hot path
- master-server-related hitches should reduce significantly

### Phase 4: Cache and Debounce Master Redirect Behavior

Make redirect handling effectively constant-time and mostly silent after the first successful decision.

Expected result:

- less main-thread log noise
- easier to see real state changes

### Phase 5: Move Expensive Discovery to Role Boundaries

Shift expensive work from:

- every host update
- every join contact
- every browse refresh

to:

- host start
- join start
- browser refresh session start if needed
- explicit cache refresh events

### Phase 6: Move Logging Off the Hot Path

At minimum:

- remove repeated redirect logging from the resolve prefix
- keep only one-time or change-driven logs in hot paths

Optionally:

- replace synchronous append logging with an async queue-based sink

### Phase 7: Add Optional Verbose Diagnostics

Keep deep tracing available without making normal hosting noisy.

Suggested modes:

- normal:
  - one-time redirect and interface selection logs
- verbose:
  - per-call utility tracing for investigation

## Risks

- Over-caching could preserve a stale LAN IP if the active adapter changes while the game is already hosting.
- Some join flows may rely on timing or context that is currently being inferred from repeated helper calls.
- If caching is added carelessly, host and join state could leak across each other.

## Mitigations

- Clear caches on host/join lifecycle boundaries.
- Allow a manual override to always win over cache.
- Add a conservative refresh rule if the cached IP becomes invalid.
- Keep a verbose mode available while validating the new behavior.

## Suggested Acceptance Criteria

- Master-server interactions no longer cause visible hitches during hosting or joining.
- Repeated host update ticks do not perform full resolve work for the same master-server endpoint.
- While hosting, repeated heartbeats do not trigger full interface re-selection.
- While joining, repeated master-server contact does not trigger repeated identical interface discovery.
- Repeated master-server contact does not spam identical redirect logs.
- Logging no longer performs synchronous disk writes on frame-sensitive paths.
- Joining still uses the correct LAN IP behavior.
- Master-server redirection still works reliably.
- Manual `hostIp` and `clientIp` overrides still take precedence.

## Short Version

The likely fix is not "stop contacting the master server." The likely fix is:

- stop re-resolving the same master server every tick
- stop doing setup-quality network discovery work every time the game contacts the master server
- stop doing synchronous file I/O on frame-sensitive paths

Cache the decisions, log only when they change, and keep the hot path cheap.
