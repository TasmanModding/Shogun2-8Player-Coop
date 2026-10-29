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
| **Desync recovery** | If a campaign goes out of sync, nobody is dropped: the resync starts by itself (as soon as everyone is back on the campaign map, e.g. after a battle) and every player joins it. The host waits until **every** player is ready before sending its copy of the campaign (the original game only waited for one other player). |
| **Host resync key: Ctrl+Shift+F9** | If the campaign looks wrong or is stuck (one PC stuck on an AI's turn while another shows a player's turn, armies in different places), the **host** presses **Ctrl+Shift+F9** on the campaign map. Everyone gets the host's copy of the campaign, the same way a detected desync is fixed. Wait for the resync popup to finish. If a resync got stuck, the key cancels it on every PC and starts a fresh one. **It only works on the campaign map**, not in the lobby, a battle or a loading screen. You hear a "ding" when the resync starts and an error sound when it can't (the reason is written to `eightplayer_log.txt`). |
| **Bug reports: `Report.bat`** | Double-click `Report.bat` after a problem: it writes **one text file**, `Shogun2-8Player-Report.txt`, to your Desktop with everything needed (MATCH CODE, other mods, the mod's event log, crash and error files, desync files, the game's logs). Attach that file to your report. |
| **Event log** | The game folder gets `eightplayer_log.txt`: a timestamped line whenever the multiplayer game changes state (players joining, battle loading and ending, the "waiting for players" screen, desyncs, resyncs, Ctrl+Shift+F9). It shows where a game got stuck. `Report.bat` includes it. |
| **Crash reports** | If the game crashes during a multiplayer game, it writes `eightplayer_crash.txt` (a few KB) in the game folder instead of Steam's 1–2 GB dump. If it closes itself instead, the reason is in `eightplayer_errors.txt`. `Report.bat` puts both into the report. |
| **Battle desync: no more kick** | A battle can't be resynced, so the game used to drop the out-of-sync player. Now the battle carries on for everyone. The out-of-sync player's view may drift (a unit alive on their screen that is dead for others). Back on the campaign map, the host's state is copied to everyone by the campaign resync. |

## Community
Join the **Tasman Modding Discord** for help, finding players, bug reports and updates: https://discord.gg/vt4JbH79Np

## Install / uninstall
1. Everyone closes Shogun 2.
2. Double-click **`Install.bat`**. It finds the game in your Steam folders. If yours is somewhere else, run
   `powershell -ExecutionPolicy Bypass -File install.ps1 -GamePath "D:\...\Total War SHOGUN 2"`.
3. Start Shogun 2 from Steam and host a multiplayer campaign.

