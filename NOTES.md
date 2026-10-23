# Shogun 2 8-player co-op: reverse-engineering notes

Base: `empire.retail.dll` sha256 `022FAE2E…74F5` (current Steam build), image base 0x10000000, 32-bit.
Tools: Ghidra 12.1.4 headless (`re/ghidra_proj`; `re/q.ps1` runs `re/query.txt` through `re/ghidra_scripts/Query.java`:
`decomp`, `refs`, `callers`, `strrefs`, `asm`), capstone helper `re/re.py`, unluac for the UI's compiled Lua
(Lua 5.1, 4-byte float numbers) in `re/ui_src`.

## Patches (lib/Patcher.cs)
| Patch | Site | New code |
|---|---|---|
| Lobby binds `panel_player1`..`8` + per-panel kick | 11029CBA, 10FD6350, 10FD6681 | .text slack 115A0C40..115A0DF8 (full; 8 bytes left) |
| Session capacity 8 | 10FD2840 `push 2` -> `push 8` (ctor 10EC3050 stores it at +30h, no clamp) | - |
| Battle result for non-participants | 11107F30 (12 bytes) jmp | `.s2mp` + 0 |
| Gift recipient with several spectators | 110E50FA call retarget | `.s2mp` + 9F; counter dword 11CB1530 (.data VirtualSize grown by 8) |
| Auto-accept campaign resync | 10F05914 call retarget | `.s2mp` + 129 |
| Save/load screens set only LC_TIME | 110B84E1 `push 0` -> `push 5` before _wsetlocale | - |
| Pin x87 CW 027F + MXCSR 1F80 | 100B190C call retarget (frame), 10227D40 (8 bytes, battle tick) jmp | `.s2mp` + 253, + 272 |
| No desync kick during battles (campaign unchanged) | 10EE9A71 (6 bytes, edi = session+4, test [edi+596CCh]), 10EF9A87 (6 bytes, ebx = session, test [ebx+596D0h]) jmp | `.s2mp` + 299, + 2B6 |
| Host resync key Ctrl+Shift+F9 (frame hook polls GetAsyncKeyState via IAT 115A1558, prev-state dword 11CB1534); 0xAC value 2 = force | frame hook + 10F10E2D call retarget (FUN_10efa140) | `.s2mp` frame stub, forced-answer stub |
| Crash reporter: SetUnhandledExceptionFilter once from the frame hook; writes eightplayer_crash.txt (code, address, DLL base, regs, 64 stack dwords), then chains to the previous filter; prev/installed dwords 11CB1538/11CB153C | frame hook | `.s2mp` before the frame stub |
| Lobby title "(N/8)": FUN_10fd6650 entry (member update; [ecx-50h] root, [ecx+34h] session), count FUN_10f0eb00(session+58EF4h), text via FUN_100ed420 + SetText FUN_10f9d010 | 10FD6650 (6 bytes) jmp | `.s2mp` |
| Load screen players 3..8: after portrait_player2 in FUN_10fc8dd0; header count [h+30h], you [h+31h], names [h+3Ch], clan keys [h+48h] (0Ch each); clan name via [[11BC095C]+18h]->4Ch, +10h -> FUN_10bce670 -> FUN_10308d20(key), [[..+10h]+D38h]->8(rec+28h,1) | 10FC93B7 (5 bytes) jmp; layout build_load_layout.ps1 | `.s2mp` |
| Co-op team: FUN_1068e0e0 adds clans whose human record [clan+6D8h] has campaign type [rec+74h] 5/6 (FUN_105cb5b0); a non-co-op human record gets the co-op type when another human's is co-op | 1068E124 call retarget | `.s2mp` |
| Invite buttons on slots 3..8 (frames invite_frame3..8) | 10FD5B70 (7 bytes) jmp; original wires only root.FindChild("button_invite") at 10FD5B36 | `.s2mp` + 13F |

`.s2mp` is a new RX section appended after `.reloc` (one free header slot existed). Its code uses only relative
calls/jumps, and no relocation covers a patched byte (`verify_load.ps1` checks under a randomised base).

