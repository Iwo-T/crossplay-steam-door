# Crossplay Steam Door

A server-only BepInEx plugin for a Valheim dedicated server that runs with crossplay on. It opens a direct Steam
connection next to crossplay, so Steam PC players stop going through Microsoft's PlayFab relay while console players
keep crossplay, and it lifts the limits that make a busy server pass the world on too slowly. Nobody installs anything
on their own PC or console.

**Status: work in progress.** It runs on one live server (eight Steam PCs and one PlayStation player) and currently
seems to work fine there: the Steam door since 2026-10-04, the world-sync part since 2026-10-05. It has only been
tried on Valheim 1.0.15 and 1.0.16 and never on a real Xbox. It is not affiliated with Iron Gate. Keep a backup of
your world, and read "Not covered" under Testing before you rely on it.

## Why

With crossplay on, every player, Steam PCs included, reaches the server through the PlayFab relay. On our server that
relay dropped one PC client about every 16 minutes. Each drop froze that player's world for about 30 s, and
everything their game was simulating froze for everyone else. A vanilla crossplay server opens only a crossplay
socket (`ZNet.OpenServer`), so a direct connection was impossible without a plugin.

The second problem is the game's own, on any connection. The server passes world changes on to one player per frame
at 30 fps, at most 10 KB per turn: with six players that is about 4 turns and 43 KB a second each. Buildings are sent
before items and creatures in every turn. During a surtling raid in our base, items were impossible to pick up for
one to two minutes, and after a portal chests and doors answered late: the building updates filled every turn, so who
owns an item never reached the players' games.

## What it changes

- Opens Steam's listen socket on the server's game port (`-port`, 2456 by default), next to the crossplay socket.
  Steam's game-server login already runs on crossplay servers; only the socket was missing.
- Lists the crossplay lobby under the game port + 1. A PC that joins by IP looks the exact `ip:port` up in the
  crossplay listings first and uses Steam only when nothing matches, so joining by IP on the game port now goes
  direct. The server list, join codes and the listed `ip:port+1` keep joining through crossplay.
- Gives each joining player the handshake for their own connection: Steam's session-ticket check for the door,
  vanilla's crossplay check for everyone else.
- Reports a Steam connection's host name as `Steam_<id>`, the form a crossplay server expects, so the player list,
  the world's saved player history, display names, bans and admin checks get valid IDs. A bare SteamID fails to parse
  there and would save `#2`-style duplicate names into the world.

If this Valheim version lacks anything the plugin needs, or the handshake cannot be adapted, it logs an error and
leaves the server vanilla. If another mod later rebuilds the handshake without this change, the door closes and logs
why, so Steam PCs fall back to crossplay instead of hanging.

World sync (since 1.2). It is applied once the door's own patches are in, so not with `Enabled = false`; if a game
update breaks one of these it logs why and that part stays vanilla:
- A Steam player gets 64 KB per turn instead of 10 (`ZDOMan.SendZDOs`). A crossplay player keeps the game's 10 KB:
  the relay is the fragile link.
- Steam's send rate per player goes from the game's 150 KB/s to 1000 KB/s when the door opens. With the larger
  budget and the old rate a player's stream stops well short of what the budget allows (about 133 KB/s on an earlier
  build of 1.2; not measured again on the final one).
- The order within a turn changes in two ways, for every player. Buildings, rocks and other solid objects a player
  has never been sent go before solid objects that only changed, so a burning base can no longer keep the rest of the
  base from someone who arrives. And on every other turn items and creatures go before the solid objects that only
  changed, so a stream of building updates can no longer keep them from being sent.
  No item or creature is ever sent ahead of a solid object the player's game does not have yet, which is the game's
  own rule: a wall torch, an item stand or an animal that arrives before its wall, floor or rock is broken or dropped
  there. Other players' characters and ships, and terrain, still go before all of it, as in the game's own order.
  What this does not speed up: chests, doors, portals and crafting stations are solid objects themselves. For a
  crossplay player, with the game's 10 KB a turn, their updates still wait among the building updates while a base
  burns, and since every other turn now goes to items and creatures first, such an update can take up to twice as
  long to reach them as without the plugin when many creatures are moving as well. That is the price of items staying
  pickable; `FairTurns = false` takes it back.
