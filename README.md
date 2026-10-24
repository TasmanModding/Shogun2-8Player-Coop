# Shogun 2: 8-player co-op campaign (experimental)
**by Tasman Modding**

![8-slot lobby layout](preview/lobby_8player.png)

Builds on the 4-player campaign mod. Installing this one removes the 4-player mod first.

## What it does
| | |
|---|---|
| **Up to 8 players** | The multiplayer campaign lobby has 8 player slots (two rows, 4 each side of the map) and the host session accepts 8 players. 8 is the engine's limit: its multiplayer battles are built for at most 8 (4v4). |
| **Player count** | The lobby title shows how many players have joined, e.g. "Eight Player Campaign (3/8)". |
| **Co-op team for every player** | In co-op, every human player is on the co-op team (can't attack each other, shared co-op rules), not just players 1 and 2. AI clans are never on it: you can ally with them, or break alliances, normally. |
| **Battles with 3–8 players** | Players who aren't in a battle follow a player who is (as in the 2-player game) instead of the game closing. Works in co-op and in head-to-head, where any two players can fight each other. Leaving a battle early no longer drops you from the campaign. |
| **Load screen shows every player** | Selecting a multiplayer save lists all its players (clan and name), four down each side of the map, instead of only two. |
| **Kick and invite buttons** | Every slot's kick button works and kicks that player, and every empty slot's invite button opens the friends list (the host sees them; the original game only wired one). The lobby is titled "Eight Player Campaign". |
| **Battle result for allies** | Fixed: players who weren't in a battle were shown the *defender's* result. When your side attacked and won, your allies saw "defeat"; when it defended, they saw "victory". Now they see the result of the ally who fought. |
| **Gift units to spectators** | With more than one spectator, gifted units could only ever go to the first one, and other spectators couldn't hand units back. Now each press of the gift button sends the selected units to the **next spectator in turn** (in join order), and any spectator can hand units back. With one spectator nothing changes. |
| **Desync: save screen bug** | Opening the Save or Load screen switched that PC's maths and text rules to its Windows language for the rest of the session (e.g. decimal comma on German, Czech, French or Russian Windows), so it no longer matched the other players. This is why "only the host should save" helped. Fixed: those screens now only change how dates are shown. |
| **Desync: floating-point drift** | The game never locked the CPU's floating-point settings, so anything that changed them on one PC (overlays, anti-virus, drivers, alt-tabbing) made that PC calculate slightly differently. Fixed: the settings are reset to the game's defaults every frame and every battle tick. |
| **Desync recovery** | If a campaign still goes out of sync, the resync now starts by itself instead of waiting for someone to press OK (Cancel used to drop the player). |
| **Host resync key: Ctrl+Shift+F9** | If the campaign looks wrong or is stuck (one PC stuck on an AI's turn while another shows a player's turn, armies in different places), the **host** presses **Ctrl+Shift+F9** on the campaign map. Everyone gets the host's copy of the campaign, the same way a detected desync is fixed. Wait for the resync popup to finish. It does nothing in battles or the menus, and nothing if a client presses it. |
| **Crash reports** | If the game crashes, it writes `eightplayer_crash.txt` in the game folder (where the crash happened in the game's code). Send that file with your bug report: it lets the crash be fixed instead of guessed at. |
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
| `empire.retail.dll` | `24BBF6D225AB360F1BD47344C986DF97447B5176DE95A38FB9B34E55D3C32327` |
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

## Fixed in v1.7: please test and report
v1.7 fixes the 3+ player battle problems found in testing. These fixes are new, so please report how they go:
- **Players not in a battle no longer close.** With 3 or more players, everyone not fighting was put into a "spectator" mode the original 2-player game never used, and the game closed itself. They now follow a player who is in the battle, the same way the original 2-player game handles it. This fixes battles involving player 3+, and human-vs-human battles in **head-to-head**.
- **AI battles no longer close everyone's game, and co-op no longer allies you with every clan.** v1.6 put AI clans on the co-op team, which made them permanent allies (alliances couldn't be broken) and turned AI battles into multiplayer battles with no players. Only human players are on the co-op team now. **Start a new campaign:** saves made with v1.6 can still carry the wrong settings for AI clans.
- **Leaving a battle early no longer drops you from the campaign** (non-host players with 3+ players were disconnected).

If something still goes wrong, post in the [Discord](https://discord.gg/medh2d8nk), [open an issue](https://github.com/TasmanModding/Shogun2-8Player-Coop/issues/new?template=crash_report.md) or comment on the Steam Workshop page, and include:
- how many players, game mode (co-op or head-to-head), and who was in the battle;
- what happened and on whose screen (and when: battle announced, choosing attack/spectate, loading, during, after);
- the battle type (field, siege, naval, reinforcements);
- your MATCH CODE and any other mods enabled;
- `eightplayer_crash.txt` from the game folder, if there is one.
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