## Battle results popup (campaign map)
- Lua binding 11134800 -> FUN_11107ee0(info, &ui+A98h, local faction, battle results (+90h), pending battle)
  -> FUN_11110fb0 builds the popup table. `info[1]` = "local is NOT attacker"; 11110fb0 picks
  our/their side results from it.
- FUN_105b7b90(battle; faction, bool* is_attacker): 105b7ab0 looks the faction up in the attacker side (+60h) then the defender
  side (+64h). If neither, a fallback walks a faction-name list at +80h/+7Ch and keeps the LAST match's side.
  A non-participant therefore got is_attacker = 0 (defender view) or an arbitrary faction's view.
- Alliance test used: FUN_105ca920 (thiscall faction; other) OR FUN_1068d240 (cdecl a, b: both in the co-op
  faction list at [factions+144h], count +140h). FUN_105ca920: same faction, or diplomatic state 2 (alliance), or
  107ef860/107ef870 (vassal relations); rebels never.
- Faction list: [[[ui+A98h]]+8]+1498h -> +44h begin, +40h count; faction +6A0h = human.
- The popup is shown only if FUN_10602ac0 says the faction is involved (FUN_105aeb70, again via 105b7b90).
- In-battle results (FUN_110fd9b0) use alliances[local alliance].result (stride 60h, +58h). Spectators
  (setup+281E8h) skip that popup. Result enum 8..16 = close/decisive/heroic/pyrrhic victory, -, close/decisive/crushing/valiant defeat.

