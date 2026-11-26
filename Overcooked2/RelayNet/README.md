# RelayNet

Online play over **Steam Datagram Relay** (SDR), Valve's modern relay network, instead of the legacy Steam P2P API Overcooked! 2 was built on.

## Why

The game sends everything through the old `ISteamNetworking` P2P API from 2018. That API connects two players directly when it can. When it can't, which is common between countries and behind home routers, it falls back to Valve's old relay servers, and those often route the long way round. Newer games use Steam Datagram Relay, which carries traffic over Valve's own backbone along a route chosen by measured ping. That's why the same friend can feel close in one game and far away in Overcooked! 2.

RelayNet moves the game's traffic onto Steam Datagram Relay. It doesn't change what the game sends or how it plays, only the path the packets take.

## What it can and can't do

- It helps when the lag comes from the **route**: the old API using a far-away relay, or a poor direct path.
- It can't beat **distance**. If your friend is 150 ms away on the best route, it stays 150 ms.
- It doesn't change the game's design. The host runs the kitchen, so a client waits one round trip for every pick-up, chop and throw. A shorter round trip makes that wait shorter, but it doesn't go away.

## Requirements

- **Both players need RelayNet.** Each pair of players switches on its own. With anyone who doesn't have it, the game keeps its old path, so mixed lobbies still work.
- Steam running and logged in, as for any online game.

## How it works

The game's whole Steam transport is one class, `SteamOnlineMultiplayerSessionTransportCoordinator` (`SendData`, `SendVoipData`, `Update`, `OnDataEvent`). RelayNet wraps those methods. Everything above them runs unchanged over the new path: messages, keepalives, disconnect timeouts and latency measurement.

1. **Reaching SDR.** The game's 2018 Steamworks.NET has no binding for `ISteamNetworkingMessages`, but the running Steam client provides it. RelayNet asks for `SteamNetworkingMessages002` through the game's own `SteamClient.GetISteamGenericInterface` and calls it through the C++ vtable. No native files are shipped. On 32-bit Windows (the Windows build), each method gets a 10-byte stub that converts a stdcall call into MSVC thiscall.
2. **Self-test.** When the game starts, RelayNet sends itself a message and reads it back. That checks the interface lookup, the vtable calls and the received-message layout on that machine, and the log says whether it passed. A wrong result turns the relay off for that run. Every message from another player is checked the same way.
3. **Handshake.** While the game talks to a player, RelayNet sends them a small hello over the relay every second. Once both sides have heard each other, that player is switched. If there's no answer within 30 s, the player doesn't have RelayNet, and they stay on the old path.
4. **No reordering at the switch.** At the moment of the switch, one 16-byte marker goes over the old path, reliably and on the game's own channel. The other side holds relay data from that player until the marker arrives. The old reliable channel is ordered, so every message sent before the switch has reached the game by then, and the game sees a single ordered stream. RelayNet removes its markers before the game sees them.
5. **Fallback.** If the relay session breaks or a reliable send fails, that player goes back to the old path. While a player is on the relay, a tiny keepalive goes over the old path every 2 s so that fallback is always ready.

## Seeing the difference

During online play, an overlay in the top-right corner (**F9** shows or hides it) lists each player:

- `Steam relay 48 ms via sgp · game RTT 60 ms`: on the relay, with the relay's ping and the data centre it goes through
- `old P2P (relayed) · game RTT 210 ms`: on the old path, which Steam says is going through its old relay

**game RTT** is the round trip the game itself measures (`LatencyMeasure`), which is what you feel. To compare, play a session with `Enabled = false` (old path, still measured), then one with RelayNet on. The log (`BepInEx/LogOutput.log`) also gets a line per player every 30 s.

## Settings

`BepInEx\config\com.manhl.overcooked2.relaynet.cfg`

| Setting | Default | |
|---|---|---|
| `Enabled` | `true` | Use the relay. Off: the old path, but the overlay still measures. Takes effect from the next online session |
| `ShowStats` | `true` | Show the overlay during online play |
| `StatsKey` | `F9` | Shows or hides the overlay |

## Build

```
build.cmd           build and stage in ..\dist\
build.cmd deploy    also copy into the game's BepInEx\plugins\RelayNet (game closed)
```
