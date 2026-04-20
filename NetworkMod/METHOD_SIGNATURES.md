# Apotheon Arena Modding: Method Signatures

This guide covers two things:

1. How Harmony identifies a method in Apotheon Arena.
2. Which method signatures are currently verified and useful for modders.

The examples below are based on the actual shipped game plus the current patcher/mod-loader setup in this repo.

## The Short Version

When you patch a method with Harmony, the target is identified by:

- declaring type
- method name
- parameter types, in order

If you get any of those wrong, your patch may:

- not apply
- apply to the wrong overload
- crash at runtime

For this project, always prefer exact signatures over name-only patching.

## How To Specify A Signature

For overloaded methods, always include the parameter type array:

```csharp
[HarmonyPatch(typeof(NetUtility), "Resolve", new[] { typeof(string) })]
```

```csharp
[HarmonyPatch(typeof(NetUtility), "Resolve", new[] { typeof(string), typeof(int) })]
```

If a method is not overloaded, name-only patching can work, but exact signatures are still safer when the target is important.

## Harmony Patch Method Shapes

These are the most useful patch shapes for Apotheon Arena mods.

### Prefix

Use a prefix when you want to inspect or change arguments before the original runs.

```csharp
static bool Prefix(ref string ipOrHost)
{
    // Modify argument here.
    return true; // true = continue into original method
}
```

Notes:

- Use `ref` to modify arguments.
- Return `false` to skip the original method completely.
- For instance methods, you can add `__instance`.

### Postfix

Use a postfix when you want to inspect or replace the result after the original runs.

```csharp
static void Postfix(ref IPAddress __result, ref IPAddress mask)
{
    // Modify return value here.
}
```

Notes:

- Use `ref __result` to replace the return value.
- If the original method has `ref` or `out` parameters, your patch must match them correctly.

### Instance Method Example

For instance methods, Harmony can pass the object instance:

```csharp
static void Prefix(Apotheon.Network __instance)
{
    // Read or modify instance state here.
}
```

## Verified Networking Targets

These signatures are either verified directly from the game or already used by the current mod/patcher.

### Lidgren.Network.NetUtility

#### `Resolve(string)`

Signature:

```csharp
static System.Net.IPAddress Resolve(string ipOrHost)
```

Good for:

- redirecting a single IP/hostname before resolution
- config-driven overrides

Current built-in example:

```csharp
[HarmonyPatch(typeof(NetUtility), "Resolve", new[] { typeof(string) })]
static class Resolve_String_Patch
{
    static bool Prefix(ref string ipOrHost)
    {
        ipOrHost = MasterServer.Redirect(ipOrHost);
        return true;
    }
}
```

#### `Resolve(string, int)`

Signature:

```csharp
static System.Net.IPEndPoint Resolve(string ipOrHost, int port)
```

Good for:

- master-server redirect
- host/port redirection logic

Current built-in example:

```csharp
[HarmonyPatch(typeof(NetUtility), "Resolve", new[] { typeof(string), typeof(int) })]
static class Resolve_StringInt_Patch
{
    static bool Prefix(ref string ipOrHost)
    {
        ipOrHost = MasterServer.Redirect(ipOrHost);
        return true;
    }
}
```

#### `GetMyAddress(ref IPAddress mask)`

Signature:

```csharp
static System.Net.IPAddress GetMyAddress(ref System.Net.IPAddress mask)
```

Good for:

- fixing adapter selection
- replacing bad `169.254.x.x` picks
- forcing a configured IPv4

Current built-in example:

```csharp
[HarmonyPatch(typeof(NetUtility), "GetMyAddress")]
static class GetMyAddress_Patch
{
    static void Postfix(ref IPAddress __result, ref IPAddress mask)
    {
        // Replace __result if needed.
    }
}
```

Why this method matters:

- host registration uses it
- heartbeat registration uses it
- client NAT introduction uses it

That makes it one of the best general-purpose networking hooks in the game.

### Apotheon.Network

#### `ServerStart()`

Signature:

```csharp
instance System.Void ServerStart()
```

Good for:

- host registration setup
- altering what the server advertises when hosting
- logging the initial host registration path

Verified behavior:

- resolves the master server
- builds the initial `ServerInfo`
- writes the advertised local endpoint
- sends the first unconnected register packet

#### `ServerUpdate()`

Signature:

```csharp
instance System.Void ServerUpdate()
```

Good for:

- heartbeat behavior
- periodic re-registration changes
- host-side networking diagnostics

Verified behavior:

- rebuilds `ServerInfo`
- sends heartbeat/register updates to the master server