## Unit gifting in battle
- HUD button `button_share_unit` (class BattleButtonShareUnit, vtable 11726604); click -> FUN_110e5090.
- Roles: setup+281E9h decides which of (FUN_110de530 = own player record, FUN_110de560 = first of
  FUN_10ef3150's list) is the source and which the recipient. FUN_10ef3150(session) lists players matching
  FUN_10f00c60 (state flags; spectating players).
- FUN_10ef9520(session; recipient, unit) sends net message 0xAA; only units the LOCAL player holds are accepted.
- BCQ_UNIT_CHANGE_CONTROL_STATUS (handler 102aadd0, unit+EA8h 0/1/2) is the AI-control toggle, not gifting.

## Desync
- Lockstep with per-tick checksums. Desync handler 10EE9A50 (vtable 116D6A74):
  1. FUN_10f058d0: in campaign state (FUN_10ef1b40()==3) and when a session vfunc +84h says no, mark resync pending
     ([session+2BE78h]+30h = 1) and notify listeners -> popup `mp_campaign_desync` (FUN_111540a0).
  2. If `mp_desync_kick_enabled` (byte 117B7A91, default 1) and no resync pending: disconnect (FUN_10f033e0(3)).
     FUN_10ef9a20 (a peer's desync report counter [conn+18DCh] changed): host kicks the reported players (10f02c10(p,2)),
     a client leaves if it is the reported one. The mod skips both kicks while [session+596D0h] (battle env) is set.
     Session layout: +596D0h battle env, +596D8h campaign env; the handler's ecx is session+4.
  3. Log `mp_battle_desync` / `mp_campaign_desync` / `mp_no_env_desync`; 10F4ABD0 = telemetry XML only.
- Popup OK (FUN_11103d10) -> FUN_10f2d4b0(resync; session, 1) -> message 0xAC. The mod now does this automatically.
- Joining compares the host's 117B7A91 (kick) and 11B8B2AB (`mp_network_sync_logging`) with the joiner's (FUN_10ee1820):
  all players must use the same values.
- `output_sync_logs` -> byte 1192A847 (battle sync logs). Both logging flags are off by default (BSS).
- Out of turn (Lua `CampaignUI.MultiplayerPlayerOutOfTurn`, FUN_11142f20): MP campaign, current faction human, not local.
- All local commands start with FUN_101cc200(writer, queue+4, id, flag) and send with FUN_101d0d40; campaign command ids
  are globals (e.g. tax rate 11AD4F20). Most state-changing UI actions are native C++ handlers, not Lua.

### Leaving a multiplayer battle
- End/quit battle (FUN_10307bc0): if this PC's battle is finished (FUN_101823e0), session+8 vfunc 78h FUN_10eeb2e0 broadcasts
  message 0xA9(1); otherwise vfunc 7Ch FUN_10f1f8d0 (client mid-battle: disconnect) and BCQ_FACTION_QUIT_BATTLE.
- 0xA9 received (FUN_10f1c6c0): start [session+597E0h] = timeGetTime. FUN_10f3a570 (per update) counts down 10 s to the
  listeners, then sets [session+59785h] = 1, which session+8 vfunc 8 (FUN_10f003c0) reports to the battle as "leave now".
  So once ANY player finishes and clicks continue, every other PC leaves the battle 10 s later, finished or not:
  a desynced player whose battle never ends is pulled out with everyone (why the battle no-kick patch can't strand anyone).

### Resync protocol
- State [resync+30h]: 0 idle, 1 pending (FUN_10f058d0), 2 local accepted, 3 remote accepted, 4 both -> FUN_10f34f00:
  host saves "resync" (106f8bc0), reads the file and sends it (10edbb20, 200000-byte chunks) -> 7 -> 10; client 6 -> 8 (received) -> loads (10bd5c60) -> 9 -> 0Bh.
- Message 0xAC (FUN_10f27820(session; value), broadcast incl. self) -> 10F10E2D FUN_10efa140(resync; session, sender==self, value):
  ignored unless state 1..3; value 0 = decline -> disconnect. FUN_10f2d4b0 only sends in state 1 or 3.

### Desync causes found in the engine
- **Locale leak** (fixed): FUN_110b83f0 (save list 10FAE6D0, load list 10FC7E90, save dialog 111C1370, 11076E6E) does
  _wsetlocale(LC_ALL, user locale); only FUN_110b55d0 restores "C", and only for the owner token. The CRT stays in the
  Windows locale after Save/Load: LC_NUMERIC (strtod/sprintf, incl. Lua tonumber/tostring), LC_CTYPE (_stricmp,
  tolower: battle code 101c60b0/1020fcd0 use _stricmp). The other setlocale calls (101ebd60, 10f8c320) are LC_TIME only;
  Lua os.setlocale (11563AD5) is unused by the game's scripts.
- **FP modes not pinned** (fixed): no FLDCW/LDMXCSR except float->int helpers (restore previous) and CRT; battle tick
  103213d0 runs TBB tasks (TBB propagates the master's FP settings). D3D9 is only used to enumerate adapters (no
  CreateDevice), so DX9/DX11 choice doesn't change FP state itself.
- **Not fixed / candidates**: 1583 x87 transcendental instructions (fsin/fcos/fpatan/fyl2x/...) in ~750 functions
  (battle 101..104, campaign 105..108): results can differ between CPU vendors in the last bits. CRT rand() unused
  (game LCGs at 101278f0 etc. keep their own state). No rand/time in the simulation found.

### Forum-reported triggers (Steam / GameFAQs)
Acting during another player's turn; mismatched mods or DirectX; naval battles and auto-resolve; diplomacy prompts;
long campaigns; both players saving. No official root cause is known.

### Head-to-head with 3+ players (open)
- Reported (4-player mod, FotS H2H, no other mods): when two humans fight, the human not in the battle crashes to desktop; sieges work.
- Pre-battle panel FUN_111853c0 picks a handler by role: [+74h] / [+70h] = the two sides, [+78h] = third party (FUN_11167480:
  button_spectate_battle -> vfunc 74h, button_play_as_ai -> 78h, button_autoresolve -> 3Ch). In 2-player games the third-party
  role only ever went to an ally; in H2H with 3+ players it goes to someone allied to nobody. Prime suspect.
- None of this mod's hooks run on that path (only the FP pin). The game's own Crash.dmp path (1000AFE0 -> 117C7950) is built but
  never used, hence the mod's crash reporter.

## Next steps
- Get logs from a real desync (players' `logs` folder; enabling `mp_network_sync_logging` for everyone gives tick-level logs)
  to find which state drifts.
- Save NAME (FUN_106a3a30: "%S (%S) vs. %S (%S)") still names two players.
- Lobby victory conditions are game-wide (FUN_10fc4ba0 fills the first victory_conditions box from the campaign type), so players 3..8 need no box of their own.
