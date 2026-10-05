# Sync bench

The load-test rig behind the world-sync numbers in the main README. A second BepInEx plugin for a **local test
server only**: it makes simulated players, a synthetic base and a steady stream of changes, and measures what the
server sends to whom.

**Never put `SyncBench.dll` on a real server.** It accepts its own fake Steam ticket in place of Steam's check
(`AcceptBenchTickets`), so with it installed a client that knows the ticket joins without Steam having verified it.

## What it does

- **Simulated players** connect to the server's own Steam socket over loopback from inside the server process. They
  go through the real join handshake, report a position like a game does, and count the world data they receive.
  They have no body unless `bodies` gives them one, and they simulate nothing.
- **A base**: `base` places any building piece by the thousand around a point; `carpet` lays a floor grid.
- **Changes**: `churn` rewrites the health of random base pieces at a steady rate, which is what a burning base
  does to the send queue: every changed piece has to go to every player again.
- **Probes** are items whose owner the rig changes every few seconds. The time until each player has been sent the
  new owner is the time an item could not be picked up.
- **Arrivals**: `arrive` connects one more player and times how long until it has been sent everything, and what it
  still lacked when it was sent its first item.
- **Order check** (`set` with `orderCheck`): after the door has changed a send list, counts every list in which
  something that is not solid sits ahead of a solid object the player was never sent. That count has to stay 0.

## Use

Build with `dotnet build -c Release -o out` in this folder, copy `out/SyncBench.dll` next to
`CrossplaySteamDoor.dll` on the test server, and start the server with `-crossplay`. Commands are JSON files dropped
into `BepInEx/bench/`; results are `BENCH` lines in `BepInEx/LogOutput.log`, one summary per report window.

`run-steady.sh <server folder>` and `run-arrivals.sh <server folder>` run the sequences the README numbers come from.
`run-arrivals.sh` expects the six players, the base and the probes that `run-steady.sh` leaves behind.

| `cmd` | Fields | Does |
| --- | --- | --- |
| `fakes` | `count`, `x`, `z` | Connect simulated players at that spot |
| `arrive` | `x`, `z` | Connect one more and time its arrival |
| `leave` | `fake` (index; omit for all) | Disconnect. The player is removed from the list, so later ones move up |
| `move` | `fake`, `x`, `z`, `time` | Move a simulated player; `time` treats it as an arrival |
| `bodies` | | Give the simulated players a character |
| `base` | `count`, `radius`, `prefab`, `x`, `z` | Place building pieces |
| `carpet` | `side`, `spacing`, `sink` | Lay a floor grid; `"at": "player"` puts it under the real player |
| `probes` | `count`, `every` | Items whose owner changes every `every` seconds |
| `drop` | `count`, `item`, `owner`, `radius` | Items handed to one player, timed until picked up |
| `churn` | `perSecond` | Changes to base pieces per second |
| `set` | `budgetKB`, `rateKB`, `fair`, `fps`, `orderCheck` | Change the door's settings while it runs |
| `label` | `text` | Name the following report windows |
| `report` | `every` | Seconds per report window |
| `inbed` | `count` | Put that many simulated players to bed |
| `status`, `dropinfo`, `clear`, `kick`, `quit` | | Show state; show who owns the dropped items; remove what the rig placed; kick a player by name; stop the server |

`set` writes the door's config file. Put the values back to the defaults when a run is over, or the next start uses
what the last run left there.