- Every `ReportMinutes` the server log gets one `World sync` line: frame rate, turns and objects per second per
  player, the longest time a player went without catching up on what is near them, and the most objects that waited.

Side effects players can see:
- The crossplay server list counts only crossplay connections, so consoles see fewer players than are online. The
  server's 10-player cap still counts everyone.
- A PC on the door runs on Steam, so its in-game join-code display stays hidden; anyone else's code still works.

Config, in `BepInEx/config/iwo.valheim.crossplaysteamdoor.cfg`:

| Setting | Default | Effect |
| --- | --- | --- |
| `Enabled` | true | false leaves the server vanilla |
| `SendIpJoinsToSteamDoor` | true | false keeps the vanilla crossplay listing; only PCs not logged in to crossplay then reach the door |
| `SteamSendBudgetKB` | 64 | world data per turn for a Steam player, 10 to 256; 10 is the game's own value. One turn is one Steam message, and Steam refuses messages over 512 KB |
| `CrossplaySendBudgetKB` | 10 | the same for a crossplay player, 10 to 64; nothing above 10 has been tested |
| `SteamSendRateKB` | 1000 | Steam's send rate per player in KB/s; 150 is the game's own value |
| `FairTurns` | true | false restores the game's own order: every solid object, new or changed, before items and creatures in every turn |
| `ReportMinutes` | 10 | minutes between `World sync` log lines; 0 turns them off |

The config is read when the server starts; a changed value needs a restart.

## Install