#### `RequestNATIntroduction(long hostid)`

Signature:

```csharp
instance System.Void RequestNATIntroduction(long hostid)
```

Good for:

- client join flow
- NAT introduction debugging
- overriding what the client reports as its local endpoint

Verified behavior:

- resolves the master server
- calls `NetUtility.GetMyAddress(ref mask)`
- falls back to `GetLocalIPAddress()` if needed
- builds the NAT intro request
- sends it with `SendUnconnectedMessage(...)`

This is the most important client-side join hook.

#### `GetLocalIPAddress()`

Signature:

```csharp
static System.Net.IPAddress GetLocalIPAddress()
```

Good for:

- understanding the game's fallback behavior
- replacing or bypassing the old DNS-based local IP selection

Caution:

- this is a simple fallback helper
- it is not as good a hook as `NetUtility.GetMyAddress(ref IPAddress mask)` for route-aware selection

### Apotheon.Program

#### `Main(string[] args)`

Signature:

```csharp
static System.Void Main(string[] args)
```

Good for:

- very early bootstrap logic
- one-time initialization
- installing global Harmony patches

Caution:

- the patcher already injects `ApotheonArena.NetworkMod.ModLoader.Init()` here
- mods should avoid patching `Main` unless they truly need startup-order control

## Recommended Hooks By Goal

### Redirect the master server

Use:

- `Lidgren.Network.NetUtility.Resolve(string)`
- `Lidgren.Network.NetUtility.Resolve(string, int)`

### Fix host-side IP advertisement

Use:

- `Lidgren.Network.NetUtility.GetMyAddress(ref IPAddress mask)`
- optionally `Apotheon.Network.ServerStart()`
- optionally `Apotheon.Network.ServerUpdate()`

### Fix client join / NAT intro behavior

Use:

- `Apotheon.Network.RequestNATIntroduction(long hostid)`
- `Lidgren.Network.NetUtility.GetMyAddress(ref IPAddress mask)`

### Add early startup behavior

Use:

- `Apotheon.Program.Main(string[] args)`

Only do this when a normal gameplay/networking hook is too late.

## Methods To Avoid Treating As Stable API

The patcher injects helper methods into the game and Lidgren at patch time. These are useful internally, but modders should not rely on them as stable public extension points.

Examples include:

- `Apotheon.Network.__GetMasterServerHost()`
- `Apotheon.Network.__GetAdvertisedLocalIp(...)`
- `Apotheon.Network.__WriteAdvertisedExternalEndpoint(...)`
- `Lidgren.Network.NetUtility.__ReadServerIp(...)`
- `Lidgren.Network.NetUtility.__GetPreferredLocalIp(...)`
- `Lidgren.Network.NetUtility.__GetBestLocalIp(...)`

Why avoid them:

- they are implementation details of the patcher
- their names and signatures may change as the patcher evolves
- a clean Harmony patch against shipped game methods is more portable

## Practical Rules For Modders

- Prefer Harmony patches against original game methods or original Lidgren methods.
- When a method has overloads, always specify the parameter type array.
- Match `ref` and `out` parameters exactly.
- Prefer patching `GetMyAddress(ref IPAddress mask)` over patching many call sites one by one.
- Use `RequestNATIntroduction(long hostid)` when you need client-join-specific behavior.
- Avoid depending on patcher-injected helper methods unless you control the exact patcher version in use.

## Minimal Templates

### Patch an overloaded static method

```csharp
using HarmonyLib;
using Lidgren.Network;

[HarmonyPatch(typeof(NetUtility), "Resolve", new[] { typeof(string), typeof(int) })]
static class MyResolvePatch
{
    static bool Prefix(ref string ipOrHost)
    {
        return true;
    }
}
```

### Patch an instance method

```csharp
using HarmonyLib;

[HarmonyPatch(typeof(Apotheon.Network), "RequestNATIntroduction", new[] { typeof(long) })]
static class MyNatPatch
{
    static void Prefix(Apotheon.Network __instance, long hostid)
    {
    }
}
```

### Replace a returned IP address

```csharp
using System.Net;
using HarmonyLib;
using Lidgren.Network;

[HarmonyPatch(typeof(NetUtility), "GetMyAddress")]
static class MyGetMyAddressPatch
{
    static void Postfix(ref IPAddress __result, ref IPAddress mask)
    {
        // Example: replace __result if you have a better IPv4.
    }
}
```

## Final Advice

For this project, the most useful networking signature to understand is:

```csharp
static IPAddress GetMyAddress(ref IPAddress mask)
```

That one method influences both hosting and joining, which makes it the highest-value Harmony target in the current networking stack.
