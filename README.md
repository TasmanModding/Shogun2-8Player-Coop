# Shogun 2: 8-player co-op campaign (experimental)
**by Tasman Modding**

![8-slot lobby layout](preview/lobby_8player.png)

Builds on the 4-player campaign mod. Installing this one removes the 4-player mod first.

## What it does
| | |
|---|---|
| **Up to 8 players** | The multiplayer campaign lobby has 8 player slots (two rows, 4 each side of the map) and the host session accepts 8 players. 8 is the engine's limit: its multiplayer battles are built for at most 8 (4v4). |
| **Player count** | The lobby title shows how many players have joined, e.g. "Eight Player Campaign (3/8)". The Campaign Games list shows each game's players under its status, e.g. "Open" / "3/8". |
| **Resume with different players** | A saved campaign no longer needs exactly the same players. Players from the save get their own clan back; anyone new takes over the clan of a missing player (in the order they joined). Anyone can host a save under "Resume as Host", so the campaign carries on if the original host can't make it. |
| **Missing players: AI takes over** | Resume a save with someone missing and the AI plays their clan. Their clan stays reserved for them in every save, and when they join a later rehost they get it back. |
| **Save names** | With 3+ players, saves are named after everyone, e.g. "Ann, Bob, Cat, Dan: Summer 1550", instead of only two players. |
| **Co-op team for every player** | In co-op, every human player is on the co-op team (can't attack each other, shared co-op rules), not just players 1 and 2. AI clans are never on it: you can ally with them, or break alliances, normally. |
| **Battles with 3–8 players** | Players who aren't in a battle follow a player who is (as in the 2-player game) instead of the game closing. Works in co-op and in head-to-head, where any two players can fight each other. Leaving a battle early no longer drops you from the campaign. |
| **Load screen shows every player** | Selecting a multiplayer save lists all its players (clan and name), four down each side of the map, instead of only two. |
| **Kick and invite buttons** | Every slot's kick button works and kicks that player, and every empty slot's invite button opens the friends list (the host sees them; the original game only wired one). The lobby is titled "Eight Player Campaign". |
| **Battle result for allies** | Fixed: players who weren't in a battle were shown the *defender's* result. When your side attacked and won, your allies saw "defeat"; when it defended, they saw "victory". Now they see the result of the ally who fought. |
| **Gift units to spectators** | Each press of the gift button sends the selected units to the **next spectator in turn** (in join order); only that player can command them, and they can hand units back. (The original game only handled one spectator: units went to the first, everyone could command them, or they were lost.) |
| **Desync: save screen bug** | Opening the Save or Load screen switched that PC's maths and text rules to its Windows language for the rest of the session (e.g. decimal comma on German, Czech, French or Russian Windows), so it no longer matched the other players. This is why "only the host should save" helped. Fixed: those screens now only change how dates are shown. |
| **Desync: floating-point drift** | The game never locked the CPU's floating-point settings, so anything that changed them on one PC (overlays, anti-virus, drivers, alt-tabbing) made that PC calculate slightly differently. Fixed: the settings are reset to the game's defaults every frame and every battle tick. |
| **Desync recovery** | If a campaign goes out of sync, nobody is dropped: the resync starts by itself (as soon as everyone is back on the campaign map, e.g. after a battle) and every player joins it. |
| **Host resync key: Ctrl+Shift+F9** | If the campaign looks wrong or is stuck (one PC stuck on an AI's turn while another shows a player's turn, armies in different places), the **host** presses **Ctrl+Shift+F9** on the campaign map. Everyone gets the host's copy of the campaign, the same way a detected desync is fixed. Wait for the resync popup to finish. It does nothing in battles or the menus, and nothing if a client presses it. |
| **Crash reports** | If the game crashes during a multiplayer game, it writes `eightplayer_crash.txt` (a few KB) and `eightplayer_crash.dmp` (a small dump, well under 1 MB) in the game folder, instead of Steam's 1–2 GB dump. If it closes itself instead, the reason is in `eightplayer_errors.txt`. Send these with your bug report. |
| **Battle desync: no more kick** | A battle can't be resynced, so the game used to drop the out-of-sync player. Now the battle carries on for everyone. The out-of-sync player's view may drift (a unit alive on their screen that is dead for others). Back on the campaign map, the host's state is copied to everyone by the campaign resync. |

## Community
Join the **Tasman Modding Discord** for help, finding players, bug reports and updates: https://discord.gg/medh2d8nk

## Install / uninstall
1. Everyone closes Shogun 2.
2. Double-click **`Install.bat`**. It finds the game in your Steam folders. If yours is somewhere else, run
   `powershell -ExecutionPolicy Bypass -File install.ps1 -GamePath "D:\...\Total War SHOGUN 2"`.
3. Start Shogun 2 from Steam and host a multiplayer campaign.

**Updating:** every new version is published on [GitHub](https://github.com/TasmanModding/Shogun2-8Player-Coop/releases/latest). Steam Workshop can only share `.pack` files, so it can't update the engine part of the mod for you. Download the newest zip, unzip it and run `Install.bat`: it replaces the old version. New versions are announced in the [Discord](https://discord.gg/medh2d8nk). Update together with your friends, since everyone needs the same version.

**`Uninstall.bat`** puts everything back. Steam's "Verify integrity of game files" also restores the original DLL.

At the end the installer prints a **MATCH CODE** (8 letters/digits). Everyone compares it: the same code means the same game files, including any other mods. A different code means someone has a different mod enabled, which causes desyncs; the installer also warns about other enabled mods.

Everyone must also end up with the same two hashes, or they can't join each other. The installer prints them:

| File | SHA-256 |
|---|---|
| `empire.retail.dll` | `971C7A955B045433F54161126A6866B3F37541A90CB23B2F3C88DB9A8ECD2280` |
| `data\eightplayer_coop.pack` | `983F63E02958AF253B911934680AEE736CED160B2BB9FC152023EBCF479853F5` |

## Before you play
- **About VAC (anti-cheat):** Steam no longer lists Valve Anti-Cheat for Shogun 2. The VAC label was added to the store page on 29 April 2026 and removed again on 21 July 2026. That's good news, but this mod still changes the game's engine file, so stay sensible: play with friends, and use a spare account if you want to be extra safe.
- It only works on the current Steam version of the game; the installer refuses anything else.
- Start with the main Sengoku Jidai campaign. Rise of the Samurai and Fall of the Samurai have special clan rules in the lobby.
- To give desyncs fewer chances: everyone has **no other mods** enabled and the **same game version**.
  If one still happens, copy everyone's `logs` folder: that shows exactly what drifted.

## If something goes wrong
| Problem | Fix |
|---|---|
| "Out of sync" popup on the map | Nothing to do: the resync runs by itself. |
| Stuck turn, or players see different things on the map | Host presses **Ctrl+Shift+F9** on the campaign map. |
| Desync during a battle | Keep playing; when you're back on the map the resync fixes it (host can press Ctrl+Shift+F9 to be sure). |
| Can't join, or desyncs straight away | Compare MATCH CODEs; turn off other mods until they're the same. |
| A game crashed | Everyone quits; the host loads the newest save and sends it to the others (only the host saves). |

## Test plan
Please report the step where it breaks, what you saw, and copy the newest files from the game's `logs` folder.
1. Host alone: the lobby shows 8 slots (two rows of four).
2. Players 2–8 join: each lands in their own slot with name and clan. The host's kick button on a slot kicks that player.
3. Everyone picks a different clan and readies up; the campaign starts for everyone, and every human clan is human-controlled.
4. Play a few turns: the turn passes through every human.
5. One human fights a battle and the others watch. When it ends, **everyone** sees the same result.
6. In that battle, with 2+ spectators: select units and press gift. The first press goes to spectator 1, the next press to spectator 2, and so on. A spectator presses gift on units they hold to hand them back.
7. If "out of sync" appears on the campaign map: the resync should start on its own and the game carry on afterwards. In a battle: nobody is dropped and the battle carries on.
8. Save, quit, resume.
9. Player 3 (or higher) attacks an AI army. The players not in the battle stay in the game and follow the battle.
10. Head-to-head, 3+ players: player 1 attacks player 3 (a field battle, then a siege). The others stay in the game.
11. Co-op: in Diplomacy only the human players are co-op allies; you can make and break alliances with AI clans. Player 1 can't attack player 3's army.
12. A non-host player retreats from a battle (or leaves it early): they stay in the campaign.
13. Load a 3+ player save: the Load screen lists every player.
14. On the campaign map the host presses Ctrl+Shift+F9: the resync popup appears for everyone and the game carries on afterwards.
15. Multiplayer → Campaign Games: an open game shows its player count (e.g. "3/8") under "Open".
16. Resume a 3+ player save with one player swapped for someone new: the new player gets the missing player's clan.
17. Resume a save with a different host (not the original one): it works.
18. Resume a save with one player missing: a few seconds after the map appears, their clan is AI-controlled. Play a turn and save.
19. Rehost that save with the missing player back: they get their own clan back and control it again.
20. Save a 3+ player game: the save is named after every player.

## v1.11: please test and report
**New in v1.11:**
- **Player counts in the Campaign Games list.** Each game shows how many players are in it, e.g. "Open" with "3/8" underneath.
- **Resume a save with different players.** Players from the save get their own clan; anyone new takes over a missing player's clan. Anyone can host the save.
- **Missing players: AI takes over.** If someone can't make it, the AI plays their clan. Their clan stays reserved for them, and they get it back when they join a later rehost.
- **Save names list everyone** with 3+ players.

Please test steps 15–20 of the test plan above and tell us what happens.

**Fixed in v1.10:** head-to-head crash for players not in a battle; small crash logs instead of Steam's 1–2 GB dumps.
**Fixed in v1.9:** nobody is dropped for a desync during a campaign; the resync starts by itself once everyone is back on the map.
**Fixed in v1.8:** gifted units belong only to the player they were given to; the reason for an instant close is saved in `eightplayer_errors.txt`.
**Fixed in v1.7:** players outside a co-op battle follow it; AI clans are no longer co-op allies; leaving a battle early keeps you in the campaign.

If something goes wrong, report it in the [Discord](https://discord.gg/medh2d8nk), [open an issue](https://github.com/TasmanModding/Shogun2-8Player-Coop/issues/new?template=crash_report.md) or comment on the Steam Workshop page, with: how many players, co-op or head-to-head, who was in the battle, what happened on whose screen and when, your MATCH CODE, and any `eightplayer_crash.txt`, `eightplayer_crash.dmp` or `eightplayer_errors.txt` from the game folder.
## Known limitations - please report what you see
The game was built for 2 players, so with 3–8 players some parts haven't been tested in real games yet:
- Other 2-player limits may remain in parts not yet played: turn order, reinforcements joining battles, the end of a 3+ player campaign, diplomacy between humans in head-to-head.
- With 3+ players, several players not in a battle could choose "play as AI" for the same AI army.
- In head-to-head, a player fighting a battle may be able to gift units to a player watching it.
- Watchers in head-to-head see the game's default battle result screen (nobody is allied).
- Resumed games can be joined by anyone who finds them: use a password or friends-only if you don't want strangers taking a missing player's clan.
- When a save is resumed with someone missing, their clan is still human for a few seconds after the map appears, until the AI takes over. Same when a player returns: their clan is AI for a few seconds.
- New players get missing players' clans in join order; there's no way to pick which one yet.
- If someone drops out of a campaign that has never been resumed, the AI takes their clan but it isn't reserved: they can't reclaim it on the next rehost.
- Campaigns started on v1.6 can keep wrong co-op settings for AI clans: start a new campaign.
- More players means more chances of a desync, and the game always waits for the slowest connection. The automatic resync and the host's Ctrl+Shift+F9 key help.

**If the game closes instantly with no error:** that isn't a crash, so no `eightplayer_crash.txt` is written: the game found a problem and closed itself. Since v1.8 the reason is saved in `eightplayer_errors.txt` in the game folder. Please report it with what happened, on whose screen and when ([Discord](https://discord.gg/medh2d8nk) or [GitHub issue](https://github.com/TasmanModding/Shogun2-8Player-Coop/issues/new?template=crash_report.md)).
## Not done yet
- **Battle desyncs** no longer drop anyone, but the battle itself can't be resynced: the out-of-sync player's view may differ until the campaign map, where the host's state wins.
- Two known causes are fixed (above); others may remain. Possible: Intel and AMD CPUs can give slightly different
  results for sine/cosine instructions the battle code uses. If a desync still happens, send every player's `logs` folder.

## Files
| Path | What |
|---|---|
| `Install.bat`, `Uninstall.bat`, `install.ps1` | installer / uninstaller |
| `build_lobby_layout.ps1`, `build_load_layout.ps1` | build the 8-slot lobby and the 8-player Load screen from your own copy of the game |
| `lib\Patcher.cs` | builds the patched engine DLL (checks the original first) |
| `lib\LayoutTool.cs`, `lib\PackTools.cs`, `lib\PackWriter.cs` | read/write UI layouts and `.pack` files |
| `verify_load.ps1` | loads a patched DLL with Windows' loader (no code runs) and checks every patch |
| `preview\` | schematic of the lobby (boxes only, no game artwork) |
| `NOTES.md` | reverse-engineering notes |

**Download:** get the zip from the [Releases page](https://github.com/TasmanModding/Shogun2-8Player-Coop/releases/latest). It contains no game files: the installer builds the mod from your own copy of the game.