The server needs BepInEx for Valheim (built against BepInExPack Valheim 5.4.23.5 / HarmonyX 2.9) and crossplay
switched on. Put `CrossplaySteamDoor.dll` from the newest entry on the
[releases page](https://github.com/Iwo-T/crossplay-steam-door/releases) into `BepInEx/plugins` and restart the
server.

On a Nitrado server:

1. Web interface: stop the server.
2. Tools > Addons > `valheim_bepinex` > Install. Not `valheimplus`: as shipped there, Valheim Plus refuses every
   player who does not run it (`enforceMod = true`), consoles included.
3. Tools > File Browser > `/valheim/BepInEx/plugins`: upload `CrossplaySteamDoor.dll`.
4. Settings > General: Crossplay stays on.
5. Start the server.

Check `BepInEx/LogOutput.log`:
- the first lines name the BepInEx version;
- `Loading [Crossplay Steam Door 1.2.0]`, `Steam send rate per player raised to 1000 KB/s` and
  `Steam door open on UDP port <port>`;
- none of `World sync keeps the game's own behaviour`, `Fair turns stopped`, `Send budget falls back`,
  `Steam refused`, `Could not raise`, `No 'World sync' report` or `report stopped`: each says that a part of world
  sync, or its report, fell back to the game's own. The three lines above appear even then, so the first `World sync`
  line is the proof that both parts are in;
- no `Loading [Valheim Plus` line;
- later, `<name> joined through the Steam door as Steam_<id>` for each PC that uses it;
- every ten minutes with players online, a `World sync` line. For door players `longest time behind` should stay at
  a few seconds; tens of seconds mean the budget is too small for what is going on. For a crossplay player long
  times behind are normal while a base burns, because their budget is the game's own.

Updating from an older version: replace the DLL and restart. New settings are added to the config file by themselves.

## Players

- Steam PC: Join Game > Add server > `<server ip>:<game port>` and join that entry. The server-list entry and join
  codes still work but go through the relay.
- Console, and a Game Pass / Microsoft Store PC: the server list, the join code or `<server ip>:<game port + 1>`. A
  saved `ip:<game port>` entry now leads to Steam, which these clients lack, so a console player who had the server
  saved by its address has to find it once more through the server list or a join code.

## Remove

Delete `CrossplaySteamDoor.dll` (or set `Enabled = false`) and restart.

## Build

`dotnet build -c Release -o dist` compiles against the dedicated server's Managed folder and the BepInEx core folder;
pass `-p:ValheimServerDir=<folder>` and `-p:BepInExCoreDir=<folder>` if they are not where the project file expects
them. It targets the game's own `mscorlib` and `netstandard` (no NuGet packages).

## Testing

Local tests run a dedicated server with `-crossplay` (1.0.15; every patched method is byte-identical in 1.0.16) and
a vanilla 1.0.16 client. `FindHostByIp` skips non-public addresses, so `127.0.0.1` goes to Steam even in vanilla; the
routing itself needs the public address.

Passed on 1.1 (2026-10-03):
- join `127.0.0.1:2456`: Steam ticket accepted, `<name> joined through the Steam door as Steam_<id>`, two world
  saves with no `failed to parse`; the saved `.fwl2` holds one history entry, `Steam_<id> | <name>`;
- join `<public ip>:2457`: resolved to the crossplay lobby, `PlayFab-backend`, joined;
- a simulated session (a PlayStation player plus two door PCs, fake peers through vanilla `UpdatePlayerList` and
  `UpdatePlayerHistory`): valid IDs, names without `#N`, history re-sent only on change.

World sync, tested on 1.2 (2026-10-04) with six players on the local server: simulated players on loopback Steam
connections made from inside the server process (the rig in `bench/`; they complete the real join handshake and count
what they receive), 20,000 building objects around them, and a steady stream of changes to those objects.

| Situation | Game's limits | 1.2 defaults |
| --- | --- | --- |
| 600 building changes/s: time until an item's new owner is sent to the players | never while it lasts | 0.10-0.23 s |
| 4000 building changes/s, more than the new budget carries | not run (never already at 600/s) | 0.10-0.47 s |
| a seventh player arrives and needs the whole base (2.1 MB), nothing else going on | 58 s | 9 s |
| a seventh player arrives during 600 changes/s: first item sent to them | none in 150 s | after 8 s |
| world data per player | 43 KB/s | up to 273 KB/s |
| server frame rate, cost of one send turn (six players, 600 changes/s) | 30 fps, 2.6-2.9 ms | 30 fps, 3.6-3.8 ms |

At the crossplay budget (10 KB a turn, the game's own) the order alone does this: an owner change is sent in
0.10-0.47 s instead of never, and a player who arrives during 600 changes/s is sent the first item after 51 s, the
same as on a quiet server, instead of not within 150 s. On an earlier build of 1.2, 64 KB with the game's 150 KB/s
send rate stopped at about 133 KB/s per player and 128 KB carried about 510 KB/s; neither was run again on the final
build.

The rig checked the order of 14,680 send lists the plugin had reordered (and 14,728 more left in the game's own
order): in none was an item or creature ahead of a solid object the player had never been sent, and every arriving
player had been sent all 20,000 building objects before its first item. The server held 30 fps throughout: 29.1 to
29.8 averaged over each phase, 27.5 in its worst five seconds.

With a real 1.0.16 client on 1.2, standing on 2,500 floor pieces that change 800 times a second, with five simulated
players online. Ten coal lumps are handed to the client by the server each time:
- joined `127.0.0.1:2456` through the door (Steam ticket accepted). With the game's limits the coal stayed on the
  ground for 40 s, until the order was switched on, and was picked up that second. With the defaults it was picked up
  1.3 s after the handover;
- joined through the crossplay listing (`PlayFab-backend`, the path a console uses), at the crossplay budget of
  10 KB. With the game's order the coal stayed for 43 s, until the order was switched on, and was picked up that
  second. With the order on from the start it was picked up 1.7 s after the handover. The client's log has no relay
  trouble for the six minutes of that test.

Not covered: a real console in these tests (the crossplay path was driven by a PC client), an Xbox at all, real
network latency and packet loss on the Steam connections (the simulated players are on loopback), a base larger than
the test one, and objects resting on large rocks or in dungeons in a real game (the rule is the game's own there, so
nothing should differ; it was checked on lists, not seen in a game).

On the live server (hosted, Linux, BepInEx 5.4.23.5, Valheim 1.0.16): since 2026-10-04 the lobby registers under the
game port + 1, and Steam PCs that join their saved `ip:<game port>` entry come in through the door (`Steam-backend`
in the client log), with no `failed to parse` lines and no exceptions. The PlayStation player joined through the
server list or a join code the same day; names and chat looked normal to him. 1.2 started cleanly there on 2026-10-05.

First evening on a real server:
- a Steam PC joins `ip:<game port>`: its `Player.log` says `Steam-backend`, the server log says
  `joined through the Steam door`;
- the same PC joins `ip:<game port + 1>`: `PlayFab-backend`;
- a console player sees the PC player's name without `#2` and can read their chat;
- the server log has no `failed to parse` lines.

## Licence

MIT, see `LICENSE`.