**Updating:** every new version is published on [GitHub](https://github.com/TasmanModding/Shogun2-8Player-Coop/releases/latest). Steam Workshop can only share `.pack` files, so it can't update the engine part of the mod for you. Download the newest zip, unzip it and run `Install.bat`: it replaces the old version. New versions are announced in the [Discord](https://discord.gg/vt4JbH79Np). Update together with your friends, since everyone needs the same version.

**`Uninstall.bat`** puts everything back. Steam's "Verify integrity of game files" also restores the original DLL.

At the end the installer prints a **MATCH CODE** (8 letters/digits). Everyone compares it: the same code means the same game files, including any other mods. A different code means someone has a different mod enabled, which causes desyncs; the installer also warns about other enabled mods.

Everyone must also end up with the same two hashes, or they can't join each other. The installer prints them:

| File | SHA-256 |
|---|---|
| `empire.retail.dll` | `E2451F4A023EC7798FBAA38DC418279241747AFEAAE9439F5003CA9091F8CB5C` |
| `data\eightplayer_coop.pack` | `983F63E02958AF253B911934680AEE736CED160B2BB9FC152023EBCF479853F5` |

## Before you play
- **About VAC (anti-cheat):** Steam no longer lists Valve Anti-Cheat for Shogun 2. The VAC label was added to the store page on 29 April 2026 and removed again on 21 July 2026. That's good news, but this mod still changes the game's engine file, so stay sensible: play with friends, and use a spare account if you want to be extra safe.
- It only works on the current Steam version of the game; the installer refuses anything else.
- Start with the main Sengoku Jidai campaign. Rise of the Samurai and Fall of the Samurai have special clan rules in the lobby.
- To give desyncs fewer chances: everyone has **no other mods** enabled and the **same game version**.
  If one still happens, every player runs `Report.bat` and sends the text file it puts on the Desktop.
- **Big overhaul mods** (Expanded Japan, MOSS/MUST, unit and AI packs...) make desyncs much more likely, above all
  when a battle starts. Every player must have exactly the same ones (same MATCH CODE). If battles hang on
  "waiting for other players", try the same battle with only this mod enabled.

## Head-to-head vs co-op
- **Co-op:** every human player is on one team. Humans can't attack each other and share the co-op rules; AI clans
  are never on the team (ally with them or break alliances normally).
- **Head-to-head:** there are no fixed teams: every human is their own clan, like an AI clan. Not 4v4: humans make
  and break alliances through diplomacy, and any two humans can fight. In a battle, the two sides are the armies
  involved; every human who isn't in it can **watch** it, or **play as the AI army** if the other side is AI
  (the first player who picks that gets it). The others wait until it ends.
- **Gifting units in battle** is meant for co-op: the player fighting gives units to the players watching, so they
  can join in. In head-to-head the game doesn't stop either side gifting units to watchers yet, so agree on house
  rules (e.g. no gifting in human-vs-human battles).

## If something goes wrong
| Problem | Fix |
|---|---|
| "Out of sync" popup on the map | Nothing to do: the resync runs by itself. |
| Stuck turn, or players see different things on the map | Host presses **Ctrl+Shift+F9** on the campaign map (listen for the "ding"). |
| Desync during a battle | Keep playing; when you're back on the map the resync fixes it (host can press Ctrl+Shift+F9 to be sure). |
| Battle never loads ("waiting for other players") | Everyone quits; the host resumes the newest save (Resume as Host) and everyone rejoins. Then everyone runs `Report.bat` and sends the report: this is being investigated. |
| Can't join, or desyncs straight away | Compare MATCH CODEs; turn off other mods until they're the same. |
| A game crashed | Everyone quits; the host loads the newest save and sends it to the others (only the host saves). Everyone runs `Report.bat` and sends the text file from the Desktop. |

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
21. 3+ players: player 1 attacks an AI army; a player not in the battle picks **Play as AI**: they command the AI army, the others watch.
22. Host presses Ctrl+Shift+F9 on the campaign map: a "ding", and the resync runs. In the lobby: an error sound, nothing else.
23. Everyone runs `Report.bat`: a text file appears on the Desktop with the MATCH CODE and the event log.

## v1.14: Ctrl+Shift+F9 and automatic resync now actually run on the campaign map
**Everyone update.** The v1.13 event logs you sent showed the real problem:
- **The mod's every-frame code never ran on the campaign map or in the lobby.** It was attached to a spot in the game that only runs in one other mode. So since v1.9, **Ctrl+Shift+F9 did nothing** (no ding), the **automatic resync never started** after a desync, the host's "wait for every player" timer never ran, missing players' clans weren't handed to the AI, and the crash reporter wasn't active on the map. It now runs from the multiplayer session's own update, which runs on every screen.
- **Play as AI with 2 players works exactly like the unmodded game again.** Several of you reported that games only broke after someone chose "Play as AI". With exactly 2 players the mod now uses the game's original rule unchanged; the 3+ player handling only applies with 3 or more.
- **The "DESYNC reported by another player" lines in v1.13 logs were not desyncs.** The game checks sync about 3 times a second, even with one player alone in a lobby, and v1.13 wrote that line after every check. v1.14 logs a desync only when the check really finds a player out of sync (at most once every 10 seconds, with a count), and logs the number of out-of-sync players whenever it changes.
- **Ctrl+Shift+F9 and the automatic resync only run with a campaign loaded and no battle running.** The game's own resync start doesn't check for this, and v1.14's code now runs in the lobby too.
- **Event log:** when a resync can't start, the log says why; each PC logs its role in a battle (who plays the AI army), so a mismatch between PCs shows up.
- **`Report.bat` is much more thorough:** it asks a few questions (host? how many players? Play as AI?), starts with an automatic summary of what went wrong, and adds the PC details that matter for desyncs (Windows number format, CPU, overlays such as Discord or MSI Afterburner), every game pack, the game's multiplayer settings and recent saves.

## v1.13: Play as AI, Ctrl+Shift+F9 feedback, one-file bug reports
- **Play as AI works with 3+ players.** The game only understood "Play as AI" with exactly 2 players. With 3+ players the player who chose it never got the AI army (and in the unmodded game the game closed). Now the first player who chooses Play as AI controls the AI army, and everyone else watching follows the battle as usual.
- **Ctrl+Shift+F9 tells you what it did:** a "ding" when the resync starts, an error sound when it can't (lobby, battle, loading screen, or a client pressed it). The reason goes into `eightplayer_log.txt`.
- **`Report.bat`:** one plain-text file on your Desktop with everything a bug report needs. No more hunting for log folders.
- **Event log** (`eightplayer_log.txt`): shows step by step what the game did before something went wrong, e.g. where a battle got stuck loading.
- **Head-to-head explained** (see "Head-to-head vs co-op" above).

**Being investigated, please report with `Report.bat`:**
- Battles that never load (black screen, "waiting for other players") and crashes on the way back from a battle. Send every player's `Report.bat` file from v1.14.
- With 3+ players: no weather choice before a battle, and no markers on the campaign map. Please tell us what you expected to see and where (screenshot from a 2-player game helps).
- A player who leaves while a popup of theirs is open (diplomacy offer, bankruptcy) can block everyone after the AI takes their clan.

## v1.12: resync fixed for 3+ players
- **Resync reaches everyone.** The game's resync was built for 2 players: the host sent its copy of the campaign as soon as *one* other player was ready. With 3+ players, anyone not ready yet missed it and stayed on the old campaign, so turns got stuck for good. Now the host waits until every player is ready (or about a minute, if one PC never answers).
- **Ctrl+Shift+F9 always works.** A half-finished resync on any PC used to block every later one, the host's key included. Now the key cancels a stuck resync on every PC and starts a fresh one.

**Already stuck on v1.11?** Everyone quits, the host resumes the newest save (Resume as Host) and everyone rejoins. Then update to v1.12.

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

If something goes wrong, report it in the [Discord](https://discord.gg/vt4JbH79Np), [open an issue](https://github.com/TasmanModding/Shogun2-8Player-Coop/issues/new?template=crash_report.md) or comment on the Steam Workshop page, with: how many players, co-op or head-to-head, who was in the battle, what happened on whose screen and when, and **every player's** `Shogun2-8Player-Report.txt` from `Report.bat`.
## Known limitations - please report what you see
The game was built for 2 players, so with 3–8 players some parts haven't been tested in real games yet:
- Other 2-player limits may remain in parts not yet played: turn order, reinforcements joining battles, the end of a 3+ player campaign, diplomacy between humans in head-to-head.
- In head-to-head, a player fighting a battle may be able to gift units to a player watching it.
- Watchers in head-to-head see the game's default battle result screen (nobody is allied).
- Resumed games can be joined by anyone who finds them: use a password or friends-only if you don't want strangers taking a missing player's clan.
- When a save is resumed with someone missing, their clan is still human for a few seconds after the map appears, until the AI takes over. Same when a player returns: their clan is AI for a few seconds.
- New players get missing players' clans in join order; there's no way to pick which one yet.
- If someone drops out of a campaign that has never been resumed, the AI takes their clan but it isn't reserved: they can't reclaim it on the next rehost.
- Campaigns started on v1.6 can keep wrong co-op settings for AI clans: start a new campaign.
- More players means more chances of a desync, and the game always waits for the slowest connection. The automatic resync and the host's Ctrl+Shift+F9 key help.

**If the game closes instantly with no error:** that isn't a crash, so no `eightplayer_crash.txt` is written: the game found a problem and closed itself. Since v1.8 the reason is saved in `eightplayer_errors.txt` in the game folder. Run `Report.bat` and report it with what happened, on whose screen and when ([Discord](https://discord.gg/vt4JbH79Np) or [GitHub issue](https://github.com/TasmanModding/Shogun2-8Player-Coop/issues/new?template=crash_report.md)).
## Not done yet
- **Battle desyncs** no longer drop anyone, but the battle itself can't be resynced: the out-of-sync player's view may differ until the campaign map, where the host's state wins.
- Two known causes are fixed (above); others may remain. Possible: Intel and AMD CPUs can give slightly different
  results for sine/cosine instructions the battle code uses. If a desync still happens, send every player's `logs` folder.

## Files
| Path | What |
|---|---|
| `Install.bat`, `Uninstall.bat`, `install.ps1` | installer / uninstaller |
| `Report.bat`, `report.ps1` | writes the one-file bug report to the Desktop (reads files only) |
| `lib\MatchCode.ps1` | MATCH CODE and game-folder search (used by the installer and the report) |
| `build_lobby_layout.ps1`, `build_load_layout.ps1` | build the 8-slot lobby and the 8-player Load screen from your own copy of the game |
| `lib\Patcher.cs` | builds the patched engine DLL (checks the original first) |
| `lib\LayoutTool.cs`, `lib\PackTools.cs`, `lib\PackWriter.cs` | read/write UI layouts and `.pack` files |
| `verify_load.ps1` | loads a patched DLL with Windows' loader (no code runs) and checks every patch |
| `preview\` | schematic of the lobby (boxes only, no game artwork) |
| `NOTES.md` | reverse-engineering notes |

**Download:** get the zip from the [Releases page](https://github.com/TasmanModding/Shogun2-8Player-Coop/releases/latest). It contains no game files: the installer builds the mod from your own copy of the game.
