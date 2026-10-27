// Builds a patched copy of empire.retail.dll for the 8-player MP campaign co-op mod
// (extends the 4-player mod's lobby patch from panels 3-4 to panels 3-8 and raises the session to 8 players).
// Never modifies its input: it reads the original, checks it is exactly the expected build, and writes a new file.
//
// Patch 1 (lobby binds 4 player panels instead of 2):
//   In the lobby's panel-binding routine (11029C20) the original code looks up "panel_player1" and
//   "panel_player2" by name. We keep both lookups' name pushes untouched (their absolute addresses are
//   fixed up by the loader under ASLR), replace one 5-byte CALL with a 5-byte JMP into new code placed in
//   the unused tail of .text, finish the panel_player2 lookup there exactly as before, then look up
//   "panel_player3".."panel_player8". The new code is position-independent (no relocations needed)
//   and skips any panel that isn't present, so without the new layout the lobby still has 2 panels.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

public static class EightPlayerPatcher {
	public const string OriginalSha256 = "022FAE2E2DA92B59B8B1B8EE52887003C624E3C8ECA060B209F5D792A7F874F5";
	const uint ImageBase = 0x10000000;
	public const int MaxPlayers = 8;   // the engine's MP battles already support 8 (4v4); the lobby layout has 8 panels
	static readonly string[] PanelNames = { "panel_player3", "panel_player4", "panel_player5", "panel_player6", "panel_player7", "panel_player8" };

	// engine functions used by the panel lookup (absolute VAs in the original image)
	const uint StrCtor = 0x100ECFA0;     // construct engine string from const char* (thiscall, 1 arg)
	const uint FindChild = 0x10F8CF70;   // find component by name (thiscall on root, 1 arg)
	const uint PushBack = 0x10098B40;    // vector push_back (thiscall, 1 arg)
	const uint StrDtor = 0x100A0B00;     // destroy engine string (thiscall)

	const uint HookAt = 0x11029CBA;      // CALL StrCtor inside the panel_player2 lookup
	const uint ResumeAt = 0x11029CE8;    // first instruction after the panel_player2 lookup

	static byte[] b; static int peOff, secTable, nSec;

	static int VaToOff(uint va) {
		uint rva = va - ImageBase;
		for (int i = 0; i < nSec; i++) {
			int s = secTable + i * 40;
			uint vaS = BitConverter.ToUInt32(b, s + 12), raw = BitConverter.ToUInt32(b, s + 16), rawOff = BitConverter.ToUInt32(b, s + 20);
			if (rva >= vaS && rva < vaS + raw) return (int)(rawOff + (rva - vaS));
		}
		throw new Exception(string.Format("VA {0:X8} not in any section's file data", va));
	}

	static string Sha(byte[] d) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(d)).Replace("-", ""); }

	class Asm {
		public uint Va; public List<byte> Bytes = new List<byte>();
		public uint Here { get { return Va + (uint)Bytes.Count; } }
		public void Raw(params byte[] x) { Bytes.AddRange(x); }
		public void Call(uint target) { Raw(0xE8); Rel32(target, 4); }
		public void Jmp(uint target) { Raw(0xE9); Rel32(target, 4); }
		void Rel32(uint target, int len) { int rel = (int)(target - (Here + (uint)len)); Bytes.AddRange(BitConverter.GetBytes(rel)); }

		// named labels for jumps inside the emitted code
		class Fix { public int Idx; public uint Next; public string Label; public bool Short; }
		readonly Dictionary<string, uint> labels = new Dictionary<string, uint>();
		readonly List<Fix> fixes = new List<Fix>();
		public void Mark(string label) { labels[label] = Here; }
		public void J8(byte opcode, string label) { Raw(opcode, 0); fixes.Add(new Fix { Idx = Bytes.Count - 1, Next = Here, Label = label, Short = true }); }
		public void J32(string label, params byte[] opcode) { Raw(opcode); Raw(0, 0, 0, 0); fixes.Add(new Fix { Idx = Bytes.Count - 4, Next = Here, Label = label }); }
		public void Resolve() {
			foreach (var f in fixes) {
				int d = (int)labels[f.Label] - (int)f.Next;
				if (f.Short) {
					if (d < -128 || d > 127) throw new Exception("short jump to " + f.Label + " out of range");
					Bytes[f.Idx] = unchecked((byte)(sbyte)d);
				}
				else { var v = BitConverter.GetBytes(d); for (int i = 0; i < 4; i++) Bytes[f.Idx + i] = v[i]; }
			}
			fixes.Clear();
		}
	}

	// ---- post-battle result for players who were not in the battle -------------------------------------
	// The campaign "battle results" popup takes its point of view from FUN_11107ee0: it asks the battle
	// (FUN_105b7b90) for the local faction's side. A faction that did not fight is on neither side, so the
	// "is attacker" flag stays 0 and every onlooker is shown the DEFENDER's result (or, via the battle's
	// fallback list, whatever faction happens to be last in it). Attackers winning therefore showed
	// "defeat" to their allies, defenders winning showed "victory".
	// Fix: if the local faction did not fight, take the side of the first faction that did fight and is
	// allied to it (humans first, then any faction), using the engine's own tests: diplomatic alliance
	// (or same faction / vassal), or both in the co-op faction list.
	const uint AllyHookAt = 0x11107F30;    // xor eax,eax / cmp [esp+1Ch],al / sete al / mov [ebx+4],eax (12 bytes)
	const uint AllyResume = 0x11107F3C;
	const uint BattleSideOf = 0x105B7AB0;  // thiscall(battle; faction, bool* is_attacker) -> participant or 0
	const uint FactionsAllied = 0x105CA920; // thiscall(faction; other) -> al = allied (diplomacy, same faction)
	const uint CoopTeammates = 0x1068D240;  // cdecl(faction, other) -> al = both in the campaign's co-op faction list

	static uint EmitAllyResultFix(Asm a) {
		// entry: ebx = result info, ebp = local faction, edi = battle, esi = &ui model ptr, [esp+1Ch] = is_attacker
		uint at = a.Here;
		a.Raw(0x6A, 0x00);                                // push 0                  (no flag wanted)
		a.Raw(0x55);                                      // push ebp
		a.Raw(0x8B, 0xCF);                                // mov ecx,edi
		a.Call(BattleSideOf);
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J32("original", 0x0F, 0x85);                    // jnz original            (local faction fought: unchanged)
		a.Raw(0x60);                                      // pushad
		a.Raw(0x6A, 0x01);                                // push 1                  (pass 1: humans only, pass 0: anyone)
		a.Mark("pass");
		a.Raw(0x8B, 0x44, 0x24, 0x08);                    // mov eax,[esp+8]         (saved esi)
		a.Raw(0x8B, 0x00);                                // mov eax,[eax]
		a.Raw(0x8B, 0x40, 0x08);                          // mov eax,[eax+8]         (campaign model)
		a.Raw(0x8B, 0x80, 0x98, 0x14, 0x00, 0x00);        // mov eax,[eax+1498h]     (faction list owner)
		a.Raw(0x8B, 0x58, 0x44);                          // mov ebx,[eax+44h]       (begin)
		a.Raw(0x8B, 0x48, 0x40);                          // mov ecx,[eax+40h]       (count)
		a.Raw(0x8D, 0x34, 0x8B);                          // lea esi,[ebx+ecx*4]     (end)
		a.Mark("next");
		a.Raw(0x3B, 0xDE);                                // cmp ebx,esi
		a.J32("pass_end", 0x0F, 0x83);                    // jae pass_end
		a.Raw(0x8B, 0x0B);                                // mov ecx,[ebx]           (candidate faction)
		a.Raw(0x83, 0xC3, 0x04);                          // add ebx,4
		a.Raw(0x3B, 0xCD);                                // cmp ecx,ebp
		a.J8(0x74, "next");                               // je next
		a.Raw(0x83, 0x3C, 0x24, 0x00);                    // cmp dword ptr [esp],0
		a.J8(0x74, "check");                              // je check                (pass 0: any faction)
		a.Raw(0x80, 0xB9, 0xA0, 0x06, 0x00, 0x00, 0x00);  // cmp byte ptr [ecx+6A0h],0 (is human)
		a.J8(0x74, "next");                               // je next
		a.Mark("check");
		a.Raw(0x51);                                      // push ecx                (keep candidate)
		a.Raw(0x51);                                      // push ecx                (other)
		a.Raw(0x8B, 0xCD);                                // mov ecx,ebp             (local faction)
		a.Call(FactionsAllied);
		a.Raw(0x59);                                      // pop ecx
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x75, "allied");                             // jne allied
		a.Raw(0x51);                                      // push ecx                (keep candidate)
		a.Raw(0x51);                                      // push ecx                (b)
		a.Raw(0x55);                                      // push ebp                (a = local faction)
		a.Call(CoopTeammates);
		a.Raw(0x83, 0xC4, 0x08);                          // add esp,8
		a.Raw(0x59);                                      // pop ecx
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J8(0x74, "next");                               // je next
		a.Mark("allied");
		a.Raw(0x6A, 0x00);                                // push 0                  (flag slot)
		a.Raw(0x54);                                      // push esp                (&flag slot)
		a.Raw(0x51);                                      // push ecx
		a.Raw(0x8B, 0xCF);                                // mov ecx,edi
		a.Call(BattleSideOf);
		a.Raw(0x5A);                                      // pop edx                 (dl = ally is attacker)
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x74, "next");                               // je next                 (ally didn't fight)
		a.Raw(0x88, 0x54, 0x24, 0x40);                    // mov [esp+40h],dl        (caller's is_attacker: 1Ch + 20h pushad + 4)
		a.J8(0xEB, "finish");                             // jmp finish
		a.Mark("pass_end");
		a.Raw(0xFF, 0x0C, 0x24);                          // dec dword ptr [esp]
		a.J32("pass", 0x0F, 0x89);                        // jns pass
		a.Mark("finish");
		a.Raw(0x83, 0xC4, 0x04);                          // add esp,4
		a.Raw(0x61);                                      // popad
		a.Mark("original");
		a.Raw(0x33, 0xC0);                                // xor eax,eax             (replaced instructions)
		a.Raw(0x38, 0x44, 0x24, 0x1C);                    // cmp [esp+1Ch],al
		a.Raw(0x0F, 0x94, 0xC0);                          // sete al
		a.Raw(0x89, 0x43, 0x04);                          // mov [ebx+4],eax
		a.Jmp(AllyResume);
		a.Resolve();
		return at;
	}

	// ---- deterministic floating-point modes -------------------------------------------------------------
	// Multiplayer is lockstep: every PC runs the same simulation and compares checksums, so every PC must do
	// its maths identically. The engine never sets the CPU's floating-point modes itself (the only FLDCW/LDMXCSR
	// in it are float->int helpers that restore whatever mode was active, and CRT internals), and TBB copies
	// the main thread's modes to its worker threads. So anything that changes them on one PC - overlays,
	// anti-virus hooks, drivers, code run on alt-tab - makes that PC's simulation drift: a desync.
	// Fix: load the Windows/MSVC defaults (x87 control 027Fh: 53-bit precision, round to nearest, exceptions
	// masked; MXCSR 1F80h: round to nearest, no flush-to-zero/denormals-are-zero) before the simulation runs:
	// at the start of each frame's game update and at the start of each battle tick. On a PC where nothing
	// changed them this does nothing.
	const uint FrameUpdateCall = 0x100B190C;   // main frame: call FUN_10f36680 (multiplayer update), first game logic
	const uint MultiplayerUpdate = 0x10F36680;
	const uint BattleTickAt = 0x10227D40;      // battle tick (vtable slot 33): push [esp+8] / mov ecx,[esp+10h]
	const uint BattleTickResume = 0x10227D48;

	static void EmitPinFloatModes(Asm a) {
		a.Raw(0x83, 0xEC, 0x04);                          // sub esp,4
		a.Raw(0x66, 0xC7, 0x04, 0x24, 0x7F, 0x02);        // mov word ptr [esp],027Fh
		a.Raw(0xD9, 0x2C, 0x24);                          // fldcw word ptr [esp]
		a.Raw(0xC7, 0x04, 0x24, 0x80, 0x1F, 0x00, 0x00);  // mov dword ptr [esp],1F80h
		a.Raw(0x0F, 0xAE, 0x14, 0x24);                    // ldmxcsr dword ptr [esp]
		a.Raw(0x83, 0xC4, 0x04);                          // add esp,4
	}

	// ---- player count in the lobby title ----------------------------------------------------------------
	// FUN_10fd6650 (thiscall on the lobby's member listener; [ecx-50h] = lobby root, [ecx+34h] = session) runs
	// whenever a player joins or changes. On entry the title "panel_heading" is set to
	// "Eight Player Campaign (N/8)", N = FUN_10f0eb00(session+58EF4h): the session's connected players.
	const uint MemberUpdateAt = 0x10FD6650;      // sub esp,84h (6 bytes)
	const uint MemberUpdateResume = 0x10FD6656;
	const uint CountPlayers = 0x10F0EB00;        // fastcall(member table) -> count
	const uint WStrFromAnsi = 0x100ED420;        // thiscall(wstring*; const char*) ret 4
	const uint WStrDtor = 0x100ED830;            // thiscall(wstring*)
	const uint SetText = 0x10F9D010;             // thiscall(component; wstring*, bool all_states) ret 8

	static uint EmitTitleCount(Asm a) {
		const string title = "Eight Player Campaign (0/8)";
		uint at = a.Here;
		a.Raw(0x60);                                      // pushad
		a.Raw(0x83, 0xEC, 0x50);                          // sub esp,50h  ([esp] text, [esp+20h] wstring, [esp+30h] name)
		a.Raw(0x8B, 0xE9);                                // mov ebp,ecx
		a.Raw(0x8B, 0x45, 0x34);                          // mov eax,[ebp+34h]       (session)
		a.Raw(0x85, 0xC0);
		a.J32("out", 0x0F, 0x84);
		a.Raw(0x8D, 0x88, 0xF4, 0x8E, 0x05, 0x00);        // lea ecx,[eax+58EF4h]
		a.Call(CountPlayers);
		a.Raw(0x83, 0xF8, 0x09);                          // cmp eax,9
		a.J8(0x72, "one_digit");
		a.Raw(0xB8, 0x09, 0x00, 0x00, 0x00);              // mov eax,9
		a.Mark("one_digit");
		a.Raw(0x8B, 0xD8);                                // mov ebx,eax
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint popAt = a.Here;
		a.Raw(0x5E);                                      // pop esi
		int tmplIdx = a.Bytes.Count + 2;
		a.Raw(0x81, 0xC6, 0, 0, 0, 0);                    // add esi, template - popAt
		a.Raw(0x8B, 0xFC);                                // mov edi,esp
		a.Raw(0xB9); Imm(a, (uint)title.Length + 1);      // mov ecx,len+1
		a.Raw(0xF3, 0xA4);                                // rep movsb               (esi -> "panel_heading")
		a.Raw(0x80, 0xC3, 0x30);                          // add bl,'0'
		a.Raw(0x88, 0x5C, 0x24, (byte)title.IndexOf('0'));   // mov [esp+digit],bl
		a.Raw(0x56);                                      // push esi
		a.Raw(0x8D, 0x4C, 0x24, 0x34);                    // lea ecx,[esp+34h]
		a.Call(StrCtor);
		a.Raw(0x8B, 0x4D, 0xB0);                          // mov ecx,[ebp-50h]       (lobby root)
		a.Raw(0x8D, 0x44, 0x24, 0x30, 0x50);              // lea eax,[esp+30h] / push eax
		a.Call(FindChild);
		a.Raw(0x8B, 0xF0);                                // mov esi,eax
		a.Raw(0x8D, 0x4C, 0x24, 0x30);                    // lea ecx,[esp+30h]
		a.Call(StrDtor);
		a.Raw(0x85, 0xF6);
		a.J8(0x74, "out");
		a.Raw(0x8D, 0x04, 0x24, 0x50);                    // lea eax,[esp] / push eax
		a.Raw(0x8D, 0x4C, 0x24, 0x24);                    // lea ecx,[esp+24h]       (wstring)
		a.Call(WStrFromAnsi);
		a.Raw(0x6A, 0x01);                                // push 1                  (all states)
		a.Raw(0x8D, 0x44, 0x24, 0x24, 0x50);              // lea eax,[esp+24h] / push eax
		a.Raw(0x8B, 0xCE);                                // mov ecx,esi
		a.Call(SetText);
		a.Raw(0x8D, 0x4C, 0x24, 0x20);                    // lea ecx,[esp+20h]
		a.Call(WStrDtor);
		a.Mark("out");
		a.Raw(0x83, 0xC4, 0x50);                          // add esp,50h
		a.Raw(0x61);                                      // popad
		a.Raw(0x81, 0xEC, 0x84, 0x00, 0x00, 0x00);        // sub esp,84h             (replaced instruction)
		a.Jmp(MemberUpdateResume);
		uint tmpl = a.Here;
		a.Bytes.AddRange(Encoding.ASCII.GetBytes(title)); a.Raw(0);
		a.Bytes.AddRange(Encoding.ASCII.GetBytes("panel_heading")); a.Raw(0);
		FixImm32(a, tmplIdx, tmpl - popAt);
		a.Resolve();
		return at;
	}

	// ---- Load Game screen: every player of a multiplayer save -----------------------------------------
	// FUN_10fc8dd0 (save selected; ebp = screen, [ebp-3Ch] = root, [ebp+4Ch] = save header) fills
	// portrait_player1 (you, index [hdr+31h]) and portrait_player2 (index 1, or 0 if you are 1) from the header's
	// player lists: count byte [hdr+30h], names {.., [hdr+3Ch]} and clan keys {.., [hdr+48h]}, 0Ch apiece.
	// Saves hold every player, but only two were shown. After those two, this fills portrait_player3..8 (from
	// build_load_layout.ps1) with the remaining players' names and clan names, and hides the unused rows.
	const uint LoadPortraitsAt = 0x10FC93B7;     // mov eax,[ebp+4Ch] / xor esi,esi (5 bytes)
	const uint LoadPortraitsResume = 0x10FC93BC;
	const uint FrontendGlobal = 0x11BC095C;      // frontend manager pointer
	const uint FactionDatabase = 0x10BCE670;     // thiscall(x) -> faction database
	const uint FactionByKey = 0x10308D20;        // thiscall(db; key*) ret 4 -> faction record (+28h: name key)

	static uint EmitLoadScreenPlayers(Asm a) {
		const string slotName = "portrait_player3";
		// find: ecx = parent, edx = const char* name -> eax = child or 0
		uint find = a.Here;
		a.Raw(0x56, 0x57, 0x83, 0xEC, 0x10);              // push esi / push edi / sub esp,10h
		a.Raw(0x8B, 0xF1, 0x52);                          // mov esi,ecx / push edx
		a.Raw(0x8D, 0x4C, 0x24, 0x04);                    // lea ecx,[esp+4]
		a.Call(StrCtor);
		a.Raw(0x8B, 0xCE, 0x8D, 0x04, 0x24, 0x50);        // mov ecx,esi / lea eax,[esp] / push eax
		a.Call(FindChild);
		a.Raw(0x8B, 0xF8, 0x8D, 0x0C, 0x24);              // mov edi,eax / lea ecx,[esp]
		a.Call(StrDtor);
		a.Raw(0x8B, 0xC7, 0x83, 0xC4, 0x10, 0x5F, 0x5E, 0xC3);   // mov eax,edi / add esp,10h / pop edi / pop esi / ret

		uint at = a.Here;
		a.Raw(0x60, 0x83, 0xEC, 0x40);                    // pushad / sub esp,40h
		// locals: [esp+0Ch] clan text box, [esp+10h] count, [esp+14h] i, [esp+18h] slot, [esp+1Ch] you,
		//         [esp+20h] other, [esp+24h] clan record, [esp+28h] "portrait_playerN"
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint B = a.Here;
		a.Raw(0x5B);                                      // pop ebx                 (base for strings / global)
		int strIdx = a.Bytes.Count;
		a.Raw(0x8B, 0x7D, 0x4C);                          // mov edi,[ebp+4Ch]       (save header)
		a.Raw(0x85, 0xFF);
		a.J32("out", 0x0F, 0x84);
		a.Raw(0x0F, 0xB6, 0x47, 0x30, 0x89, 0x44, 0x24, 0x10);   // movzx eax,byte [edi+30h] / mov [esp+10h],eax
		a.Raw(0x0F, 0xB6, 0x47, 0x31, 0x89, 0x44, 0x24, 0x1C);   // movzx eax,byte [edi+31h] / mov [esp+1Ch],eax
		a.Raw(0x33, 0xC9, 0x85, 0xC0, 0x0F, 0x94, 0xC1);  // xor ecx,ecx / test eax,eax / sete cl
		a.Raw(0x89, 0x4C, 0x24, 0x20);                    // mov [esp+20h],ecx       (the player in portrait 2)
		a.Raw(0xC7, 0x44, 0x24, 0x18, 0x03, 0x00, 0x00, 0x00);   // mov dword [esp+18h],3
		a.Raw(0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00);   // mov dword [esp+14h],0
		Action<string> strEdx = null, strEsi = null;      // filled below once the string offsets are known
		var strFix = new List<KeyValuePair<int, string>>();
		strEdx = key => { a.Raw(0x8D, 0x93); strFix.Add(new KeyValuePair<int, string>(a.Bytes.Count, key)); Imm(a, 0); };   // lea edx,[ebx+s]
		strEsi = key => { a.Raw(0x8D, 0xB3); strFix.Add(new KeyValuePair<int, string>(a.Bytes.Count, key)); Imm(a, 0); };   // lea esi,[ebx+s]
		Action makeName = () => {                         // [esp+28h] = "portrait_player" + slot
			strEsi("slot");
			a.Raw(0x57, 0x8D, 0x7C, 0x24, 0x2C);          // push edi / lea edi,[esp+2Ch]
			a.Raw(0xB9); Imm(a, (uint)slotName.Length + 1);
			a.Raw(0xF3, 0xA4, 0x5F);                      // rep movsb / pop edi
			a.Raw(0x8A, 0x44, 0x24, 0x18, 0x04, 0x30);    // mov al,[esp+18h] / add al,'0'
			a.Raw(0x88, 0x44, 0x24, (byte)(0x28 + slotName.Length - 1));
		};
		Action frontend = () => {                         // eax = [frontend]+18h -> vcall 4Ch
			a.Raw(0x8B, 0x83); Imm(a, FrontendGlobal - B);   // mov eax,[ebx+global]
			a.Raw(0x8D, 0x48, 0x18, 0x8B, 0x01, 0xFF, 0x50, 0x4C);   // lea ecx,[eax+18h] / mov eax,[ecx] / call [eax+4Ch]
		};
		a.Mark("loop");
		a.Raw(0x8B, 0x44, 0x24, 0x14, 0x3B, 0x44, 0x24, 0x10);   // mov eax,[esp+14h] / cmp eax,[esp+10h]
		a.J32("hide", 0x0F, 0x83);                        // jae hide
		a.Raw(0x3B, 0x44, 0x24, 0x1C); a.J32("next", 0x0F, 0x84);   // skip you
		a.Raw(0x3B, 0x44, 0x24, 0x20); a.J32("next", 0x0F, 0x84);   // skip portrait 2
		a.Raw(0x83, 0x7C, 0x24, 0x18, 0x08); a.J32("hide", 0x0F, 0x87);   // slot > 8
		makeName();
		a.Raw(0x8B, 0x4D, 0xC4, 0x8D, 0x54, 0x24, 0x28); a.Call(find);   // mov ecx,[ebp-3Ch] / lea edx,[esp+28h]
		a.Raw(0x85, 0xC0); a.J32("out", 0x0F, 0x84);       // layout without the rows: stop
		a.Raw(0x8B, 0xF0, 0x6A, 0x01, 0x8B, 0xCE); a.Call(SetVisible);  // mov esi,eax / SetVisible(1)
		// player name
		a.Raw(0x8B, 0xCE); strEdx("name"); a.Call(find);
		a.Raw(0x85, 0xC0); a.J8(0x74, "clan");
		a.Raw(0x8B, 0x54, 0x24, 0x14, 0x6B, 0xD2, 0x0C, 0x03, 0x57, 0x3C);   // mov edx,[esp+14h] / imul edx,edx,0Ch / add edx,[edi+3Ch]
		a.Raw(0x6A, 0x00, 0x52, 0x8B, 0xC8); a.Call(SetText);
		a.Mark("clan");
		frontend();
		a.Raw(0x8B, 0x48, 0x10); a.Call(FactionDatabase);  // mov ecx,[eax+10h]
		a.Raw(0x8B, 0x54, 0x24, 0x14, 0x6B, 0xD2, 0x0C, 0x03, 0x57, 0x48);   // edx = clan key of player i
		a.Raw(0x52, 0x8B, 0xC8); a.Call(FactionByKey);
		a.Raw(0x85, 0xC0); a.J32("slotdone", 0x0F, 0x84);
		a.Raw(0x89, 0x44, 0x24, 0x24);                    // mov [esp+24h],eax
		a.Raw(0x8B, 0xCE); strEdx("clan"); a.Call(find);
		a.Raw(0x85, 0xC0); a.J32("slotdone", 0x0F, 0x84);
		a.Raw(0x89, 0x44, 0x24, 0x0C);                    // mov [esp+0Ch],eax
		frontend();
		a.Raw(0x6A, 0x01, 0x8B, 0x48, 0x10, 0x8B, 0x89, 0x38, 0x0D, 0x00, 0x00);   // push 1 / mov ecx,[eax+10h] / mov ecx,[ecx+0D38h]
		a.Raw(0x8B, 0x44, 0x24, 0x28, 0x83, 0xC0, 0x28, 0x50);   // mov eax,[esp+28h] (record) / add eax,28h / push eax
		a.Raw(0x8B, 0x01, 0xFF, 0x50, 0x08);              // mov eax,[ecx] / call [eax+8]  -> localised clan name
		a.Raw(0x6A, 0x00, 0x50, 0x8B, 0x4C, 0x24, 0x14); a.Call(SetText);   // push 0 / push eax / mov ecx,[esp+14h]
		a.Mark("slotdone");
		a.Raw(0xFF, 0x44, 0x24, 0x18);                    // inc dword [esp+18h]
		a.Mark("next");
		a.Raw(0xFF, 0x44, 0x24, 0x14);                    // inc dword [esp+14h]
		a.J32("loop", 0xE9);
		a.Mark("hide");                                   // rows nobody uses
		a.Raw(0x83, 0x7C, 0x24, 0x18, 0x08); a.J8(0x77, "out");
		makeName();
		a.Raw(0x8B, 0x4D, 0xC4, 0x8D, 0x54, 0x24, 0x28); a.Call(find);
		a.Raw(0x85, 0xC0); a.J8(0x74, "out");
		a.Raw(0x6A, 0x00, 0x8B, 0xC8); a.Call(SetVisible);
		a.Raw(0xFF, 0x44, 0x24, 0x18);
		a.J8(0xEB, "hide");
		a.Mark("out");
		a.Raw(0x83, 0xC4, 0x40, 0x61);                    // add esp,40h / popad
		a.Raw(0x8B, 0x45, 0x4C, 0x33, 0xF6);              // mov eax,[ebp+4Ch] / xor esi,esi (replaced)
		a.Jmp(LoadPortraitsResume);
		var strs = new Dictionary<string, uint>();
		foreach (var kv in new[] { new KeyValuePair<string, string>("slot", slotName), new KeyValuePair<string, string>("name", "player_name"), new KeyValuePair<string, string>("clan", "dy_clan_name") }) {
			strs[kv.Key] = a.Here; a.Bytes.AddRange(Encoding.ASCII.GetBytes(kv.Value)); a.Raw(0);
		}
		foreach (var f in strFix) FixImm32(a, f.Key, strs[f.Value] - B);
		a.Resolve();
		return at;
	}

	// ---- co-op team for every human clan -------------------------------------------------------------------
	// FUN_1068e0e0 (faction manager in esi) rebuilds the co-op team list [mgr+13Ch..144h] (the list
	// FUN_1068d240 uses for "co-op ally"): every clan with a human player record [clan+6D8h] whose campaign type
	// [record+74h] is co-op short/long (5/6, FUN_105cb5b0). Each human gets that type from the lobby slot, so
	// a record set up as anything else would leave that player out of the team.
	// Fix, at that call: if the record isn't co-op but another human's record is, copy that co-op type into it
	// (so every other co-op check agrees) and count it in. Head-to-head (no co-op record) is unchanged.
	const uint CoopMemberCall = 0x1068E124;      // call FUN_105cb5b0 (ecx = player record, esi = faction manager)
	const uint IsCoopRecord = 0x105CB5B0;

	// Only HUMAN clans ([clan+6A0h]) count: start positions and saves also give player records to playable AI
	// clans, and v1.6 (which didn't check) made every one of them a permanent co-op ally.
	static uint EmitCoopMember(Asm a) {
		uint at = a.Here;
		a.Raw(0x80, 0xBB, 0xA0, 0x06, 0x00, 0x00, 0x00);  // cmp byte [ebx+6A0h],0   (ebx = clan at the call)
		a.J8(0x75, "human");
		a.Raw(0x32, 0xC0, 0xC3);                          // AI clan: never on the co-op team (xor al,al / ret)
		a.Mark("human");
		a.Call(IsCoopRecord);
		a.Raw(0x84, 0xC0); a.J8(0x75, "ret");             // already co-op
		a.Raw(0x8B, 0x93, 0xD8, 0x06, 0x00, 0x00);        // mov edx,[ebx+6D8h]      (this human's record)
		a.Raw(0x53, 0x57, 0x51);                          // push ebx / push edi / push ecx
		a.Raw(0x8B, 0x7E, 0x44, 0x8B, 0x4E, 0x40);        // mov edi,[esi+44h] / mov ecx,[esi+40h]  (all clans)
		a.Raw(0x85, 0xC9); a.J8(0x74, "none");
		a.Mark("scan");
		a.Raw(0x8B, 0x1F);                                // mov ebx,[edi]           (clan)
		a.Raw(0x80, 0xBB, 0xA0, 0x06, 0x00, 0x00, 0x00);  // cmp byte [ebx+6A0h],0   (other humans only)
		a.J8(0x74, "nx");
		a.Raw(0x8B, 0x9B, 0xD8, 0x06, 0x00, 0x00);        // mov ebx,[ebx+6D8h]      (its record)
		a.Raw(0x85, 0xDB); a.J8(0x74, "nx");
		a.Raw(0x8B, 0x43, 0x74);                          // mov eax,[ebx+74h]
		a.Raw(0x83, 0xF8, 0x05); a.J8(0x74, "yes");
		a.Raw(0x83, 0xF8, 0x06); a.J8(0x74, "yes");
		a.Mark("nx");
		a.Raw(0x83, 0xC7, 0x04, 0x49); a.J8(0x75, "scan");   // add edi,4 / dec ecx / jnz scan
		a.Mark("none");
		a.Raw(0x33, 0xC0); a.J8(0xEB, "done");            // xor eax,eax
		a.Mark("yes");
		a.Raw(0x89, 0x42, 0x74, 0xB0, 0x01);              // mov [edx+74h],eax / mov al,1
		a.Mark("done");
		a.Raw(0x59, 0x5F, 0x5B);                          // pop ecx / pop edi / pop ebx
		a.Mark("ret");
		a.Raw(0xC3);
		a.Resolve();
		return at;
	}

	// ---- players not in a battle (3+ humans) ------------------------------------------------------------
	// FUN_105cefa0 decides what a human who is NOT in a battle controls when it starts. It counts the human
	// players ([[..+0EA0h]+30h], 0B8h per slot, active flag +0A0h) and handles exactly 2: follow the human who is
	// in the battle ("ally spectate", 281E9h = 1, the mode gifting uses), or for one battle kind (FUN_105cbb50)
	// take the pending battle's army. Any other count makes a plain spectator of alliance 0 / army 0, a path
	// 2-player campaigns never reach and that closes the game. Players 3+ were always on it, which is why
	// everyone not in a battle quit, and (with v1.6's AI co-op team) why AI battles closed every game.
	// Fix: "count == 2" -> "count >= 2", so 3..8 humans get the same tested handling as 2.
	const uint NonParticipantCountJump = 0x105CF0A1;   // cmp ecx,2 / jne -> jb  (0F 85 -> 0F 82)

	// ---- leaving a campaign battle early with 3+ players ----------------------------------------------------
	// FUN_10f1f8d0 (esi = session+8) runs when a player leaves a battle before it is finished. A non-host may
	// leave normally only if the session has at most 2 players (FUN_10f0eb00 <= 2); otherwise they are
	// disconnected from the session (FUN_10f033e0). Right for 3+ player custom battles, wrong in a campaign,
	// where a player retreating or leaving a battle dropped out of the whole campaign.
	// Fix: keep the rule outside campaigns; in a campaign ([session+596D8h], campaign environment) leave normally.
	const uint LeaveBattleCountAt = 0x10F1F91E;   // cmp eax,2 / ja 10F1F931 (5 bytes)
	const uint LeaveBattleAllowed = 0x10F1F923;
	const uint LeaveBattleDisconnect = 0x10F1F931;

	static uint EmitLeaveBattleRule(Asm a) {
		uint at = a.Here;
		a.Raw(0x83, 0xF8, 0x02);                          // cmp eax,2
		a.J8(0x76, "allowed");                            // jbe allowed            (original rule)
		a.Raw(0x83, 0xBE, 0xD0, 0x96, 0x05, 0x00, 0x00);  // cmp dword [esi+596D0h],0   (session+596D8h: campaign)
		a.J8(0x75, "allowed");
		a.Jmp(LeaveBattleDisconnect);
		a.Mark("allowed");
		a.Jmp(LeaveBattleAllowed);
		a.Resolve();
		return at;
	}

	// ---- crash reporter ----------------------------------------------------------------------------------
	// The game builds a Crash.dmp path at start-up but never installs a handler, so a crash leaves no trace.
	// This installs an unhandled-exception filter (once, from the frame hook) that writes eightplayer_crash.txt
	// to the game's working folder: exception code and address, this DLL's base (to turn addresses into the
	// offsets in NOTES.md), registers and the top 64 stack dwords. Then it hands over to the previous filter.
	const uint SetUnhandledExceptionFilterIat = 0x115A10F4;
	const uint CreateFileAIat = 0x115A11A4;
	const uint WriteFileIat = 0x115A1154;
	const uint CloseHandleIat = 0x115A1158;
	const uint LoadLibraryAIat = 0x115A1128;
	const uint GetProcAddressIat = 0x115A1190;
	const uint GetCurrentProcessIat = 0x115A1170;
	const uint GetCurrentProcessIdIat = 0x115A1174;
	const uint GetCurrentThreadIdIat = 0x115A11F4;
	const uint CrashPrevFilterVa = 0x11CB1538;   // previous filter (third reserved .data dword)
	const uint CrashInstalledVa = 0x11CB153C;    // filter installed (fourth)
	public const string Version = "1.13";
	static uint crashFilterAt;

	// ---- engine error log ------------------------------------------------------------------------------
	// Before the engine closes itself on an internal error (TerminateProcess, exit code 0: no crash, no Windows
	// error), it formats a message through FUN_100c19b0 and hands it to a handler that shows nothing. This
	// appends every such message as a line of text to eightplayer_errors.txt in the game folder, then
	// continues exactly as before. Hooked after the message is formatted (ebx = wide text).
	const uint ErrorMessageAt = 0x100C1B2B;      // add esp,1Ch / test eax,eax / je 100C1B7F (7 bytes)
	const uint ErrorMessageNoHandler = 0x100C1B7F;
	const uint ErrorMessageResume = 0x100C1B32;

	static uint EmitErrorLog(Asm a) {
		uint at = a.Here;
		a.Raw(0x83, 0xC4, 0x1C);                          // add esp,1Ch             (replaced)
		a.Raw(0x60);                                      // pushad
		a.Raw(0x81, 0xEC, 0x10, 0x04, 0x00, 0x00);        // sub esp,410h  ([esp] written, [esp+4] length, [esp+8] text)
		a.Raw(0x8B, 0xF3, 0x8D, 0x7C, 0x24, 0x08, 0x33, 0xC9);   // mov esi,ebx / lea edi,[esp+8] / xor ecx,ecx
		a.Raw(0x85, 0xF6); a.J8(0x74, "end");             // no message
		a.Mark("copy");                                   // wide -> ASCII, '?' for anything else
		a.Raw(0x0F, 0xB7, 0x04, 0x4E);                    // movzx eax,word [esi+ecx*2]
		a.Raw(0x85, 0xC0); a.J8(0x74, "end");
		a.Raw(0x81, 0xF9, 0xF0, 0x03, 0x00, 0x00); a.J8(0x73, "end");   // cmp ecx,3F0h / jae end
		a.Raw(0x3D, 0x80, 0x00, 0x00, 0x00); a.J8(0x72, "ascii");       // cmp eax,80h / jb ascii
		a.Raw(0xB0, 0x3F);                                // mov al,'?'
		a.Mark("ascii");
		a.Raw(0x88, 0x04, 0x0F, 0x41);                    // mov [edi+ecx],al / inc ecx
		a.J8(0xEB, "copy");
		a.Mark("end");
		a.Raw(0x66, 0xC7, 0x04, 0x0F, 0x0D, 0x0A);        // mov word [edi+ecx],0A0Dh   (CR LF)
		a.Raw(0x83, 0xC1, 0x02, 0x89, 0x4C, 0x24, 0x04);  // add ecx,2 / mov [esp+4],ecx
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint B = a.Here;
		a.Raw(0x5D);                                      // pop ebp
		a.Raw(0x6A, 0x00, 0x68, 0x80, 0x00, 0x00, 0x00, 0x6A, 0x04, 0x6A, 0x00, 0x6A, 0x03, 0x6A, 0x04);   // 0, NORMAL, OPEN_ALWAYS, 0, share r/w, FILE_APPEND_DATA
		int fileIdx = a.Bytes.Count + 2;
		a.Raw(0x8D, 0x85, 0, 0, 0, 0, 0x50);              // lea eax,[ebp+file] / push eax
		a.Raw(0x8B, 0x85); Imm(a, CreateFileAIat - B); a.Raw(0xFF, 0xD0);
		a.Raw(0x83, 0xF8, 0xFF); a.J8(0x74, "done");
		a.Raw(0x8B, 0xF0);                                // mov esi,eax
		a.Raw(0x6A, 0x00);                                // push 0                  (overlapped)
		a.Raw(0x8D, 0x44, 0x24, 0x04, 0x50);              // lea eax,[esp+4] / push eax   (written)
		a.Raw(0xFF, 0x74, 0x24, 0x0C);                    // push [esp+0Ch]          (length)
		a.Raw(0x8D, 0x44, 0x24, 0x14, 0x50);              // lea eax,[esp+14h] / push eax (text)
		a.Raw(0x56);                                      // push esi
		a.Raw(0x8B, 0x85); Imm(a, WriteFileIat - B); a.Raw(0xFF, 0xD0);
		a.Raw(0x56);
		a.Raw(0x8B, 0x85); Imm(a, CloseHandleIat - B); a.Raw(0xFF, 0xD0);
		a.Mark("done");
		a.Raw(0x81, 0xC4, 0x10, 0x04, 0x00, 0x00, 0x61); // add esp,410h / popad
		a.Raw(0x85, 0xC0);                                // test eax,eax            (replaced)
		a.J32("nohandler", 0x0F, 0x84);
		a.Jmp(ErrorMessageResume);
		a.Mark("nohandler");
		a.Jmp(ErrorMessageNoHandler);
		uint file = a.Here;
		a.Bytes.AddRange(Encoding.ASCII.GetBytes("eightplayer_errors.txt")); a.Raw(0);
		FixImm32(a, fileIdx, file - B);
		a.Resolve();
		return at;
	}

	// ---- "who controls my side" for a clan that isn't in the battle (head-to-head crash) -----------------
	// Two crash dumps from a 3+ player head-to-head game (v1.7): access violation reading address 4 at 105B5128.
	// FUN_105b50b0 (pending battle; clan) returns the clan controlling that clan's side: co-op team member or
	// [side+4]. It reads the side from FUN_105b7b90 without a null check. The pre-battle screen (11185F5B) and
	// a campaign UI panel (1115696D) ask it for the LOCAL clan; for a player who isn't in the battle - which only
	// happens with 3+ players - there is no side, and the game crashed. In co-op the team branch happened to
	// avoid the read in that panel; in head-to-head (empty co-op team) it always crashed.
	// Fix: both lookups go through a stub; "not in the battle" returns a stand-in side whose controller is the
	// clan itself ("my side is mine"), which matches no one in the battle, as for the original third party.
	static readonly uint[] SideLookupCalls = { 0x105B50D7, 0x105B5120 };   // call FUN_105b7b90 (ebp = clan)
	const uint BattleSideOfClan = 0x105B7B90;      // thiscall(battle; clan, bool*) ret 8
	const uint StandInSideVa = 0x11CB1544;         // 2 dwords (sixth/seventh reserved): [+4] = controller

	static uint EmitSideOrSelf(Asm a) {
		uint at = a.Here;
		a.Raw(0xFF, 0x74, 0x24, 0x08, 0xFF, 0x74, 0x24, 0x08);   // push [esp+8] / push [esp+8]  (same two arguments)
		a.Call(BattleSideOfClan);                         // ecx = battle, unchanged
		a.Raw(0x85, 0xC0); a.J8(0x75, "ret");             // in the battle: the real side
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint B = a.Here;
		a.Raw(0x58);                                      // pop eax
		a.Raw(0x05); Imm(a, StandInSideVa - B);           // add eax, stand-in - B
		a.Raw(0x89, 0x68, 0x04);                          // mov [eax+4],ebp     (controller = the clan asked about)
		a.Mark("ret");
		a.Raw(0xC2, 0x08, 0x00);                          // ret 8
		a.Resolve();
		return at;
	}

	// ---- player count in the Campaign Games list -----------------------------------------------------------
	// FUN_10ff5210 builds a list row's details from the Steam lobby (ebx): members byte [+9Ch], limit [+38h],
	// lobby id [+78h..7Fh]. It only keeps "full" (count == limit), so the count is recorded in a small table
	// keyed by lobby id. FUN_110499f0 (row update; ebp = details, id at [+80h..87h]) sets the Status cell's
	// state (open / full / in-progress); after it the cell's own text gets a second line "3/8". The text before
	// any newline is kept, so the localised word stays and repeated updates don't stack.
	const uint LobbyRecordAt = 0x10FF5337;         // cmp dword ptr [ebx+88h],1 (7 bytes)
	const uint LobbyRecordResume = 0x10FF533E;
	const uint StatusStateCall = 0x11049DF4;       // call SetState for the Status cell (ecx = cell, ebp = details)
	const uint SetState = 0x10F9BF20;              // thiscall(component; string* state) ret 4
	const uint WStrFromRange = 0x100ED580;         // thiscall(wstring*; const wchar_t*, ushort len) ret 8
	const uint LobbyCursorVa = 0x11CB15FC;         // next slot to reuse
	const uint LobbyTableVa = 0x11CB1600;          // 32 x {id lo, id hi, members, limit}
	const int LobbySlots = 32;

	static uint EmitLobbyCountRecord(Asm a) {
		uint at = a.Here;
		a.Raw(0x60);                                      // pushad
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint B = a.Here;
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0x81, 0xC6); Imm(a, LobbyTableVa - B);      // add esi, table - B
		a.Raw(0x8B, 0x43, 0x78);                          // mov eax,[ebx+78h]      (lobby id)
		a.Raw(0x8B, 0x53, 0x7C);                          // mov edx,[ebx+7Ch]
		a.Raw(0x8B, 0xC8, 0x0B, 0xCA);                    // mov ecx,eax / or ecx,edx
		a.J8(0x74, "done");
		a.Raw(0x8B, 0xFE);                                // mov edi,esi
		a.Raw(0xB9); Imm(a, (uint)LobbySlots);            // mov ecx,slots
		a.Mark("find");
		a.Raw(0x39, 0x07); a.J8(0x75, "next");            // cmp [edi],eax
		a.Raw(0x39, 0x57, 0x04); a.J8(0x74, "store");     // cmp [edi+4],edx
		a.Mark("next");
		a.Raw(0x83, 0xC7, 0x10);                          // add edi,10h
		a.J8(0xE2, "find");                               // loop
		a.Raw(0x8B, 0x4E, 0xFC);                          // mov ecx,[esi-4]        (cursor)
		a.Raw(0x8B, 0xF9);                                // mov edi,ecx
		a.Raw(0x83, 0xE7, (byte)(LobbySlots - 1));        // and edi,slots-1
		a.Raw(0xC1, 0xE7, 0x04);                          // shl edi,4
		a.Raw(0x03, 0xFE);                                // add edi,esi
		a.Raw(0x41, 0x89, 0x4E, 0xFC);                    // inc ecx / mov [esi-4],ecx
		a.Raw(0x89, 0x07, 0x89, 0x57, 0x04);              // mov [edi],eax / mov [edi+4],edx
		a.Mark("store");
		a.Raw(0x0F, 0xB6, 0x8B, 0x9C, 0x00, 0x00, 0x00);  // movzx ecx,byte ptr [ebx+9Ch]   (members)
		a.Raw(0x89, 0x4F, 0x08);                          // mov [edi+8],ecx
		a.Raw(0x8B, 0x4B, 0x38);                          // mov ecx,[ebx+38h]      (limit)
		a.Raw(0x89, 0x4F, 0x0C);                          // mov [edi+0Ch],ecx
		a.Mark("done");
		a.Raw(0x61);                                      // popad
		a.Raw(0x83, 0xBB, 0x88, 0x00, 0x00, 0x00, 0x01);  // cmp dword ptr [ebx+88h],1   (replaced)
		a.Jmp(LobbyRecordResume);
		a.Resolve();
		return at;
	}

	// writes eax (clamped to 0..99) as UTF-16 digits at [ebx], advancing ebx
	static void EmitWideNumber(Asm a, string tag) {
		a.Raw(0x83, 0xF8, 0x63); a.J8(0x76, tag + "_le");  // cmp eax,99 / jbe
		a.Raw(0xB8, 0x63, 0x00, 0x00, 0x00);              // mov eax,99
		a.Mark(tag + "_le");
		a.Raw(0x83, 0xF8, 0x0A); a.J8(0x72, tag + "_one"); // cmp eax,10 / jb
		a.Raw(0x33, 0xD2, 0xB9, 0x0A, 0x00, 0x00, 0x00);  // xor edx,edx / mov ecx,10
		a.Raw(0xF7, 0xF1);                                // div ecx
		a.Raw(0x83, 0xC0, 0x30, 0x66, 0x89, 0x03);        // add eax,'0' / mov [ebx],ax
		a.Raw(0x83, 0xC3, 0x02, 0x8B, 0xC2);              // add ebx,2 / mov eax,edx
		a.Mark(tag + "_one");
		a.Raw(0x83, 0xC0, 0x30, 0x66, 0x89, 0x03);        // add eax,'0' / mov [ebx],ax
		a.Raw(0x83, 0xC3, 0x02);                          // add ebx,2
	}

	static uint EmitStatusCount(Asm a) {
		// thiscall(cell; state name) ret 4, in place of SetState; ebp = the row's details
		uint at = a.Here;
		a.Raw(0x51);                                      // push ecx
		a.Raw(0xFF, 0x74, 0x24, 0x08);                    // push [esp+8]
		a.Call(SetState);
		a.Raw(0x59);                                      // pop ecx                (cell)
		a.Raw(0x60);                                      // pushad
		a.Raw(0x83, 0xEC, 0x60);                          // sub esp,60h  ([esp] text, [esp+40h] wstring)
		a.Raw(0x8B, 0xF1);                                // mov esi,ecx
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint B = a.Here;
		a.Raw(0x5F);                                      // pop edi
		a.Raw(0x81, 0xC7); Imm(a, LobbyTableVa - B);      // add edi, table - B
		a.Raw(0x8B, 0x85, 0x80, 0x00, 0x00, 0x00);        // mov eax,[ebp+80h]      (lobby id)
		a.Raw(0x8B, 0x95, 0x84, 0x00, 0x00, 0x00);        // mov edx,[ebp+84h]
		a.Raw(0x8B, 0xC8, 0x0B, 0xCA);                    // mov ecx,eax / or ecx,edx
		a.J32("out", 0x0F, 0x84);
		a.Raw(0xB9); Imm(a, (uint)LobbySlots);            // mov ecx,slots
		a.Mark("find");
		a.Raw(0x39, 0x07); a.J8(0x75, "next");            // cmp [edi],eax
		a.Raw(0x39, 0x57, 0x04); a.J8(0x74, "found");     // cmp [edi+4],edx
		a.Mark("next");
		a.Raw(0x83, 0xC7, 0x10);                          // add edi,10h
		a.J8(0xE2, "find");                               // loop
		a.J32("out", 0xE9);                               // jmp out          (not in the table)
		a.Mark("found");
		a.Raw(0x83, 0x7F, 0x0C, 0x00);                    // cmp dword ptr [edi+0Ch],0   (no limit: leave it)
		a.J32("out", 0x0F, 0x84);
		a.Raw(0x8B, 0x9E, 0xB8, 0x00, 0x00, 0x00);        // mov ebx,[esi+0B8h]     (current state)
		a.Raw(0x85, 0xDB); a.J32("out", 0x0F, 0x84);
		a.Raw(0x0F, 0xB7, 0x4B, 0x34);                    // movzx ecx,word ptr [ebx+34h]  (text length)
		a.Raw(0x8B, 0x53, 0x40);                          // mov edx,[ebx+40h]      (text)
		a.Raw(0x85, 0xD2); a.J32("out", 0x0F, 0x84);
		a.Raw(0x83, 0xF9, 0x14); a.J8(0x76, "short");     // cmp ecx,20 / jbe
		a.Raw(0xB9, 0x14, 0x00, 0x00, 0x00);              // mov ecx,20
		a.Mark("short");
		a.Raw(0x8D, 0x1C, 0x24);                          // lea ebx,[esp]
		a.Mark("copy");
		a.J8(0xE3, "copied");                             // jecxz
		a.Raw(0x0F, 0xB7, 0x02);                          // movzx eax,word ptr [edx]
		a.Raw(0x83, 0xF8, 0x0A); a.J8(0x74, "copied");    // stop at a newline (our own line)
		a.Raw(0x66, 0x89, 0x03);                          // mov [ebx],ax
		a.Raw(0x83, 0xC3, 0x02, 0x83, 0xC2, 0x02, 0x49);  // add ebx,2 / add edx,2 / dec ecx
		a.J8(0xEB, "copy");
		a.Mark("copied");
		a.Raw(0x3B, 0xDC); a.J8(0x76, "trimmed");         // cmp ebx,esp / jbe
		a.Raw(0x66, 0x83, 0x7B, 0xFE, 0x20);              // cmp word ptr [ebx-2],' '
		a.J8(0x75, "trimmed");
		a.Raw(0x83, 0xEB, 0x02); a.J8(0xEB, "copied");    // sub ebx,2
		a.Mark("trimmed");
		a.Raw(0x3B, 0xDC); a.J32("out", 0x0F, 0x84);      // cmp ebx,esp: no word to keep, leave it
		a.Raw(0x66, 0xC7, 0x03, 0x0A, 0x00, 0x83, 0xC3, 0x02);   // mov word [ebx],'\n' / add ebx,2
		a.Raw(0x8B, 0x47, 0x08);                          // mov eax,[edi+8]        (members)
		EmitWideNumber(a, "count");
		a.Raw(0x66, 0xC7, 0x03, 0x2F, 0x00, 0x83, 0xC3, 0x02);   // mov word [ebx],'/' / add ebx,2
		a.Raw(0x8B, 0x47, 0x0C);                          // mov eax,[edi+0Ch]      (limit)
		EmitWideNumber(a, "limit");
		a.Raw(0x2B, 0xDC, 0xD1, 0xEB);                    // sub ebx,esp / shr ebx,1   (length)
		a.Raw(0x53);                                      // push ebx
		a.Raw(0x8D, 0x44, 0x24, 0x04, 0x50);              // lea eax,[esp+4] / push eax
		a.Raw(0x8D, 0x4C, 0x24, 0x48);                    // lea ecx,[esp+48h]      (wstring)
		a.Call(WStrFromRange);
		a.Raw(0x6A, 0x00);                                // push 0                 (current state only)
		a.Raw(0x8D, 0x44, 0x24, 0x44, 0x50);              // lea eax,[esp+44h] / push eax
		a.Raw(0x8B, 0xCE);                                // mov ecx,esi
		a.Call(SetText);
		a.Raw(0x8D, 0x4C, 0x24, 0x40);                    // lea ecx,[esp+40h]
		a.Call(WStrDtor);
		a.Mark("out");
		a.Raw(0x83, 0xC4, 0x60);                          // add esp,60h
		a.Raw(0x61);                                      // popad
		a.Raw(0xC2, 0x04, 0x00);                          // ret 4
		a.Resolve();
		return at;
	}

	// ---- resuming a campaign save with different players -----------------------------------------------
	// A multiplayer save lists its players' Steam ids. FUN_10ef0d40 (save info; steam id*, is_host) returns the
	// faction record (0Ch) of the player with that id, or an empty record: anyone not in the save had no
	// faction. The engine's hidden "mp_campaign_all_resume_any_players" ([11B8B2AE]) skips that, but only for
	// 2 players (host -> host slot, anyone else -> the other slot).
	// Now: a player in the save keeps their faction; anyone else takes a faction whose player isn't in the
	// session, in member-table order (the n-th newcomer gets the n-th free faction). Every machine has the same
	// member table, so every machine computes the same factions. Not a member: empty record, as before.
	// The three other checks of the hidden flag are switched on in place:
	//   10EE0F20 (may I host/join this save) -> always; 10EE0BC0 (lobby browser: resumed lobby compatible)
	//   -> always; FUN_1100c650 (saves listed for "Resume as Host") -> every save.
	const uint SaveSlotOf = 0x10EF0D40;            // thiscall(save info; steam id*, bool is_host) ret 8
	const uint SaveSlotCallSession = 0x10EF1DAF;   // edi = session, ecx = session+59730h
	const uint SaveSlotCallFile = 0x10EF49F1;      // esi = session+8, ecx = save info read from a file
	const uint EmptyRecordVa = 0x11B8B080;
	const uint ResumeJoinCheckJump = 0x10EE0F2D;   // jne 10EE0F88 (75 59) -> jmp
	const uint ResumeLobbyCompatCmov = 0x10EE0C41; // cmove eax,ecx (0F 44 C1) -> nops
	const uint ResumeListCmov = 0x1100C6B5;        // cmovne ecx,edx (0F 45 CA) -> mov ecx,edx

	static uint ssInSaveAt, ssMemberAt;   // helpers inside EmitSaveSlot, reused by the reserved-faction code

	static uint EmitSaveSlot(Asm a, out uint fromFile, out uint core) {
		// entries: ecx = save info, [esp+4] = steam id*, [esp+8] = is_host; core gets eax = session
		uint at = a.Here;
		a.Raw(0x8B, 0xC7);                                // mov eax,edi
		a.J8(0xEB, "ss_core");
		fromFile = a.Here;
		a.Raw(0x8D, 0x46, 0xF8);                          // lea eax,[esi-8]
		a.Mark("ss_core");
		core = a.Here;
		a.Raw(0x53, 0x55, 0x56, 0x57);                    // push ebx/ebp/esi/edi
		a.Raw(0x83, 0xEC, 0x08);                          // sub esp,8           ([esp] = asking id)
		a.Raw(0x8B, 0xF1);                                // mov esi,ecx         (save info)
		a.Raw(0x8D, 0xA8, 0xF4, 0x8E, 0x05, 0x00);        // lea ebp,[eax+58EF4h] (member table)
		a.Raw(0x8B, 0x54, 0x24, 0x1C);                    // mov edx,[esp+1Ch]   (steam id*)
		a.Raw(0x8B, 0x02, 0x89, 0x04, 0x24);              // mov eax,[edx] / mov [esp],eax
		a.Raw(0x8B, 0x42, 0x04, 0x89, 0x44, 0x24, 0x04);  // mov eax,[edx+4] / mov [esp+4],eax
		// 1. in the save: that faction
		a.Raw(0x8B, 0xD4);                                // mov edx,esp
		a.J32("ss_insave", 0xE8);                         // call -> ecx = slot or -1
		a.Raw(0x85, 0xC9); a.J32("ss_slot", 0x0F, 0x89);  // test ecx,ecx / jns
		// 2. k = newcomers before the asker in the member table (the asker must be a member)
		a.Raw(0x33, 0xFF, 0x33, 0xC9);                    // xor edi,edi / xor ecx,ecx
		a.Mark("ss_b");
		a.Raw(0x3B, 0x8D, 0xC0, 0x07, 0x00, 0x00);        // cmp ecx,[ebp+7C0h]
		a.J32("ss_empty", 0x0F, 0x83);                    // jae: not a member
		a.Raw(0x69, 0xD9, 0xF8, 0x00, 0x00, 0x00);        // imul ebx,ecx,0F8h
		a.Raw(0x03, 0xDD);                                // add ebx,ebp
		a.Raw(0xF6, 0x83, 0xF5, 0x00, 0x00, 0x00, 0x04);  // test byte ptr [ebx+0F5h],4   (slot in use)
		a.J8(0x74, "ss_bnext");
		a.Raw(0x8B, 0x03, 0x3B, 0x04, 0x24);              // mov eax,[ebx] / cmp eax,[esp]
		a.J8(0x75, "ss_bother");
		a.Raw(0x8B, 0x43, 0x04, 0x3B, 0x44, 0x24, 0x04);  // mov eax,[ebx+4] / cmp eax,[esp+4]
		a.J8(0x74, "ss_c");
		a.Mark("ss_bother");
		a.Raw(0x51, 0x8B, 0xD3);                          // push ecx / mov edx,ebx
		a.J32("ss_insave", 0xE8);
		a.Raw(0x85, 0xC9, 0x59);                          // test ecx,ecx / pop ecx
		a.J8(0x79, "ss_bnext");                           // jns: in the save, not a newcomer
		a.Raw(0x47);                                      // inc edi
		a.Mark("ss_bnext");
		a.Raw(0x41); a.J8(0xEB, "ss_b");                  // inc ecx
		// 3. the k-th faction whose player is not a member
		a.Mark("ss_c");
		a.Raw(0x33, 0xC9);                                // xor ecx,ecx
		a.Mark("ss_cl");
		a.Raw(0x0F, 0xB6, 0x06, 0x3B, 0xC8);              // movzx eax,byte ptr [esi] / cmp ecx,eax
		a.J8(0x73, "ss_empty");                           // more newcomers than free factions
		a.Raw(0x8B, 0x56, 0x0C, 0x8D, 0x14, 0xCA);        // mov edx,[esi+0Ch] / lea edx,[edx+ecx*8]
		a.J32("ss_member", 0xE8);                         // al = member
		a.Raw(0x84, 0xC0); a.J8(0x75, "ss_cnext");
		a.Raw(0x85, 0xFF); a.J8(0x74, "ss_slot");         // test edi,edi / jz
		a.Raw(0x4F);                                      // dec edi
		a.Mark("ss_cnext");
		a.Raw(0x41); a.J8(0xEB, "ss_cl");                 // inc ecx
		a.Mark("ss_slot");
		a.Raw(0x6B, 0xC1, 0x0C, 0x03, 0x46, 0x18);        // imul eax,ecx,0Ch / add eax,[esi+18h]
		a.J8(0xEB, "ss_out");
		a.Mark("ss_empty");
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint B = a.Here;
		a.Raw(0x58, 0x05); Imm(a, EmptyRecordVa - B);     // pop eax / add eax, empty - B
		a.Mark("ss_out");
		a.Raw(0x83, 0xC4, 0x08, 0x5F, 0x5E, 0x5D, 0x5B);  // add esp,8 / pop edi/esi/ebp/ebx
		a.Raw(0xC2, 0x08, 0x00);                          // ret 8

		// ss_insave: esi = save info, edx = id* -> ecx = slot with that id, or -1 (keeps others but eax)
		a.Mark("ss_insave");
		ssInSaveAt = a.Here;
		a.Raw(0x53, 0x33, 0xC9);                          // push ebx / xor ecx,ecx
		a.Mark("ss_il");
		a.Raw(0x0F, 0xB6, 0x06, 0x3B, 0xC8);              // movzx eax,byte ptr [esi] / cmp ecx,eax
		a.J8(0x73, "ss_ino");
		a.Raw(0x8B, 0x5E, 0x0C);                          // mov ebx,[esi+0Ch]
		a.Raw(0x8B, 0x04, 0xCB, 0x3B, 0x02);              // mov eax,[ebx+ecx*8] / cmp eax,[edx]
		a.J8(0x75, "ss_inext");
		a.Raw(0x8B, 0x44, 0xCB, 0x04, 0x3B, 0x42, 0x04);  // mov eax,[ebx+ecx*8+4] / cmp eax,[edx+4]
		a.J8(0x74, "ss_iret");
		a.Mark("ss_inext");
		a.Raw(0x41); a.J8(0xEB, "ss_il");
		a.Mark("ss_ino");
		a.Raw(0x83, 0xC9, 0xFF);                          // or ecx,-1
		a.Mark("ss_iret");
		a.Raw(0x5B, 0xC3);                                // pop ebx / ret

		// ss_member: ebp = member table, edx = id* -> al = 1 if a member (slot in use) has that id
		a.Mark("ss_member");
		ssMemberAt = a.Here;
		a.Raw(0x53, 0x51, 0x33, 0xC9);                    // push ebx / push ecx / xor ecx,ecx
		a.Mark("ss_ml");
		a.Raw(0x3B, 0x8D, 0xC0, 0x07, 0x00, 0x00);        // cmp ecx,[ebp+7C0h]
		a.J8(0x73, "ss_mno");
		a.Raw(0x69, 0xD9, 0xF8, 0x00, 0x00, 0x00, 0x03, 0xDD);   // imul ebx,ecx,0F8h / add ebx,ebp
		a.Raw(0xF6, 0x83, 0xF5, 0x00, 0x00, 0x00, 0x04);  // test byte ptr [ebx+0F5h],4
		a.J8(0x74, "ss_mnext");
		a.Raw(0x8B, 0x03, 0x3B, 0x02);                    // mov eax,[ebx] / cmp eax,[edx]
		a.J8(0x75, "ss_mnext");
		a.Raw(0x8B, 0x43, 0x04, 0x3B, 0x42, 0x04);        // mov eax,[ebx+4] / cmp eax,[edx+4]
		a.J8(0x74, "ss_myes");
		a.Mark("ss_mnext");
		a.Raw(0x41); a.J8(0xEB, "ss_ml");
		a.Mark("ss_mno");
		a.Raw(0x33, 0xC0); a.J8(0xEB, "ss_mret");         // xor eax,eax
		a.Mark("ss_myes");
		a.Raw(0xB0, 0x01);                                // mov al,1
		a.Mark("ss_mret");
		a.Raw(0x59, 0x5B, 0xC3);                          // pop ecx / pop ebx / ret
		a.Resolve();
		return at;
	}

	// ---- missing players: AI holds their faction until they come back ------------------------------------
	// A faction of the resumed save is "reserved" when its player isn't in the session and no newcomer took it
	// (the faction lookup gives newcomers the free factions in order, so the first N free ones are taken).
	// - Save: the save's player list (FUN_10eebc70, session+8 vtable +60h) is built from the member table only.
	//   Its count / entry calls now also return a stand-in member for each reserved faction (owner's Steam id,
	//   faction key, slot bytes, empty name), so the next save still lists the absent player with their faction.
	// - After loading (host, once per campaign, when the campaign map runs): every reserved faction that is
	//   human is handed to the AI, and every faction that has a player but is AI-controlled (its player came back)
	//   is handed back, with the engine's own synced command CCQ_FACTION_SWITCH_HUMAN_TO_AI (FUN_10f25180, the
	//   path the game uses when a player drops out of a campaign; the command flips human <-> AI).
	const uint MemberCountCall1 = 0x10EEBD77;      // call FUN_101f39d0 (ecx = member table): count
	const uint MemberCountCall2 = 0x10EEC13A;
	const uint MemberCount = 0x101F39D0;
	const uint MemberEntryCall = 0x10EEBD87;       // call FUN_10f14e30 (ecx = member table; index) ret 4
	const uint MemberEntry = 0x10F14E30;
	const uint SwitchHumanAi = 0x10F25180;         // thiscall(session; faction key*) ret 4: host queues the command
	const uint MpLobbyState = 0x10EF1B40;          // thiscall([session+596F0h]) -> 3 in a running game
	const uint FactionKey = 0x105ECEF0;            // thiscall(faction) -> key*
	const uint WStrEquals = 0x100EE2C0;            // thiscall(wstring*; wstring*) ret 4 -> al
	const uint CampaignUiVa = 0x11BC8FE0;          // [[[+0]+0A98h]+8] = campaign model, [model+1498h] = factions
	const uint ReconcileEnvVa = 0x11CB1800;        // campaign env already handled
	const uint ReconcileWaitVa = 0x11CB1804;       // frames since the campaign map came up
	const uint PhantomVa = 0x11CB1810;             // 100h: stand-in member entry
	const int ReconcileWaitFrames = 120;
	static uint reservedTestAt;

	// esi = save info, ebp = member table, ecx = slot -> al = 1 if reserved; keeps ebx, ecx, esi, edi, ebp
	static uint EmitReservedTest(Asm a) {
		uint at = a.Here;
		a.Raw(0x53, 0x57, 0x51);                          // push ebx / push edi / push ecx     ([esp] = slot)
		a.Raw(0x8B, 0x56, 0x0C, 0x8D, 0x14, 0xCA);        // mov edx,[esi+0Ch] / lea edx,[edx+ecx*8]
		a.Call(ssMemberAt);
		a.Raw(0x84, 0xC0); a.J8(0x75, "rt_no");           // its player is here
		a.Raw(0x33, 0xFF, 0x33, 0xDB);                    // xor edi,edi (newcomers) / xor ebx,ebx
		a.Mark("rt_nl");
		a.Raw(0x3B, 0x9D, 0xC0, 0x07, 0x00, 0x00);        // cmp ebx,[ebp+7C0h]
		a.J8(0x73, "rt_ndone");
		a.Raw(0x69, 0xD3, 0xF8, 0x00, 0x00, 0x00, 0x03, 0xD5);   // imul edx,ebx,0F8h / add edx,ebp
		a.Raw(0xF6, 0x82, 0xF5, 0x00, 0x00, 0x00, 0x04);  // test byte ptr [edx+0F5h],4
		a.J8(0x74, "rt_nnext");
		a.Call(ssInSaveAt);                               // ecx = slot or -1
		a.Raw(0x85, 0xC9); a.J8(0x79, "rt_nnext");
		a.Raw(0x47);                                      // inc edi
		a.Mark("rt_nnext");
		a.Raw(0x43); a.J8(0xEB, "rt_nl");                 // inc ebx
		a.Mark("rt_ndone");
		a.Raw(0x33, 0xDB);                                // xor ebx,ebx
		a.Mark("rt_rl");
		a.Raw(0x3B, 0x1C, 0x24); a.J8(0x73, "rt_rdone");  // cmp ebx,[esp] / jae
		a.Raw(0x8B, 0x56, 0x0C, 0x8D, 0x14, 0xDA);        // mov edx,[esi+0Ch] / lea edx,[edx+ebx*8]
		a.Call(ssMemberAt);
		a.Raw(0x84, 0xC0); a.J8(0x75, "rt_rnext");
		a.Raw(0x4F);                                      // dec edi    (a free faction before this one)
		a.Mark("rt_rnext");
		a.Raw(0x43); a.J8(0xEB, "rt_rl");
		a.Mark("rt_rdone");
		a.Raw(0x85, 0xFF); a.J8(0x7F, "rt_no");           // test edi,edi / jg: a newcomer gets this one
		a.Raw(0xB0, 0x01); a.J8(0xEB, "rt_out");
		a.Mark("rt_no");
		a.Raw(0x33, 0xC0);
		a.Mark("rt_out");
		a.Raw(0x59, 0x5F, 0x5B, 0xC3);                    // pop ecx / pop edi / pop ebx / ret
		a.Resolve();
		return at;
	}

	// replaces the count call: ecx = member table -> members + reserved factions
	static uint EmitMemberCountPlus(Asm a, uint reserved) {
		uint at = a.Here;
		a.Raw(0x56, 0x55, 0x57, 0x51);                    // push esi/ebp/edi/ecx
		a.Raw(0x8B, 0xE9, 0x8D, 0xB1, 0x3C, 0x08, 0x00, 0x00);   // mov ebp,ecx / lea esi,[ecx+83Ch]  (session+59730h)
		a.Raw(0x33, 0xFF, 0x33, 0xC9);                    // xor edi,edi / xor ecx,ecx
		a.Mark("mc_l");
		a.Raw(0x0F, 0xB6, 0x06, 0x3B, 0xC8); a.J8(0x73, "mc_d");   // movzx eax,byte [esi] / cmp ecx,eax
		a.Call(reserved);
		a.Raw(0x84, 0xC0); a.J8(0x74, "mc_n");
		a.Raw(0x47);                                      // inc edi
		a.Mark("mc_n");
		a.Raw(0x41); a.J8(0xEB, "mc_l");
		a.Mark("mc_d");
		a.Raw(0x8B, 0x85, 0xC0, 0x07, 0x00, 0x00, 0x03, 0xC7);   // mov eax,[ebp+7C0h] / add eax,edi
		a.Raw(0x59, 0x5F, 0x5D, 0x5E, 0xC3);              // pop ecx/edi/ebp/esi / ret
		a.Resolve();
		return at;
	}

	// replaces the entry call: thiscall(member table; index) ret 4; past the members -> stand-in for the
	// (index - members)-th reserved faction
	static uint EmitMemberEntryPlus(Asm a, uint reserved) {
		uint at = a.Here;
		a.Raw(0x8B, 0x44, 0x24, 0x04, 0x3B, 0x81, 0xC0, 0x07, 0x00, 0x00);   // mov eax,[esp+4] / cmp eax,[ecx+7C0h]
		a.J8(0x73, "me_ph");
		a.Jmp(MemberEntry);                               // a real member: the original
		a.Mark("me_ph");
		a.Raw(0x56, 0x55, 0x57, 0x53);                    // push esi/ebp/edi/ebx
		a.Raw(0x8B, 0xE9, 0x8D, 0xB1, 0x3C, 0x08, 0x00, 0x00);   // mov ebp,ecx / lea esi,[ecx+83Ch]
		a.Raw(0x2B, 0x81, 0xC0, 0x07, 0x00, 0x00, 0x8B, 0xF8);   // sub eax,[ecx+7C0h] / mov edi,eax
		a.Raw(0x33, 0xC9);
		a.Mark("me_l");
		a.Raw(0x0F, 0xB6, 0x06, 0x3B, 0xC8); a.J8(0x73, "me_none");
		a.Call(reserved);
		a.Raw(0x84, 0xC0); a.J8(0x74, "me_n");
		a.Raw(0x85, 0xFF); a.J8(0x74, "me_build");
		a.Raw(0x4F);
		a.Mark("me_n");
		a.Raw(0x41); a.J8(0xEB, "me_l");
		a.Mark("me_none");
		a.Raw(0x33, 0xC0); a.J32("me_out", 0xE9);
		a.Mark("me_build");                               // ecx = slot
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint B = a.Here;
		a.Raw(0x5B, 0x81, 0xC3); Imm(a, PhantomVa - B);   // pop ebx / add ebx, stand-in - B
		a.Raw(0x8B, 0x56, 0x0C);                          // mov edx,[esi+0Ch]
		a.Raw(0x8B, 0x04, 0xCA, 0x89, 0x03);              // mov eax,[edx+ecx*8] / mov [ebx],eax       (Steam id)
		a.Raw(0x8B, 0x44, 0xCA, 0x04, 0x89, 0x43, 0x04);  // mov eax,[edx+ecx*8+4] / mov [ebx+4],eax
		a.Raw(0x6B, 0xC1, 0x0C, 0x03, 0x46, 0x18);        // imul eax,ecx,0Ch / add eax,[esi+18h]       (faction key)
		a.Raw(0x8B, 0x10, 0x89, 0x93, 0xA4, 0x00, 0x00, 0x00);   // [ebx+0A4h..0AFh] = key (read-only view)
		a.Raw(0x8B, 0x50, 0x04, 0x89, 0x93, 0xA8, 0x00, 0x00, 0x00);
		a.Raw(0x8B, 0x50, 0x08, 0x89, 0x93, 0xAC, 0x00, 0x00, 0x00);
		a.Raw(0x33, 0xD2);                                // name: empty string at +0D4h
		a.Raw(0x89, 0x93, 0xD4, 0x00, 0x00, 0x00, 0x89, 0x93, 0xD8, 0x00, 0x00, 0x00, 0x89, 0x9B, 0xDC, 0x00, 0x00, 0x00);
		a.Raw(0xC6, 0x83, 0xF5, 0x00, 0x00, 0x00, 0x04);  // mov byte ptr [ebx+0F5h],4   (in use)
		a.Raw(0x33, 0xC0);                                // slot bytes -> [+0F6h] = b24 & 7 | (b30 & 7) << 3
		a.Raw(0x8B, 0x56, 0x24, 0x85, 0xD2); a.J8(0x74, "me_b30");
		a.Raw(0x0F, 0xB6, 0x04, 0x0A, 0x83, 0xE0, 0x07);  // movzx eax,byte ptr [edx+ecx] / and eax,7
		a.Mark("me_b30");
		a.Raw(0x8B, 0x56, 0x30, 0x85, 0xD2); a.J8(0x74, "me_bset");
		a.Raw(0x0F, 0xB6, 0x14, 0x0A, 0x83, 0xE2, 0x07, 0xC1, 0xE2, 0x03, 0x0B, 0xC2);   // movzx edx,.. / and / shl 3 / or
		a.Mark("me_bset");
		a.Raw(0x88, 0x83, 0xF6, 0x00, 0x00, 0x00);        // mov [ebx+0F6h],al
		a.Raw(0x8B, 0xC3);                                // mov eax,ebx
		a.Mark("me_out");
		a.Raw(0x5B, 0x5F, 0x5D, 0x5E, 0xC2, 0x04, 0x00);  // pop ebx/edi/ebp/esi / ret 4
		a.Resolve();
		return at;
	}

	// every frame (frame hook pushad block, [ebp+58h] = session): once per campaign, after the map has run for
	// a moment, the host hands reserved factions to the AI and gives returning players their faction back
	static void EmitReserveReconcile(Asm a, uint reserved) {
		a.Raw(0x60);                                      // pushad
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint B = a.Here;
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0x8B, 0x7D, 0x58, 0x85, 0xFF); a.J32("rc_done", 0x0F, 0x84);   // mov edi,[ebp+58h] (session)
		a.Raw(0x8B, 0x87, 0xD8, 0x96, 0x05, 0x00);        // mov eax,[edi+596D8h]   (campaign env)
		a.Raw(0x85, 0xC0); a.J8(0x75, "rc_have");
		a.Raw(0x89, 0x83); Imm(a, ReconcileEnvVa - B);    // mov [env],eax (0)
		a.Raw(0x89, 0x83); Imm(a, ReconcileWaitVa - B);   // mov [wait],eax (0)
		a.J32("rc_done", 0xE9);
		a.Mark("rc_have");
		a.Raw(0x3B, 0x83); Imm(a, ReconcileEnvVa - B); a.J32("rc_done", 0x0F, 0x84);   // handled already
		a.Raw(0x83, 0xBF, 0xD0, 0x96, 0x05, 0x00, 0x00); a.J32("rc_done", 0x0F, 0x85); // in a battle
		a.Raw(0x8B, 0x8F, 0xF0, 0x96, 0x05, 0x00, 0x85, 0xC9); a.J32("rc_done", 0x0F, 0x84);
		a.Call(MpLobbyState);
		a.Raw(0x83, 0xF8, 0x03); a.J32("rc_done", 0x0F, 0x85);   // game running
		a.Raw(0x8B, 0x83); Imm(a, CampaignUiVa - B);      // mov eax,[campaign UI]
		a.Raw(0x85, 0xC0); a.J32("rc_done", 0x0F, 0x84);
		a.Raw(0x8B, 0x80, 0x98, 0x0A, 0x00, 0x00, 0x85, 0xC0); a.J32("rc_done", 0x0F, 0x84);
		a.Raw(0x83, 0x78, 0x70, 0x00); a.J32("rc_done", 0x0F, 0x84);   // campaign map up
		a.Raw(0x8B, 0x40, 0x08, 0x85, 0xC0); a.J32("rc_done", 0x0F, 0x84);
		a.Raw(0x8B, 0x80, 0x98, 0x14, 0x00, 0x00, 0x85, 0xC0); a.J32("rc_done", 0x0F, 0x84);   // factions
		a.Raw(0x8B, 0xF0);                                // mov esi,eax  (factions, for now)
		a.Raw(0xFF, 0x83); Imm(a, ReconcileWaitVa - B);   // inc dword [wait]
		a.Raw(0x81, 0xBB); Imm(a, ReconcileWaitVa - B); Imm(a, (uint)ReconcileWaitFrames);
		a.J32("rc_done", 0x0F, 0x82);                     // jb: give the map a moment
		a.Raw(0x8B, 0x87, 0xD8, 0x96, 0x05, 0x00);        // mov eax,[edi+596D8h]
		a.Raw(0x89, 0x83); Imm(a, ReconcileEnvVa - B);    // handled (whatever happens next)
		a.Raw(0x56);                                      // push esi                ([esp] = factions)
		a.Raw(0x8B, 0xCF); a.Call(SessionIsHost);
		a.Raw(0x84, 0xC0); a.J32("rc_pop1", 0x0F, 0x84);  // only the host sends commands
		a.Raw(0x8D, 0xB7, 0x30, 0x97, 0x05, 0x00);        // lea esi,[edi+59730h]  (save info)
		a.Raw(0x8D, 0xAF, 0xF4, 0x8E, 0x05, 0x00);        // lea ebp,[edi+58EF4h]  (member table)
		a.Raw(0x33, 0xC9);                                // slot 0
		a.Mark("rc_sl");
		a.Raw(0x0F, 0xB6, 0x06, 0x3B, 0xC8); a.J32("rc_pop1", 0x0F, 0x83);
		a.Raw(0x51);                                      // push ecx                ([esp] = slot, [esp+4] = factions)
		a.Call(reserved);
		a.Raw(0x0F, 0xB6, 0xC0, 0x50);                    // movzx eax,al / push eax ([esp] = want AI)
		a.Raw(0x6B, 0xC1, 0x0C, 0x03, 0x46, 0x18, 0x50);  // imul eax,ecx,0Ch / add eax,[esi+18h] / push eax  ([esp] = key)
		a.Raw(0x33, 0xDB);                                // faction index
		a.Mark("rc_fl");
		a.Raw(0x8B, 0x54, 0x24, 0x0C);                    // mov edx,[esp+0Ch]      (factions)
		a.Raw(0x3B, 0x5A, 0x40); a.J8(0x73, "rc_next");   // cmp ebx,[edx+40h]: not found
		a.Raw(0x8B, 0x42, 0x44, 0x8B, 0x0C, 0x98);        // mov eax,[edx+44h] / mov ecx,[eax+ebx*4]
		a.Raw(0x51);                                      // push ecx                (faction)
		a.Call(FactionKey);                               // eax = its key
		a.Raw(0xFF, 0x74, 0x24, 0x04, 0x8B, 0xC8);        // push [esp+4] (our key) / mov ecx,eax
		a.Call(WStrEquals);
		a.Raw(0x59);                                      // pop ecx                 (faction)
		a.Raw(0x84, 0xC0); a.J8(0x75, "rc_found");
		a.Raw(0x43); a.J8(0xEB, "rc_fl");
		a.Mark("rc_found");
		a.Raw(0x33, 0xD2, 0x80, 0xB9, 0xA0, 0x06, 0x00, 0x00, 0x00, 0x0F, 0x94, 0xC2);   // xor edx,edx / cmp byte [ecx+6A0h],0 / sete dl (AI now)
		a.Raw(0x3B, 0x54, 0x24, 0x04); a.J8(0x74, "rc_next");     // already as wanted
		a.Raw(0xFF, 0x34, 0x24, 0x8B, 0xCF);              // push [esp] (key) / mov ecx,edi
		a.Call(SwitchHumanAi);
		a.Mark("rc_next");
		a.Raw(0x83, 0xC4, 0x08, 0x59, 0x41);              // add esp,8 / pop ecx / inc ecx
		a.J32("rc_sl", 0xE9);
		a.Mark("rc_pop1");
		a.Raw(0x83, 0xC4, 0x04);                          // drop factions
		a.Mark("rc_done");
		a.Raw(0x61);                                      // popad
		a.Resolve();
	}

	// ---- "Play as AI" with 3+ players (v1.13) --------------------------------------------------------------
	// A player not in a battle can "Play as AI" (CCQ_SET_PENDING_BATTLE_READY_TO_START, choice 4; spectate = 3).
	// The pending battle keeps one entry per human ([pb+0F8h]: count [+8], 20h-byte entries [+0Ch]: faction key
	// [+0], choice [+0Ch]). FUN_105cbb50 ("a human plays the AI army") tested exactly 2 entries: entry 0 = 1,
	// entry 1 = 4. With 3+ humans it was never true, so the chooser never got the AI army; FUN_105f8b20 (gives the
	// AI army to a human) also required exactly 2 humans. And where it was true, FUN_105cefa0 (what a player
	// outside the battle controls) and FUN_105b7b90 (a clan's battle side) gave the AI army to EVERY non-fighter.
	//   - FUN_105cbb50 -> any entry chose 4 and any entry chose 1 (2+ entries).
	//   - FUN_105f8b20: humans == 2 -> >= 2.
	//   - FUN_105cefa0 / FUN_105b7b90: only the first entry that chose 4 gets the AI army; any other player outside
	//     the battle follows it as before.
	const uint AiPlayedCheck = 0x105CBB50;          // fastcall(pb+0F8h) -> al; 6 bytes replaced by a jmp
	const uint AiArmyHumansJump = 0x105F8BC5;       // cmp ebx,2 / jne (75 23) -> jb
	const uint NonParticipantEntry = 0x105CEFA0;    // sub esp,0Ch / push esi / push edi (5 bytes); [esp+8] = clan
	const uint NonParticipantEntryResume = 0x105CEFA5;
	static readonly uint[] NonParticipantAiCalls = { 0x105CF092, 0x105CF0C2 };
	const uint SideOfClanAiCall = 0x105B7BB1;       // in FUN_105b7b90; clan = [esp+10h] in the stub
	const uint AskingClanVa = 0x11CB1910;

	static uint EmitAiPlayedCheck(Asm a) {
		// ecx = choice list -> al = some human chose "play as AI" (4) and one fights (1); keeps all but eax
		uint at = a.Here;
		a.Raw(0x52, 0x51);                                // push edx / push ecx
		a.Raw(0x33, 0xD2);                                // dl = seen 4, dh = seen 1
		a.Raw(0x8B, 0x41, 0x08, 0x83, 0xF8, 0x02); a.J8(0x72, "ap_out");   // fewer than 2 entries
		a.Raw(0x8B, 0x49, 0x0C);                          // mov ecx,[ecx+0Ch]
		a.Mark("ap_l");
		a.Raw(0x83, 0x79, 0x0C, 0x04); a.J8(0x75, "ap_n4");
		a.Raw(0xB2, 0x01);                                // mov dl,1
		a.Mark("ap_n4");
		a.Raw(0x83, 0x79, 0x0C, 0x01); a.J8(0x75, "ap_n1");
		a.Raw(0xB6, 0x01);                                // mov dh,1
		a.Mark("ap_n1");
		a.Raw(0x83, 0xC1, 0x20, 0x48); a.J8(0x75, "ap_l");   // add ecx,20h / dec eax
		a.Mark("ap_out");
		a.Raw(0x84, 0xD2, 0x0F, 0x95, 0xC0);              // test dl,dl / setne al
		a.Raw(0x84, 0xF6, 0x0F, 0x95, 0xC2, 0x22, 0xC2);  // test dh,dh / setne dl / and al,dl
		a.Raw(0x59, 0x5A, 0xC3);                          // pop ecx / pop edx / ret
		a.Resolve();
		return at;
	}

	// ecx = choice list, edx = clan -> al = this clan is the (first) one playing the AI army
	static uint EmitAiChooserTest(Asm a, uint aiPlayed) {
		uint at = a.Here;
		a.Call(aiPlayed);
		a.Raw(0x84, 0xC0); a.J8(0x75, "ac_who");
		a.Raw(0xC3);
		a.Mark("ac_who");
		a.Raw(0x53, 0x56, 0x57, 0x51);                    // push ebx/esi/edi/ecx
		a.Raw(0x8B, 0xFA);                                // mov edi,edx       (clan)
		a.Raw(0x8B, 0x59, 0x08, 0x8B, 0x71, 0x0C);        // ebx = count / esi = entries
		a.Mark("ac_l");
		a.Raw(0x85, 0xDB); a.J8(0x74, "ac_no");
		a.Raw(0x83, 0x7E, 0x0C, 0x04); a.J8(0x74, "ac_first");
		a.Raw(0x83, 0xC6, 0x20, 0x4B); a.J8(0xEB, "ac_l");   // add esi,20h / dec ebx
		a.Mark("ac_first");                               // the first "play as AI" entry: is it this clan?
		a.Raw(0x8B, 0xCF); a.Call(FactionKey);            // eax = clan's key
		a.Raw(0x56, 0x8B, 0xC8); a.Call(WStrEquals);      // push entry key / ecx = clan key
		a.J8(0xEB, "ac_out");
		a.Mark("ac_no");
		a.Raw(0x32, 0xC0);
		a.Mark("ac_out");
		a.Raw(0x59, 0x5F, 0x5E, 0x5B, 0xC3);              // pop ecx/edi/esi/ebx / ret
		a.Resolve();
		return at;
	}

	static uint EmitNonParticipantEntry(Asm a) {
		uint at = a.Here;
		a.Raw(0x8B, 0x44, 0x24, 0x08);                    // mov eax,[esp+8]   (clan asked about)
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);
		uint B = a.Here;
		a.Raw(0x5A, 0x89, 0x82); Imm(a, AskingClanVa - B);   // pop edx / mov [edx+clan],eax
		a.Raw(0x83, 0xEC, 0x0C, 0x56, 0x57);              // sub esp,0Ch / push esi / push edi   (replaced)
		a.Jmp(NonParticipantEntryResume);
		a.Resolve();
		return at;
	}

	// replaces "call FUN_105cbb50" in FUN_105cefa0 (clan saved at its entry)
	static uint EmitNonParticipantAiCall(Asm a, uint chooser) {
		uint at = a.Here;
		a.Raw(0x52);                                      // push edx
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);
		uint B = a.Here;
		a.Raw(0x5A, 0x8B, 0x92); Imm(a, AskingClanVa - B);   // pop edx / mov edx,[edx+clan]
		a.Call(chooser);
		a.Raw(0x5A, 0xC3);                                // pop edx / ret
		a.Resolve();
		return at;
	}

	// replaces "call FUN_105cbb50" in FUN_105b7b90 (clan = its first argument, [esp+10h] here)
	static uint EmitSideOfClanAiCall(Asm a, uint chooser) {
		uint at = a.Here;
		a.Raw(0x52, 0x8B, 0x54, 0x24, 0x14);              // push edx / mov edx,[esp+14h]
		a.Call(chooser);
		a.Raw(0x5A, 0xC3);
		return at;
	}

	// ---- default save name with 3+ players ---------------------------------------------------------------
	// FUN_106a3a30 names a multiplayer save "Host (Clan) vs. Other (Clan): date" (or the co-op form) from the
	// save's player list, which only ever shows two players. With 3+ players the name is now
	// "Ann, Bob, Cat, Dan: date" (absent players' stand-ins have no name and are left out; long lists end in "...").
	// Hook: the final assign (call FUN_100edbe0, ecx = output, [esp+4] = name); the player list is the local
	// header at [esp+5Ch] (count [+30h], names data [+3Ch]) and the date string is at [esp+50h].
	const uint SaveNameAssignCall = 0x106A3DFD;
	const uint WStrAssign = 0x100EDBE0;            // thiscall(wstring*; const wstring*) ret 4

	static uint EmitSaveName(Asm a) {
		uint at = a.Here;
		a.Raw(0x80, 0xBC, 0x24, 0x8C, 0x00, 0x00, 0x00, 0x02);   // cmp byte ptr [esp+8Ch],2
		a.J8(0x77, "sn_build");
		a.Jmp(WStrAssign);                                // 1-2 players: the game's own name
		a.Mark("sn_build");
		a.Raw(0x60);                                      // pushad
		a.Raw(0x81, 0xEC, 0x20, 0x02, 0x00, 0x00);        // sub esp,220h  ([esp] text, [esp+200h] wstring)
		a.Raw(0x0F, 0xB6, 0xAC, 0x24, 0xCC, 0x02, 0x00, 0x00);   // movzx ebp,byte ptr [esp+2CCh]  (players)
		a.Raw(0x8B, 0xB4, 0x24, 0xD8, 0x02, 0x00, 0x00);  // mov esi,[esp+2D8h]   (names)
		a.Raw(0x8B, 0xFC, 0x33, 0xDB);                    // mov edi,esp / xor ebx,ebx
		a.Mark("sn_nl");
		a.Raw(0x3B, 0xDD); a.J8(0x73, "sn_nd");           // cmp ebx,ebp / jae
		a.Raw(0x6B, 0xC3, 0x0C, 0x03, 0xC6);              // imul eax,ebx,0Ch / add eax,esi
		a.Raw(0x8B, 0x08, 0x85, 0xC9); a.J8(0x74, "sn_nn");   // mov ecx,[eax] / test: no name
		a.Raw(0x8B, 0x50, 0x08);                          // mov edx,[eax+8]
		a.Raw(0x8B, 0xC7, 0x2B, 0xC4);                    // mov eax,edi / sub eax,esp  (bytes so far)
		a.Raw(0x3D, 0x2C, 0x01, 0x00, 0x00); a.J8(0x72, "sn_room");   // cmp eax,300 (150 chars)
		a.Raw(0xC7, 0x07, 0x2E, 0x00, 0x2E, 0x00, 0x66, 0xC7, 0x47, 0x04, 0x2E, 0x00);   // "..."
		a.Raw(0x83, 0xC7, 0x06); a.J8(0xEB, "sn_nd");
		a.Mark("sn_room");
		a.Raw(0x85, 0xC0); a.J8(0x74, "sn_first");
		a.Raw(0xC7, 0x07, 0x2C, 0x00, 0x20, 0x00, 0x83, 0xC7, 0x04);   // ", "
		a.Mark("sn_first");
		a.Raw(0x83, 0xF9, 0x28); a.J8(0x76, "sn_c1");     // at most 40 chars of a name
		a.Raw(0xB9, 0x28, 0x00, 0x00, 0x00);
		a.Mark("sn_c1");
		a.Raw(0x56, 0x8B, 0xF2, 0xF3, 0x66, 0xA5, 0x5E);  // push esi / mov esi,edx / rep movsw / pop esi
		a.Mark("sn_nn");
		a.Raw(0x43); a.J8(0xEB, "sn_nl");                 // inc ebx
		a.Mark("sn_nd");
		a.Raw(0xC7, 0x07, 0x3A, 0x00, 0x20, 0x00, 0x83, 0xC7, 0x04);   // ": "
		a.Raw(0x8D, 0x84, 0x24, 0x90, 0x02, 0x00, 0x00);  // lea eax,[esp+290h]   (date)
		a.Raw(0x8B, 0x08, 0x8B, 0x70, 0x08);              // mov ecx,[eax] / mov esi,[eax+8]
		a.Raw(0x83, 0xF9, 0x28); a.J8(0x76, "sn_c2");
		a.Raw(0xB9, 0x28, 0x00, 0x00, 0x00);
		a.Mark("sn_c2");
		a.Raw(0xF3, 0x66, 0xA5);                          // rep movsw
		a.Raw(0x8B, 0xCF, 0x2B, 0xCC, 0xD1, 0xE9);        // mov ecx,edi / sub ecx,esp / shr ecx,1  (length)
		a.Raw(0x51, 0x8D, 0x44, 0x24, 0x04, 0x50);        // push ecx / lea eax,[esp+4] / push eax
		a.Raw(0x8D, 0x8C, 0x24, 0x08, 0x02, 0x00, 0x00);  // lea ecx,[esp+208h]
		a.Call(WStrFromRange);
		a.Raw(0x8D, 0x84, 0x24, 0x00, 0x02, 0x00, 0x00, 0x50);   // lea eax,[esp+200h] / push eax
		a.Raw(0x8B, 0x8C, 0x24, 0x3C, 0x02, 0x00, 0x00);  // mov ecx,[esp+23Ch]   (output: pushad's ecx)
		a.Call(WStrAssign);
		a.Raw(0x8D, 0x8C, 0x24, 0x00, 0x02, 0x00, 0x00);
		a.Call(WStrDtor);
		a.Raw(0x81, 0xC4, 0x20, 0x02, 0x00, 0x00, 0x61, 0xC2, 0x04, 0x00);   // add esp,220h / popad / ret 4
		a.Resolve();
		return at;
	}

	// The resume lobby (FUN_10f2c830, edi = session, ebp = member) also gives a joining player two per-slot
	// bytes of the save by Steam id: FUN_10ef0b30 ([info+24h][slot]) and FUN_10ef0ae0 ([info+30h][slot]); not in
	// the save -> 0. Both now use the slot the faction lookup above assigns, so a newcomer gets the values of
	// the faction they take over.
	const uint SaveByte24Call = 0x10F2C8AC;        // call FUN_10ef0b30: thiscall(info; steam id*) ret 4
	const uint SaveByte30Call = 0x10F2C8BE;        // call FUN_10ef0ae0
	const uint SaveByte24 = 0x10EF0B30;
	const uint SaveByte30 = 0x10EF0AE0;

	static uint EmitSaveSlotByte(Asm a, uint core, out uint at30) {
		uint at24 = a.Here;
		a.Raw(0xBA, 0x24, 0x00, 0x00, 0x00);              // mov edx,24h
		a.J8(0xEB, "sb_core");
		at30 = a.Here;
		a.Raw(0xBA, 0x30, 0x00, 0x00, 0x00);              // mov edx,30h
		a.Mark("sb_core");
		a.Raw(0x56, 0x53);                                // push esi / push ebx
		a.Raw(0x8B, 0xDA, 0x8B, 0xF1);                    // mov ebx,edx / mov esi,ecx
		a.Raw(0x6A, 0x00);                                // push 0                 (is_host)
		a.Raw(0xFF, 0x74, 0x24, 0x10);                    // push [esp+10h]         (steam id*)
		a.Raw(0x8B, 0xC7);                                // mov eax,edi            (session)
		a.Call(core);                                     // eax = faction record (ecx = info)
		a.Raw(0x2B, 0x46, 0x18);                          // sub eax,[esi+18h]
		a.Raw(0x0F, 0xB6, 0x0E, 0x6B, 0xC9, 0x0C);        // movzx ecx,byte ptr [esi] / imul ecx,ecx,0Ch
		a.Raw(0x3B, 0xC1); a.J8(0x73, "sb_zero");         // cmp eax,ecx / jae: no faction
		a.Raw(0x33, 0xD2, 0xB9, 0x0C, 0x00, 0x00, 0x00);  // xor edx,edx / mov ecx,0Ch
		a.Raw(0xF7, 0xF1);                                // div ecx                (slot)
		a.Raw(0x8B, 0x0C, 0x1E);                          // mov ecx,[esi+ebx]
		a.Raw(0x0F, 0xB6, 0x04, 0x01);                    // movzx eax,byte ptr [ecx+eax]
		a.J8(0xEB, "sb_done");
		a.Mark("sb_zero");
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Mark("sb_done");
		a.Raw(0x5B, 0x5E, 0xC2, 0x04, 0x00);              // pop ebx / pop esi / ret 4
		a.Resolve();
		return at24;
	}

	// ---- gifted units with several watchers -------------------------------------------------------------
	// A watching player ("ally spectate", setup+281E9h) decides "is this unit mine?" with FUN_110de560, which
	// returns the FIRST spectator. Right with one watcher; with several, every watcher used spectator #1's unit
	// list: all of them could command units given to spectator #1, and units given to anyone else belonged to
	// nobody (lost). These three call sites now use the mod's picker, whose watcher branch returns the local
	// player's own id (they are only reached when 281E9h is set).
	static readonly uint[] OwnerCheckCalls = { 0x110FCCAE, 0x11101142, 0x111011EE };   // gift arrived / may command (x2)

	static void Imm(Asm a, uint v) { a.Bytes.AddRange(BitConverter.GetBytes(v)); }

	static void EmitCrashFilter(Asm a) {
		var str = new Dictionary<string, uint>();
		Action<string, string> addStr = (key, text) => { str[key] = a.Here; a.Bytes.AddRange(Encoding.ASCII.GetBytes(text)); a.Raw(0); };
		addStr("head", "Shogun 2 8-player co-op mod v" + Version + " crash report\r\nexception ");
		addStr("at", " at ");
		addStr("base", "  empire.retail.dll base ");
		var regs = new[] { "eip", "eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp" };
		var regOff = new uint[] { 0xB8, 0xB0, 0xA4, 0xAC, 0xA8, 0xA0, 0x9C, 0xB4, 0xC4 };
		foreach (var r in regs) addStr(r, (r == "eip" ? "\r\n" : " ") + r + " ");
		addStr("stack", "\r\nstack:");
		addStr("nl", "\r\n");
		addStr("sp", " ");
		addStr("file", "eightplayer_crash.txt");
		addStr("dmp", "eightplayer_crash.dmp");
		addStr("dbghelp", "dbghelp.dll");
		addStr("mdwd", "MiniDumpWriteDump");

		// hex8: eax -> 8 hex digits at [edi], edi advanced; keeps ecx/edx
		uint hex8 = a.Here;
		a.Raw(0x51, 0x52, 0xB9, 0x08, 0x00, 0x00, 0x00);  // push ecx / push edx / mov ecx,8
		a.Mark("hx");
		a.Raw(0xC1, 0xC0, 0x04);                          // rol eax,4
		a.Raw(0x8B, 0xD0, 0x83, 0xE2, 0x0F);              // mov edx,eax / and edx,0Fh
		a.Raw(0x80, 0xFA, 0x0A);                          // cmp dl,10
		a.J8(0x72, "dig");                                // jb dig
		a.Raw(0x80, 0xC2, 0x07);                          // add dl,7
		a.Mark("dig");
		a.Raw(0x80, 0xC2, 0x30, 0x88, 0x17, 0x47);        // add dl,'0' / mov [edi],dl / inc edi
		a.Raw(0x49);                                      // dec ecx
		a.J8(0x75, "hx");                                 // jnz hx
		a.Raw(0x5A, 0x59, 0xC3);                          // pop edx / pop ecx / ret
		// puts: copy the C string at esi to [edi]; keeps ecx/edx
		uint puts = a.Here;
		a.Mark("pc");
		a.Raw(0x8A, 0x06, 0x46, 0x84, 0xC0);              // mov al,[esi] / inc esi / test al,al
		a.J8(0x74, "pd");                                 // jz pd
		a.Raw(0x88, 0x07, 0x47);                          // mov [edi],al / inc edi
		a.J8(0xEB, "pc");                                 // jmp pc
		a.Mark("pd");
		a.Raw(0xC3);
		a.Resolve();

		// LONG WINAPI filter(EXCEPTION_POINTERS* ep)
		crashFilterAt = a.Here;
		a.Raw(0x55, 0x8B, 0xEC);                          // push ebp / mov ebp,esp
		a.Raw(0x81, 0xEC, 0x20, 0x10, 0x00, 0x00);        // sub esp,1020h  (text buffer [ebp-1000h], locals below)
		a.Raw(0x60);                                      // pushad
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint popAt = a.Here;
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0x8D, 0xBD, 0x00, 0xF0, 0xFF, 0xFF);        // lea edi,[ebp-1000h]
		Action<string> puts1 = key => { a.Raw(0x8D, 0xB3); Imm(a, str[key] - popAt); a.Call(puts); };   // lea esi,[ebx+s] / call puts
		Action ep = () => a.Raw(0x8B, 0x45, 0x08);       // mov eax,[ebp+8]
		puts1("head");
		ep(); a.Raw(0x8B, 0x00, 0x8B, 0x00); a.Call(hex8);                    // ExceptionRecord->ExceptionCode
		puts1("at");
		ep(); a.Raw(0x8B, 0x00, 0x8B, 0x40, 0x0C); a.Call(hex8);              // ->ExceptionAddress
		puts1("base");
		a.Raw(0x8B, 0xC3, 0x2D); Imm(a, popAt - ImageBase); a.Call(hex8);     // mov eax,ebx / sub eax,(popAt - image base)
		for (int i = 0; i < regs.Length; i++) {
			puts1(regs[i]);
			ep(); a.Raw(0x8B, 0x40, 0x04, 0x8B, 0x80); Imm(a, regOff[i]); a.Call(hex8);   // ContextRecord->reg
		}
		puts1("stack");
		ep(); a.Raw(0x8B, 0x40, 0x04, 0x8B, 0x90, 0xC4, 0x00, 0x00, 0x00);    // mov edx,[ctx+0C4h] (Esp)
		// read at most up to the top of this thread's stack (TEB StackBase, fs:[4]), and at most 256 dwords
		a.Raw(0x64, 0xA1, 0x04, 0x00, 0x00, 0x00);        // mov eax,fs:[4]
		a.Raw(0x2B, 0xC2, 0xC1, 0xE8, 0x02);              // sub eax,edx / shr eax,2
		a.Raw(0x3D, 0x00, 0x01, 0x00, 0x00); a.J8(0x76, "lim");   // cmp eax,256 / jbe lim
		a.Raw(0xB8, 0x00, 0x01, 0x00, 0x00);              // mov eax,256
		a.Mark("lim");
		a.Raw(0x83, 0xF8, 0x01); a.J8(0x73, "lim1");      // at least 1 (esp itself is always readable)
		a.Raw(0xB8, 0x01, 0x00, 0x00, 0x00);
		a.Mark("lim1");
		a.Raw(0x89, 0x85, 0xF0, 0xEF, 0xFF, 0xFF);        // mov [ebp-1010h],eax  (dwords to dump)
		a.Raw(0x33, 0xC9);                                // xor ecx,ecx
		a.Mark("row");
		a.Raw(0xF6, 0xC1, 0x07);                          // test cl,7
		a.J8(0x75, "space");                              // jnz space
		puts1("nl");
		a.J8(0xEB, "word");
		a.Mark("space");
		puts1("sp");
		a.Mark("word");
		a.Raw(0x8B, 0x04, 0x8A); a.Call(hex8);            // mov eax,[edx+ecx*4]
		a.Raw(0x41, 0x3B, 0x8D, 0xF0, 0xEF, 0xFF, 0xFF);  // inc ecx / cmp ecx,[ebp-1010h]
		a.J8(0x72, "row");                                // jb row
		puts1("nl");
		// write the file
		a.Raw(0x8D, 0x85, 0x00, 0xF0, 0xFF, 0xFF);        // lea eax,[ebp-1000h]
		a.Raw(0x2B, 0xF8, 0x89, 0xBD, 0xFC, 0xEF, 0xFF, 0xFF);   // sub edi,eax / mov [ebp-1004h],edi (length)
		a.Raw(0x6A, 0x00, 0x68, 0x80, 0x00, 0x00, 0x00, 0x6A, 0x02, 0x6A, 0x00, 0x6A, 0x01);   // 0, NORMAL, CREATE_ALWAYS, 0, SHARE_READ
		a.Raw(0x68, 0x00, 0x00, 0x00, 0x40);              // push GENERIC_WRITE
		a.Raw(0x8D, 0xB3); Imm(a, str["file"] - popAt); a.Raw(0x56);   // lea esi,[ebx+file] / push esi
		a.Raw(0x8B, 0x83); Imm(a, CreateFileAIat - popAt); a.Raw(0xFF, 0xD0);   // call [CreateFileA]
		a.Raw(0x83, 0xF8, 0xFF);                          // cmp eax,-1
		a.J8(0x74, "nofile");
		a.Raw(0x8B, 0xF0);                                // mov esi,eax
		a.Raw(0x6A, 0x00);                                // push 0 (overlapped)
		a.Raw(0x8D, 0x8D, 0xF8, 0xEF, 0xFF, 0xFF, 0x51);  // lea ecx,[ebp-1008h] / push ecx (written)
		a.Raw(0xFF, 0xB5, 0xFC, 0xEF, 0xFF, 0xFF);        // push [ebp-1004h] (length)
		a.Raw(0x8D, 0x8D, 0x00, 0xF0, 0xFF, 0xFF, 0x51);  // lea ecx,[ebp-1000h] / push ecx
		a.Raw(0x56);                                      // push esi
		a.Raw(0x8B, 0x83); Imm(a, WriteFileIat - popAt); a.Raw(0xFF, 0xD0);
		a.Raw(0x56);
		a.Raw(0x8B, 0x83); Imm(a, CloseHandleIat - popAt); a.Raw(0xFF, 0xD0);
		a.Mark("nofile");
		// a small minidump (threads, stacks, modules: MiniDumpNormal) next to the text report
		a.Raw(0x8D, 0x83); Imm(a, str["dbghelp"] - popAt); a.Raw(0x50);          // lea eax,["dbghelp.dll"] / push eax
		a.Raw(0x8B, 0x83); Imm(a, LoadLibraryAIat - popAt); a.Raw(0xFF, 0xD0);   // call [LoadLibraryA]
		a.Raw(0x85, 0xC0); a.J32("nodump", 0x0F, 0x84);
		a.Raw(0x8D, 0x8B); Imm(a, str["mdwd"] - popAt); a.Raw(0x51, 0x50);       // lea ecx,["MiniDumpWriteDump"] / push ecx / push eax
		a.Raw(0x8B, 0x83); Imm(a, GetProcAddressIat - popAt); a.Raw(0xFF, 0xD0);
		a.Raw(0x85, 0xC0); a.J32("nodump", 0x0F, 0x84);
		a.Raw(0x8B, 0xF8);                                // mov edi,eax             (MiniDumpWriteDump)
		a.Raw(0x6A, 0x00, 0x68, 0x80, 0x00, 0x00, 0x00, 0x6A, 0x02, 0x6A, 0x00, 0x6A, 0x00);   // 0, NORMAL, CREATE_ALWAYS, 0, no sharing
		a.Raw(0x68, 0x00, 0x00, 0x00, 0x40);              // push GENERIC_WRITE
		a.Raw(0x8D, 0x83); Imm(a, str["dmp"] - popAt); a.Raw(0x50);
		a.Raw(0x8B, 0x83); Imm(a, CreateFileAIat - popAt); a.Raw(0xFF, 0xD0);
		a.Raw(0x83, 0xF8, 0xFF); a.J32("nodump", 0x0F, 0x84);
		a.Raw(0x8B, 0xF0);                                // mov esi,eax             (file)
		a.Raw(0x8B, 0x83); Imm(a, GetCurrentThreadIdIat - popAt); a.Raw(0xFF, 0xD0);
		a.Raw(0x89, 0x85, 0xE4, 0xEF, 0xFF, 0xFF);        // MINIDUMP_EXCEPTION_INFORMATION at [ebp-101Ch]: ThreadId
		a.Raw(0x8B, 0x45, 0x08, 0x89, 0x85, 0xE8, 0xEF, 0xFF, 0xFF);   // ExceptionPointers = ep
		a.Raw(0xC7, 0x85, 0xEC, 0xEF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00);   // ClientPointers = FALSE
		a.Raw(0x6A, 0x00, 0x6A, 0x00);                    // CallbackParam, UserStreamParam
		a.Raw(0x8D, 0x85, 0xE4, 0xEF, 0xFF, 0xFF, 0x50);  // &exception information
		a.Raw(0x6A, 0x00, 0x56);                          // MiniDumpNormal, file
		a.Raw(0x8B, 0x83); Imm(a, GetCurrentProcessIdIat - popAt); a.Raw(0xFF, 0xD0, 0x50);
		a.Raw(0x8B, 0x83); Imm(a, GetCurrentProcessIat - popAt); a.Raw(0xFF, 0xD0, 0x50);
		a.Raw(0xFF, 0xD7);                                // call MiniDumpWriteDump
		a.Raw(0x56);
		a.Raw(0x8B, 0x83); Imm(a, CloseHandleIat - popAt); a.Raw(0xFF, 0xD0);
		a.Mark("nodump");
		// end the process here: no hand-over to Steam's crash handler, which writes a 1-2 GB full memory dump
		a.Raw(0xC7, 0x85, 0xF4, 0xEF, 0xFF, 0xFF, 0x01, 0x00, 0x00, 0x00);   // result = EXCEPTION_EXECUTE_HANDLER
		a.Raw(0x61);                                      // popad
		a.Raw(0x8B, 0x85, 0xF4, 0xEF, 0xFF, 0xFF);        // mov eax,[ebp-100Ch]
		a.Raw(0x8B, 0xE5, 0x5D, 0xC2, 0x04, 0x00);        // mov esp,ebp / pop ebp / ret 4
		a.Resolve();
	}

	// every frame (from the frame hook): SetUnhandledExceptionFilter(crash filter). Steam's tier0 registers its
	// own handler after the mod's, which then never ran; re-registering each frame keeps the mod's first. A
	// pointer swap, nothing measurable per frame. [prev] keeps the last other filter seen (informational).
	static void EmitCrashFilterInstall(Asm a) {
		a.Raw(0x60);                                      // pushad
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint popAt = a.Here;
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0xC7, 0x83); Imm(a, CrashInstalledVa - popAt); Imm(a, 1);     // mov dword [installed],1
		a.Raw(0x8D, 0xB3); Imm(a, crashFilterAt - popAt); a.Raw(0x56);      // lea esi,[filter] / push esi
		a.Raw(0x8B, 0x83); Imm(a, SetUnhandledExceptionFilterIat - popAt); a.Raw(0xFF, 0xD0);
		a.Raw(0x3B, 0xC6); a.J8(0x74, "done");            // cmp eax,esi: ours was still in place
		a.Raw(0x89, 0x83); Imm(a, CrashPrevFilterVa - popAt);               // mov [prev],eax
		a.Mark("done");
		a.Raw(0x61);                                      // popad
		a.Resolve();
	}

	static uint EmitFramePin(Asm a) {
		// replaces "call FUN_10f36680" (ecx = its this): pin, then tail-call the original
		EmitCrashFilter(a);
		uint at = a.Here;
		EmitCrashFilterInstall(a);
		EmitStateWatch(a);
		EmitResyncKeyPoll(a);
		EmitPendingResync(a);
		EmitResyncWait(a);
		EmitReserveReconcile(a, reservedTestAt);
		EmitPinFloatModes(a);
		a.Jmp(MultiplayerUpdate);
		return at;
	}

	static uint EmitBattleTickPin(Asm a) {
		uint at = a.Here;
		EmitPinFloatModes(a);
		a.Raw(0xFF, 0x74, 0x24, 0x08);                    // push dword ptr [esp+8]  (replaced instructions)
		a.Raw(0x8B, 0x4C, 0x24, 0x10);                    // mov ecx,[esp+10h]
		a.Jmp(BattleTickResume);
		return at;
	}

	// ---- battle desyncs no longer drop the player ---------------------------------------------------------
	// A battle cannot be resynced, so when its checksums differ the engine disconnects (mp_desync_kick_enabled):
	// the desync handler FUN_10ee9a50 drops the local player, and FUN_10ef9a20 (another player reported a
	// desync) has the host kick the reported players / the reported client leave. Both now skip the kick
	// while a battle is running ([session+596D0h] = battle environment, the same test the handler uses to log
	// "mp_battle_desync"). The battle carries on; back on the campaign map any difference is caught by the
	// campaign checksums and the (automatic) campaign resync copies the host's state to everyone.
	// Campaign desyncs keep the original behaviour. The absolute flag reads (relocated) are left untouched.
	const uint DesyncKickAt = 0x10EE9A71;        // mov ecx,[edi+2BE74h] (6 bytes); edi = session+4
	const uint DesyncKickResume = 0x10EE9A77;
	const uint DesyncKickSkip = 0x10EE9A8D;
	const uint ReportKickAt = 0x10EF9A87;        // push ebp / push edi / lea eax,[esp+10h] (6 bytes); ebx = session
	const uint ReportKickResume = 0x10EF9A8D;
	const uint ReportKickSkip = 0x10EF9B29;

	// v1.9: no desync kicks anywhere in a campaign. Testers (7 players) were dropped right after a co-op battle:
	// back on the map the campaign desync was detected while the game was still switching back from the battle,
	// FUN_10f058d0 couldn't start a resync yet (it needs the campaign state), and the kick followed at once.
	// Now, in a campaign ([session+596D8h], also set during campaign battles) the kick is skipped and a
	// "resync pending" flag is set; the frame hook (EmitPendingResync) starts the resync as soon as the game is
	// back on the campaign map and tells every PC to join (as Ctrl+Shift+F9). Custom battles keep the original rules.
	const uint ResyncPendingVa = 0x11CB1540;     // fifth reserved .data dword

	static uint EmitBattleNoKick(Asm a) {
		uint at = a.Here;
		a.Raw(0x50, 0x33, 0xC0, 0x83, 0xBF, 0xCC, 0x96, 0x05, 0x00, 0x00, 0x0F, 0x95, 0xC0);   // push eax / eax = battle running
		EmitLog(a, "DESYNC detected on this PC (1 = during a battle, 0 = on the campaign map)", 1);
		a.Raw(0x58);                                      // pop eax
		a.Raw(0x83, 0xBF, 0xD4, 0x96, 0x05, 0x00, 0x00);  // cmp dword ptr [edi+596D4h],0   (campaign?  edi = session+4)
		a.J8(0x75, "campaign");
		a.Raw(0x83, 0xBF, 0xCC, 0x96, 0x05, 0x00, 0x00);  // cmp dword ptr [edi+596CCh],0   (battle running?)
		a.J32("skip", 0x0F, 0x85);                        // jne -> no kick
		a.Raw(0x8B, 0x8F, 0x74, 0xBE, 0x02, 0x00);        // mov ecx,[edi+2BE74h]          (replaced instruction)
		a.Jmp(DesyncKickResume);
		a.Mark("campaign");                               // no kick; resync as soon as possible
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint B = a.Here;
		a.Raw(0x58);                                      // pop eax
		a.Raw(0xC7, 0x80); Imm(a, ResyncPendingVa - B); Imm(a, 1);   // mov dword [eax+pending],1
		a.Mark("skip");
		a.Jmp(DesyncKickSkip);
		a.Resolve();
		return at;
	}

	// once per frame: a desync was noticed but no resync could start then -> start it now if possible, and make
	// every PC join (0xAC "force"). Runs inside the frame hook's pushad block ([ebp+58h] = session).
	static void EmitPendingResync(Asm a) {
		a.Raw(0x60);                                      // pushad
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint B = a.Here;
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0x83, 0xBB); Imm(a, ResyncPendingVa - B); a.Raw(0x00);   // cmp dword [pending],0
		a.J8(0x74, "done");
		a.Raw(0x8B, 0x4D, 0x58, 0x85, 0xC9); a.J8(0x74, "done");       // mov ecx,[ebp+58h] / test ecx,ecx (session)
		a.Raw(0x8B, 0xB1, 0x78, 0xBE, 0x02, 0x00);        // mov esi,[ecx+2BE78h]    (resync state)
		a.Raw(0x85, 0xF6); a.J8(0x74, "done");
		a.Raw(0x83, 0x7E, 0x30, 0x00); a.J8(0x75, "clear");             // a resync is already running
		a.Raw(0x51, 0x8B, 0xCE); a.Call(StartResync);     // push session / ecx = resync
		a.Raw(0x83, 0x7E, 0x30, 0x00); a.J8(0x74, "done");             // not on the campaign map yet: retry next frame
		a.Raw(0x8B, 0x4D, 0x58, 0x6A, 0x02); a.Call(SendResyncAnswer); // everyone joins
		a.Mark("clear");
		a.Raw(0xC7, 0x83); Imm(a, ResyncPendingVa - B); Imm(a, 0);     // mov dword [pending],0
		a.Mark("done");
		a.Raw(0x61);                                      // popad
		a.Resolve();
	}

	static uint EmitReportNoKick(Asm a) {
		uint at = a.Here;
		a.Raw(0x50, 0x33, 0xC0, 0x83, 0xBB, 0xD0, 0x96, 0x05, 0x00, 0x00, 0x0F, 0x95, 0xC0);   // push eax / eax = battle running
		EmitLog(a, "DESYNC reported by another player (1 = during a battle, 0 = on the campaign map)", 1);
		a.Raw(0x58);                                      // pop eax
		a.Raw(0x83, 0xBB, 0xD8, 0x96, 0x05, 0x00, 0x00);  // cmp dword ptr [ebx+596D8h],0   (campaign)
		a.J32("skip", 0x0F, 0x85);                        // jne -> no kick
		a.Raw(0x83, 0xBB, 0xD0, 0x96, 0x05, 0x00, 0x00);  // cmp dword ptr [ebx+596D0h],0
		a.J32("skip", 0x0F, 0x85);                        // jne -> no kick
		a.Raw(0x55, 0x57);                                // push ebp / push edi            (replaced instructions)
		a.Raw(0x8D, 0x44, 0x24, 0x10);                    // lea eax,[esp+10h]
		a.Jmp(ReportKickResume);
		a.Mark("skip");
		a.Jmp(ReportKickSkip);
		a.Resolve();
		return at;
	}

	// ---- save/load screens leak the player's Windows locale into the simulation --------------------------
	// FUN_110b83f0 (run when the save list, load list or save dialog opens, to format save dates) calls
	// _wsetlocale(LC_ALL, "<language>_<country>.<codepage>"). Only some of those screens call FUN_110b55d0 to go
	// back to "C", and only if they still own the token; so after a player opens Save or Load, the C runtime on
	// that PC stays in the Windows locale for the rest of the session: number parsing/printing (decimal comma
	// on German, Czech, French, Russian... Windows), character case rules (Turkish I) and so on now differ from
	// the other players' PCs. This fits the long-standing "only the host should save" advice and CA's own
	// "German vs Czech desync" patch.
	// Fix: switch only LC_TIME there (all those screens need is the dates); everything else stays "C".
	const uint SaveScreenLocaleCategory = 0x110B84E1;   // push 0 (LC_ALL) -> push 5 (LC_TIME), before call _wsetlocale

	// ---- invite buttons on slots 3..8 -------------------------------------------------------------------
	// The lobby wires only the FIRST "button_invite" under its root (10FD5B36: FindChild + AddListener), the one
	// in the frame behind panel 2. The copies behind panels 3..8 showed tooltips but did nothing when clicked,
	// so once player 2 had joined there was no working invite button. The layout names those frames
	// invite_frame3..8; this wires each frame's button to the same listener and shows it to the host only
	// (as 10FD5EB4 does for the first one).
	const uint InviteWireAt = 0x10FD5B70;      // mov esi,[ebx+4] / lea ecx,[esp+20h]  (ebx = lobby)
	const uint InviteWireResume = 0x10FD5B77;
	const uint SessionIsHost = 0x10F00940;     // thiscall(session) -> al
	const uint SetVisible = 0x10F9C360;        // thiscall(component; bool) ret 4

	static uint EmitInviteWiring(Asm a) {
		uint at = a.Here;
		a.Raw(0x60);                                      // pushad
		a.Raw(0x83, 0xEC, 0x14);                          // sub esp,14h             (temp string + [esp+10h] = strings)
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint popAt = a.Here;
		a.Raw(0x58);                                      // pop eax
		int stringsIdx = a.Bytes.Count + 1;
		a.Raw(0x05, 0, 0, 0, 0);                          // add eax, strings - popAt
		a.Raw(0x89, 0x44, 0x24, 0x10);                    // mov [esp+10h],eax
		a.Raw(0x33, 0xED);                                // xor ebp,ebp             (frame index 0..5)
		a.Mark("loop");
		a.Raw(0x6B, 0xC5, 0x0E);                          // imul eax,ebp,14
		a.Raw(0x03, 0x44, 0x24, 0x10);                    // add eax,[esp+10h]       (-> "invite_frameN")
		a.Raw(0x50);                                      // push eax
		a.Raw(0x8D, 0x4C, 0x24, 0x04);                    // lea ecx,[esp+4]
		a.Call(StrCtor);
		a.Raw(0x8B, 0x4B, 0x04);                          // mov ecx,[ebx+4]         (root)
		a.Raw(0x54);                                      // push esp                (&temp)
		a.Call(FindChild);
		a.Raw(0x8B, 0xF0);                                // mov esi,eax
		a.Raw(0x8B, 0xCC);                                // mov ecx,esp
		a.Call(StrDtor);
		a.Raw(0x85, 0xF6);                                // test esi,esi
		a.J32("next", 0x0F, 0x84);                        // jz next
		a.Raw(0x8B, 0x44, 0x24, 0x10);                    // mov eax,[esp+10h]
		a.Raw(0x83, 0xC0, 6 * 14);                        // add eax,84              (-> "button_invite")
		a.Raw(0x50);                                      // push eax
		a.Raw(0x8D, 0x4C, 0x24, 0x04);                    // lea ecx,[esp+4]
		a.Call(StrCtor);
		a.Raw(0x8B, 0xCE);                                // mov ecx,esi             (search inside this frame)
		a.Raw(0x54);                                      // push esp
		a.Call(FindChild);
		a.Raw(0x8B, 0xF0);                                // mov esi,eax             (its invite button)
		a.Raw(0x8B, 0xCC);                                // mov ecx,esp
		a.Call(StrDtor);
		a.Raw(0x85, 0xF6);                                // test esi,esi
		a.J8(0x74, "next");                               // jz next
		a.Raw(0x8B, 0x8E, 0x18, 0x03, 0x00, 0x00);        // mov ecx,[esi+318h]      (button handler object)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "visible");                            // jz visible
		a.Raw(0x83, 0xC1, 0x14);                          // add ecx,14h
		a.Raw(0x8D, 0x43, 0x2C);                          // lea eax,[ebx+2Ch]       (lobby listener, as 10FD5B13)
		a.Raw(0x50);                                      // push eax
		a.Call(AddListener);
		a.Mark("visible");
		a.Raw(0x33, 0xC0);                                // xor eax,eax
		a.Raw(0x8B, 0x8B, 0x88, 0x00, 0x00, 0x00);        // mov ecx,[ebx+88h]       (session)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "setvis");                             // jz setvis
		a.Call(SessionIsHost);
		a.Raw(0x0F, 0xB6, 0xC0);                          // movzx eax,al
		a.Mark("setvis");
		a.Raw(0x50);                                      // push eax
		a.Raw(0x8B, 0xCE);                                // mov ecx,esi
		a.Call(SetVisible);
		a.Mark("next");
		a.Raw(0x45);                                      // inc ebp
		a.Raw(0x83, 0xFD, 0x06);                          // cmp ebp,6
		a.J32("loop", 0x0F, 0x82);                        // jb loop
		a.Raw(0x83, 0xC4, 0x14);                          // add esp,14h
		a.Raw(0x61);                                      // popad
		a.Raw(0x8B, 0x73, 0x04);                          // mov esi,[ebx+4]         (replaced instructions)
		a.Raw(0x8D, 0x4C, 0x24, 0x20);                    // lea ecx,[esp+20h]
		a.Jmp(InviteWireResume);
		uint strings = a.Here;
		for (int n = 3; n <= 8; n++) { a.Bytes.AddRange(Encoding.ASCII.GetBytes("invite_frame" + n)); a.Raw(0x00); }
		a.Bytes.AddRange(Encoding.ASCII.GetBytes("button_invite")); a.Raw(0x00);
		FixImm32(a, stringsIdx, strings - popAt);
		a.Resolve();
		return at;
	}

	// ---- automatic campaign resync ---------------------------------------------------------------------
	// On a campaign desync FUN_10f058d0 marks a resync as pending ([resync+30h] = 1) and tells its listeners,
	// which open the "mp_campaign_desync" popup. Nothing happens until someone presses OK there
	// (FUN_11103d10 -> FUN_10f2d4b0(session, 1), which sends message 0xAC). Until then the game sits out
	// of sync, and pressing Cancel or leaving it drops the player.
	// Fix: accept straight away, exactly as the OK button does. The popup still shows the resync progress.
	const uint ResyncNotifyCall = 0x10F05914;  // call FUN_102fc670 (notify listeners); esi = resync, edi = session
	const uint NotifyListeners = 0x102FC670;   // thiscall(list; arg) ret 4
	const uint AnswerResync = 0x10F2D4B0;      // thiscall(resync; session, bool accept) ret 8

	static uint EmitAutoResync(Asm a) {
		uint at = a.Here;
		a.Raw(0xFF, 0x74, 0x24, 0x04);                    // push dword ptr [esp+4]  (the listeners' argument)
		a.Call(NotifyListeners);                          // ecx = esi on entry, as the original call
		a.Raw(0x6A, 0x01);                                // push 1                  (accept)
		a.Raw(0x57);                                      // push edi                (session)
		a.Raw(0x8B, 0xCE);                                // mov ecx,esi             (resync state)
		a.Call(AnswerResync);
		a.Raw(0xC2, 0x04, 0x00);                          // ret 4
		return at;
	}

	// ---- host resync key (Ctrl+Shift+F9) --------------------------------------------------------------
	// A resync only starts on a PC that noticed a checksum mismatch itself. Desyncs that hang the game instead
	// (one PC stuck on an AI turn while another thinks it is a player's turn) never start one, and players who
	// didn't notice ignore the others' 0xAC "accept" messages (FUN_10efa140 needs state 1..3).
	// Resync itself: once every PC has accepted, the host saves the campaign, sends the file and everyone loads it.
	// Fix:
	//   - host presses Ctrl+Shift+F9 (campaign, no resync running): it starts its own resync and broadcasts 0xAC
	//     with value 2 ("force").
	//   - every PC, the host included, that receives a "force" while idle starts a resync exactly as on a detected
	//     desync (FUN_10f058d0: popup + the auto-accept above), then the message counts as a normal accept.
	// FUN_10f058d0 still requires the campaign map, so the key does nothing in battles or the front end.
	const uint ResyncAnswerCall = 0x10F10E2D;  // message 0xAC: call FUN_10efa140(resync; session, is_local, accept)
	const uint ResyncAnswer = 0x10EFA140;
	const uint StartResync = 0x10F058D0;       // thiscall(resync; session) ret 4
	const uint SendResyncAnswer = 0x10F27820;  // thiscall(session; value) ret 4: broadcast message 0xAC
	const uint GetAsyncKeyStateIat = 0x115A1558;
	const uint ResyncKeyPrevVa = 0x11CB1534;   // key held last frame (second reserved .data dword)

	// v1.12: the engine's resync is built for 2 players. FUN_10efa140 goes 1 -> 2 (I accepted) / 3 (the other
	// accepted) -> 4 (both), and in state 4 the host saves and sends the campaign at once. With 3-8 players the
	// host sent after the FIRST other player accepted; anyone not ready yet (still loading, or unable to start a
	// resync) missed it and stayed on the old campaign: turns stuck for good. A half-finished resync anywhere
	// (state != 0) also blocked every later one, the host's key included.
	//   - Host: remote accepts (value 1) are counted ([accepts]); state 4 is held back as 2 until every other
	//     connected player (FUN_10f0eb00 - 1) has accepted. After ResyncWaitFrames the frame hook sends anyway.
	//   - Value 3 = host reset (the key): a PC whose resync is past the accept stage (4+) aborts it (FUN_10f25890,
	//     the engine's own reset) and starts again; one in state 2 sends its accept again so the new count has it.
	//   - Value 2 (a PC that started a pending resync) only starts idle PCs, as before; it never aborts anything.
	//   - A force that arrives while a PC can't start a resync yet sets "pending": the frame hook starts it later.
	const uint ResyncAbort = 0x10F25890;       // fastcall(resync): state 0, free the buffer
	const uint ResyncAcceptsVa = 0x11CB1808;   // host: other players' accepts in this resync
	const uint ResyncWaitVa = 0x11CB180C;      // host: frames spent waiting for the rest
	const int ResyncWaitFrames = 2400;

	static uint EmitForcedResyncAnswer(Asm a) {
		// thiscall(resync; session, is_local, value) ret 0Ch
		uint at = a.Here;
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint B = a.Here;
		a.Raw(0x58);                                      // pop eax               (base)
		a.Raw(0x80, 0x7C, 0x24, 0x0C, 0x01);              // cmp byte ptr [esp+0Ch],1   (a plain accept)
		a.J8(0x75, "fa_notacc");
		a.Raw(0x80, 0x7C, 0x24, 0x08, 0x00);              // cmp byte ptr [esp+8],0     (from another PC)
		a.J8(0x75, "fa_answer");
		a.Raw(0xFF, 0x80); Imm(a, ResyncAcceptsVa - B);   // inc dword [eax+accepts]
		a.J8(0xEB, "fa_answer");
		a.Mark("fa_notacc");
		a.Raw(0x8A, 0x54, 0x24, 0x0C);                    // mov dl,[esp+0Ch]
		a.Raw(0x80, 0xFA, 0x02); a.J8(0x72, "fa_answer"); // 0 = decline: the original handles it
		a.Raw(0x80, 0xFA, 0x03); a.J8(0x77, "fa_answer");
		a.Raw(0xC6, 0x44, 0x24, 0x0C, 0x01);              // mov byte ptr [esp+0Ch],1   (counts as an accept)
		a.Raw(0x83, 0x79, 0x30, 0x00); a.J8(0x74, "fa_start");   // idle: start one
		a.Raw(0x80, 0xFA, 0x03); a.J8(0x75, "fa_answer"); // only the host's reset goes further
		a.Raw(0x80, 0x7C, 0x24, 0x08, 0x00); a.J8(0x75, "fa_answer");   // our own reset (host aborted already)
		a.Raw(0x83, 0x79, 0x30, 0x03); a.J8(0x77, "fa_abort");   // past the accept stage: start over
		a.Raw(0x83, 0x79, 0x30, 0x02); a.J8(0x75, "fa_answer");
		a.Raw(0x51);                                      // state 2: accept again for the host's new count
		a.Raw(0x6A, 0x01, 0x8B, 0x4C, 0x24, 0x0C);        // push 1 / mov ecx,[esp+0Ch]   (session)
		a.Call(SendResyncAnswer);
		a.Raw(0x59); a.J8(0xEB, "fa_answer");
		a.Mark("fa_abort");
		a.Raw(0x51); a.Call(ResyncAbort); a.Raw(0x59);    // push ecx / call / pop ecx
		a.Mark("fa_start");
		a.Raw(0x51, 0xFF, 0x74, 0x24, 0x08);              // push ecx / push [esp+8]  (session)
		a.Call(StartResync);
		a.Raw(0x59);
		a.Raw(0x83, 0x79, 0x30, 0x00); a.J8(0x75, "fa_answer");   // started
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // can't start yet: the frame hook retries
		uint B2 = a.Here;
		a.Raw(0x58, 0xC7, 0x80); Imm(a, ResyncPendingVa - B2); Imm(a, 1);
		a.Mark("fa_answer");
		a.Raw(0x51);                                      // push ecx                 (resync)
		a.Raw(0xFF, 0x74, 0x24, 0x10, 0xFF, 0x74, 0x24, 0x10, 0xFF, 0x74, 0x24, 0x10);   // same 3 arguments
		a.Call(ResyncAnswer);
		a.Raw(0x59);                                      // pop ecx
		// host: don't send the campaign until every other player has accepted
		a.Raw(0x83, 0x79, 0x30, 0x04); a.J8(0x75, "fa_ret");
		a.Raw(0x51, 0x8B, 0x4C, 0x24, 0x08);              // push ecx / mov ecx,[esp+8]  (session)
		a.Call(SessionIsHost);
		a.Raw(0x84, 0xC0); a.J8(0x74, "fa_pop");
		a.Raw(0x8B, 0x4C, 0x24, 0x08, 0x81, 0xC1, 0xF4, 0x8E, 0x05, 0x00);   // lea: ecx = session+58EF4h
		a.Call(CountPlayers);
		a.Raw(0x48);                                      // dec eax                  (other players)
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);
		uint B3 = a.Here;
		a.Raw(0x5A);                                      // pop edx
		a.Raw(0x3B, 0x82); Imm(a, ResyncAcceptsVa - B3);  // cmp eax,[edx+accepts]
		a.J8(0x76, "fa_pop");                             // jbe: everyone accepted
		a.Raw(0x59, 0xC7, 0x41, 0x30, 0x02, 0x00, 0x00, 0x00);   // pop ecx / state 2: wait for the rest
		a.J8(0xEB, "fa_ret");
		a.Mark("fa_pop");
		a.Raw(0x59);
		a.Mark("fa_ret");
		a.Raw(0xC2, 0x0C, 0x00);                          // ret 0Ch
		a.Resolve();
		return at;
	}

	// every frame (frame hook pushad block, [ebp+58h] = session): host bookkeeping for the accept count
	static void EmitResyncWait(Asm a) {
		a.Raw(0x60);                                      // pushad
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);
		uint B = a.Here;
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0x8B, 0x4D, 0x58, 0x85, 0xC9); a.J8(0x74, "rw_done");   // session
		a.Raw(0x8B, 0xB1, 0x78, 0xBE, 0x02, 0x00, 0x85, 0xF6); a.J8(0x74, "rw_done");   // esi = resync
		a.Raw(0x8B, 0x46, 0x30);                          // mov eax,[esi+30h]
		a.Raw(0x85, 0xC0); a.J8(0x75, "rw_active");
		a.Raw(0x89, 0x83); Imm(a, ResyncAcceptsVa - B);   // idle: reset the count and the wait
		a.Raw(0x89, 0x83); Imm(a, ResyncWaitVa - B);
		a.J8(0xEB, "rw_done");
		a.Mark("rw_active");
		a.Raw(0x83, 0xF8, 0x02); a.J8(0x75, "rw_reset_wait");   // waiting for the others (host in state 2)
		a.Raw(0x83, 0xBB); Imm(a, ResyncAcceptsVa - B); a.Raw(0x00);
		a.J8(0x74, "rw_reset_wait");                      // nobody accepted yet: not this case
		a.Raw(0xFF, 0x83); Imm(a, ResyncWaitVa - B);      // inc dword [wait]
		a.Raw(0x81, 0xBB); Imm(a, ResyncWaitVa - B); Imm(a, (uint)ResyncWaitFrames);
		a.J8(0x72, "rw_done");
		a.Call(SessionIsHost);                            // waited long enough: the host sends anyway (ecx = session)
		a.Raw(0x84, 0xC0); a.J8(0x74, "rw_reset_wait");
		a.Raw(0xC7, 0x46, 0x30, 0x04, 0x00, 0x00, 0x00);  // state 4
		a.Mark("rw_reset_wait");
		a.Raw(0xC7, 0x83); Imm(a, ResyncWaitVa - B); Imm(a, 0);
		a.Mark("rw_done");
		a.Raw(0x61);
		a.Resolve();
	}

	// polled once per frame from the frame hook (ebp = owner, [ebp+58h] = session); keeps every register
	// v1.13: every press is answered: a beep (OK sound when the resync starts, error sound otherwise) and a line in
	// eightplayer_log.txt saying what happened. Testers pressed it in the lobby and on loading screens, where it
	// can't work, and couldn't tell whether anything had happened.
	static void EmitResyncKeyPoll(Asm a) {
		a.Raw(0x60);                                      // pushad
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint popAt = a.Here;
		a.Raw(0x5E);                                      // pop esi
		a.Raw(0x8B, 0x9E); a.Bytes.AddRange(BitConverter.GetBytes(GetAsyncKeyStateIat - popAt));   // mov ebx,[esi+iat]
		a.Raw(0x8D, 0xBE); a.Bytes.AddRange(BitConverter.GetBytes(ResyncKeyPrevVa - popAt));       // lea edi,[esi+prev]
		foreach (byte vk in new byte[] { 0x78, 0x11, 0x10 }) {   // F9, Ctrl, Shift
			a.Raw(0x6A, vk);                              // push vk
			a.Raw(0xFF, 0xD3);                            // call ebx                (GetAsyncKeyState)
			a.Raw(0xF6, 0xC4, 0x80);                      // test ah,80h             (held)
			a.J32("up", 0x0F, 0x84);                      // jz up
		}
		a.Raw(0x83, 0x3F, 0x00);                          // cmp dword ptr [edi],0   (only on the press itself)
		a.J32("done", 0x0F, 0x85);                        // jne done
		a.Raw(0xC7, 0x07, 0x01, 0x00, 0x00, 0x00);        // mov dword ptr [edi],1
		a.Raw(0x8B, 0x4D, 0x58);                          // mov ecx,[ebp+58h]       (session)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J32("k_nosession", 0x0F, 0x84);
		a.Call(SessionIsHost);
		a.Raw(0x84, 0xC0);                                // test al,al
		a.J32("k_nothost", 0x0F, 0x84);
		a.Raw(0x8B, 0x4D, 0x58);                          // mov ecx,[ebp+58h]
		a.Raw(0x8B, 0x81, 0x78, 0xBE, 0x02, 0x00);        // mov eax,[ecx+2BE78h]    (resync state)
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J32("k_cant", 0x0F, 0x84);
		a.Raw(0x8B, 0xF0);                                // mov esi,eax
		a.Raw(0x83, 0x7E, 0x30, 0x00);                    // cmp dword ptr [esi+30h],0
		a.J8(0x74, "fresh");                              // a resync is stuck or running: cancel it first
		a.Raw(0x8B, 0x46, 0x30);                          // mov eax,[esi+30h]
		EmitLog(a, "Ctrl+Shift+F9: cancelling the resync that was still running, state", 1);
		a.Raw(0x8B, 0xCE); a.Call(ResyncAbort);           // mov ecx,esi / FUN_10f25890
		a.Mark("fresh");
		a.Raw(0xC7, 0x87); Imm(a, ResyncAcceptsVa - ResyncKeyPrevVa); Imm(a, 0);   // new count of accepts
		a.Raw(0x8B, 0x4D, 0x58, 0x51);                    // mov ecx,[ebp+58h] / push ecx  (session)
		a.Raw(0x8B, 0xCE);                                // mov ecx,esi
		a.Call(StartResync);                              // the host's own resync (popup + auto-accept)
		a.Raw(0x83, 0x7E, 0x30, 0x00);                    // cmp dword ptr [esi+30h],0
		a.J32("k_cant", 0x0F, 0x84);                      // not on the campaign map
		a.Raw(0x8B, 0x4D, 0x58);                          // mov ecx,[ebp+58h]
		a.Raw(0x6A, 0x03);                                // push 3                  (host reset: everyone starts over and joins)
		a.Call(SendResyncAnswer);                         // ecx = session
		a.Raw(0x8B, 0x4D, 0x58, 0x81, 0xC1, 0xF4, 0x8E, 0x05, 0x00);   // ecx = session+58EF4h (members)
		a.Call(CountPlayers);
		EmitLog(a, "Ctrl+Shift+F9: resync started; players in the game", 1);
		EmitBeep(a, 0x40);                                // MB_ICONINFORMATION
		a.J32("done", 0xE9);
		a.Mark("k_nosession");
		EmitLog(a, "Ctrl+Shift+F9 ignored: not in a multiplayer game", 0);
		a.J32("k_err", 0xE9);
		a.Mark("k_nothost");
		EmitLog(a, "Ctrl+Shift+F9 ignored: only the host can start a resync", 0);
		a.J32("k_err", 0xE9);
		a.Mark("k_cant");
		EmitLog(a, "Ctrl+Shift+F9: no resync possible here; it only works on the campaign map (not in the lobby, a battle or a loading screen)", 0);
		a.Mark("k_err");
		EmitBeep(a, 0x10);                                // MB_ICONHAND
		a.J32("done", 0xE9);
		a.Mark("up");
		a.Raw(0xC7, 0x07, 0x00, 0x00, 0x00, 0x00);        // mov dword ptr [edi],0
		a.Mark("done");
		a.Raw(0x61);                                      // popad
		a.Resolve();
	}

	// ---- event log (v1.13) -----------------------------------------------------------------------------
	// eightplayer_log.txt in the game folder: one timestamped line whenever the multiplayer state changes (session,
	// players, lobby state, campaign / battle environment, resync state, the "waiting for players" screen), every
	// detected desync and every Ctrl+Shift+F9. Plain text, appended across game launches (cut back when over
	// 256 KB at a launch), so a report after a hang or a restart still shows what led up to it.
	// ModLog: esi = ASCII text, edx = value, ecx = 0 (no value) / 1 (decimal) / 2 (hex); keeps every register.
	const uint GetLocalTimeIat = 0x115A1264;
	const uint SetFilePointerIat = 0x115A12E0;
	const uint LogStartedVa = 0x11CB1920;        // a line was written in this run
	const uint WatchVa = 0x11CB1924;             // 16 watched values (+1; 0 = not logged yet)
	const int WatchSlots = 16;
	const uint BattleUiVa = 0x11BC8180;          // battle UI root: [+110h] = battle setup
	public static uint modLogAt;

	static void EmitModLog(Asm a) {
		// decw: eax -> decimal at [edi] with at least ecx digits (zero padded); keeps ebx/edx/esi
		uint decw = a.Here;
		a.Raw(0x53, 0x52, 0x56, 0x8B, 0xF1, 0x33, 0xC9, 0xBB, 0x0A, 0x00, 0x00, 0x00);   // push ebx/edx/esi / esi = width / ecx = 0 / ebx = 10
		a.Mark("ml_d");
		a.Raw(0x33, 0xD2, 0xF7, 0xF3, 0x52, 0x41, 0x85, 0xC0); a.J8(0x75, "ml_d");   // xor edx,edx / div ebx / push edx / inc ecx / test eax,eax
		a.Mark("ml_p");
		a.Raw(0x3B, 0xCE); a.J8(0x73, "ml_w");            // cmp ecx,esi / jae
		a.Raw(0x6A, 0x00, 0x41); a.J8(0xEB, "ml_p");      // push 0 / inc ecx
		a.Mark("ml_w");
		a.Raw(0x58, 0x04, 0x30, 0x88, 0x07, 0x47, 0x49); a.J8(0x75, "ml_w");   // pop eax / add al,'0' / stosb / dec ecx
		a.Raw(0x5E, 0x5A, 0x5B, 0xC3);
		// hex8: eax -> 8 hex digits at [edi]; keeps ecx/edx
		uint hex8 = a.Here;
		a.Raw(0x51, 0x52, 0xB9, 0x08, 0x00, 0x00, 0x00);
		a.Mark("ml_hx");
		a.Raw(0xC1, 0xC0, 0x04, 0x8B, 0xD0, 0x83, 0xE2, 0x0F, 0x80, 0xFA, 0x0A); a.J8(0x72, "ml_hd");
		a.Raw(0x80, 0xC2, 0x07);
		a.Mark("ml_hd");
		a.Raw(0x80, 0xC2, 0x30, 0x88, 0x17, 0x47, 0x49); a.J8(0x75, "ml_hx");
		a.Raw(0x5A, 0x59, 0xC3);
		a.Resolve();

		modLogAt = a.Here;
		a.Raw(0x60);                                      // pushad
		a.Raw(0x81, 0xEC, 0x40, 0x01, 0x00, 0x00);        // sub esp,140h: [0] SYSTEMTIME, [10h] written, [14h] file, [18h] format, [1Ch] value, [20h] text
		a.Raw(0x89, 0x4C, 0x24, 0x18, 0x89, 0x54, 0x24, 0x1C);
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);
		uint B = a.Here;
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0x54, 0xFF, 0x93); Imm(a, GetLocalTimeIat - B);   // push esp / call [GetLocalTime]
		a.Raw(0x8D, 0x7C, 0x24, 0x20);                    // lea edi,[esp+20h]
		foreach (var f in new[] { new { Off = (byte)0x08, Digits = (byte)2, Sep = (byte)':' }, new { Off = (byte)0x0A, Digits = (byte)2, Sep = (byte)':' },
				new { Off = (byte)0x0C, Digits = (byte)2, Sep = (byte)'.' }, new { Off = (byte)0x0E, Digits = (byte)3, Sep = (byte)' ' } }) {
			a.Raw(0x0F, 0xB7, 0x44, 0x24, f.Off, 0xB9, f.Digits, 0x00, 0x00, 0x00); a.Call(decw);   // movzx eax,word [esp+off] / mov ecx,digits
			a.Raw(0xC6, 0x07, f.Sep, 0x47);               // mov byte [edi],sep / inc edi
		}
		a.Raw(0xC6, 0x07, 0x20, 0x47, 0x33, 0xC9);        // ' ' / xor ecx,ecx
		a.Mark("ml_c");
		a.Raw(0x8A, 0x06, 0x46, 0x84, 0xC0); a.J8(0x74, "ml_t");
		a.Raw(0x88, 0x07, 0x47, 0x41, 0x81, 0xF9, 0xC8, 0x00, 0x00, 0x00); a.J8(0x72, "ml_c");   // at most 200 characters
		a.Mark("ml_t");
		a.Raw(0x83, 0x7C, 0x24, 0x18, 0x00); a.J8(0x74, "ml_eol");
		a.Raw(0xC6, 0x07, 0x20, 0x47, 0x8B, 0x44, 0x24, 0x1C);   // ' ' / mov eax,[esp+1Ch]
		a.Raw(0x83, 0x7C, 0x24, 0x18, 0x01); a.J8(0x75, "ml_hex");
		a.Raw(0x33, 0xC9); a.Call(decw); a.J8(0xEB, "ml_eol");
		a.Mark("ml_hex");
		a.Call(hex8);
		a.Mark("ml_eol");
		a.Raw(0x66, 0xC7, 0x07, 0x0D, 0x0A, 0x83, 0xC7, 0x02);   // CR LF
		Action<byte> open = disposition => {
			a.Raw(0x6A, 0x00, 0x68, 0x80, 0x00, 0x00, 0x00, 0x6A, disposition, 0x6A, 0x00, 0x6A, 0x03, 0x68, 0x00, 0x00, 0x00, 0x40);
			int fileIdx = a.Bytes.Count + 2;
			a.Raw(0x8D, 0x83, 0, 0, 0, 0, 0x50, 0xFF, 0x93); Imm(a, CreateFileAIat - B);
			fixFile.Add(fileIdx);
			a.Raw(0x83, 0xF8, 0xFF); a.J32("ml_done", 0x0F, 0x84);
			a.Raw(0x89, 0x44, 0x24, 0x14);                // mov [esp+14h],eax
		};
		fixFile.Clear();
		open(4);                                          // OPEN_ALWAYS, GENERIC_WRITE
		a.Raw(0x6A, 0x02, 0x6A, 0x00, 0x6A, 0x00, 0x50, 0xFF, 0x93); Imm(a, SetFilePointerIat - B);   // to the end: eax = size
		a.Raw(0x83, 0xBB); Imm(a, LogStartedVa - B); a.Raw(0x00); a.J32("ml_line", 0x0F, 0x85);
		a.Raw(0xC7, 0x83); Imm(a, LogStartedVa - B); Imm(a, 1);
		a.Raw(0x3D, 0x00, 0x00, 0x04, 0x00); a.J8(0x72, "ml_head");   // over 256 KB: start the file again
		a.Raw(0xFF, 0x74, 0x24, 0x14, 0xFF, 0x93); Imm(a, CloseHandleIat - B);
		open(2);                                          // CREATE_ALWAYS
		a.Mark("ml_head");
		string head = "\r\n=== Shogun 2 8-player co-op mod v" + Version + ": game started ===\r\n";
		a.Raw(0x6A, 0x00, 0x8D, 0x44, 0x24, 0x14, 0x50, 0x68); Imm(a, (uint)head.Length);   // push 0 / &written / length
		int headLea = a.Bytes.Count + 2;
		a.Raw(0x8D, 0x83, 0, 0, 0, 0, 0x50, 0xFF, 0x74, 0x24, 0x24, 0xFF, 0x93); Imm(a, WriteFileIat - B);
		a.Mark("ml_line");
		a.Raw(0x8D, 0x44, 0x24, 0x20, 0x8B, 0xCF, 0x2B, 0xC8);   // lea eax,[esp+20h] / mov ecx,edi / sub ecx,eax (length)
		a.Raw(0x6A, 0x00, 0x8D, 0x54, 0x24, 0x14, 0x52, 0x51);   // push 0 / &written / length
		a.Raw(0x8D, 0x44, 0x24, 0x2C, 0x50, 0xFF, 0x74, 0x24, 0x24, 0xFF, 0x93); Imm(a, WriteFileIat - B);
		a.Raw(0xFF, 0x74, 0x24, 0x14, 0xFF, 0x93); Imm(a, CloseHandleIat - B);
		a.Mark("ml_done");
		a.Raw(0x81, 0xC4, 0x40, 0x01, 0x00, 0x00, 0x61, 0xC3);   // add esp,140h / popad / ret
		uint file = a.Here;
		a.Bytes.AddRange(Encoding.ASCII.GetBytes("eightplayer_log.txt")); a.Raw(0);
		uint headAt = a.Here;
		a.Bytes.AddRange(Encoding.ASCII.GetBytes(head)); a.Raw(0);
		foreach (int i in fixFile) FixImm32(a, i, file - B);
		FixImm32(a, headLea, headAt - B);
		a.Resolve();
	}
	static readonly List<int> fixFile = new List<int>();

	// one log line; the value to print is in eax (format 0 = none, 1 = decimal, 2 = hex); keeps every register
	static void EmitLog(Asm a, string text, int format) {
		a.Raw(0x60, 0x8B, 0xD0);                          // pushad / mov edx,eax
		a.Raw(0xE8); Imm(a, (uint)text.Length + 1);       // call over the text: pushes its address
		a.Bytes.AddRange(Encoding.ASCII.GetBytes(text)); a.Raw(0);
		a.Raw(0x5E, 0xB9); Imm(a, (uint)format);          // pop esi / mov ecx,format
		a.Call(modLogAt);
		a.Raw(0x61);                                      // popad
	}

	// MessageBeep(type) through user32 (already loaded by the game); keeps every register
	static void EmitBeep(Asm a, byte type) {
		a.Raw(0x60, 0xE8, 0x00, 0x00, 0x00, 0x00);
		uint B = a.Here;
		a.Raw(0x5B);                                      // pop ebx
		a.Raw(0xE8); Imm(a, 11); a.Bytes.AddRange(Encoding.ASCII.GetBytes("user32.dll")); a.Raw(0);   // push "user32.dll"
		a.Raw(0xFF, 0x93); Imm(a, LoadLibraryAIat - B);
		a.Raw(0x85, 0xC0, 0x74, 0x20);                    // test eax,eax / jz end (17 + 7 + 4 + 4 bytes on)
		a.Raw(0xE8); Imm(a, 12); a.Bytes.AddRange(Encoding.ASCII.GetBytes("MessageBeep")); a.Raw(0);   // push "MessageBeep"
		a.Raw(0x50, 0xFF, 0x93); Imm(a, GetProcAddressIat - B);
		a.Raw(0x85, 0xC0, 0x74, 0x04, 0x6A, type, 0xFF, 0xD0);   // test / jz end / push type / call eax
		a.Raw(0x61);                                      // end: popad
	}

	// every frame (frame hook, [ebp+58h] = session): log each watched value when it changes
	static void EmitStateWatch(Asm a) {
		a.Raw(0x60, 0xE8, 0x00, 0x00, 0x00, 0x00);
		uint B = a.Here;
		a.Raw(0x5B);                                      // pop ebx
		int slot = 0;
		Action<string> watch = text => {                  // eax = current value
			string next = "sw_" + slot;
			a.Raw(0x8D, 0x48, 0x01, 0x3B, 0x8B); Imm(a, WatchVa + (uint)slot * 4 - B);   // lea ecx,[eax+1] / cmp ecx,[slot]
			a.J32(next, 0x0F, 0x84);
			a.Raw(0x89, 0x8B); Imm(a, WatchVa + (uint)slot * 4 - B);
			EmitLog(a, text, 1);
			a.Mark(next);
			slot++;
		};
		a.Raw(0x8B, 0x7D, 0x58, 0x33, 0xC0, 0x85, 0xFF, 0x0F, 0x95, 0xC0);   // mov edi,[ebp+58h] / eax = session != 0
		watch("multiplayer session");
		a.Raw(0x85, 0xFF); a.J32("sw_end", 0x0F, 0x84);
		a.Raw(0x8B, 0xCF); a.Call(SessionIsHost); a.Raw(0x0F, 0xB6, 0xC0);
		watch("this PC is the host");
		a.Raw(0x8D, 0x8F, 0xF4, 0x8E, 0x05, 0x00); a.Call(CountPlayers);
		watch("players");
		a.Raw(0x33, 0xC0, 0x8B, 0x8F, 0xF0, 0x96, 0x05, 0x00, 0x85, 0xC9); a.J8(0x74, "sw_ls");
		a.Call(MpLobbyState);
		a.Mark("sw_ls");
		watch("game state (3 = game running)");
		a.Raw(0x33, 0xC0, 0x83, 0xBF, 0xD8, 0x96, 0x05, 0x00, 0x00, 0x0F, 0x95, 0xC0);
		watch("campaign environment");
		a.Raw(0x33, 0xC0, 0x83, 0xBF, 0xD0, 0x96, 0x05, 0x00, 0x00, 0x0F, 0x95, 0xC0);
		watch("battle environment (battle loading or running)");
		a.Raw(0x33, 0xC0, 0x8B, 0x8F, 0x78, 0xBE, 0x02, 0x00, 0x85, 0xC9); a.J8(0x74, "sw_rs");
		a.Raw(0x8B, 0x41, 0x30);
		a.Mark("sw_rs");
		watch("resync state (0 idle, 1 pending, 2/3 accepted, 4 sending, 6-11 transfer/load)");
		a.Raw(0x0F, 0xB6, 0x87, 0x85, 0x97, 0x05, 0x00);
		watch("battle over: leaving the battle in 10 s");
		a.Raw(0x33, 0xC0, 0x8B, 0x8B); Imm(a, CampaignUiVa - B);
		a.Raw(0x85, 0xC9, 0x74, 0x11, 0x8B, 0x89, 0x98, 0x0A, 0x00, 0x00, 0x85, 0xC9, 0x74, 0x07, 0x83, 0x79, 0x70, 0x00, 0x0F, 0x95, 0xC0);
		watch("campaign map shown");
		a.Raw(0x33, 0xC0, 0x8B, 0x8B); Imm(a, BattleUiVa - B);
		a.Raw(0x85, 0xC9, 0x74, 0x0A, 0x83, 0xB9, 0x10, 0x01, 0x00, 0x00, 0x00, 0x0F, 0x95, 0xC0);
		watch("battle shown");
		a.Raw(0x8B, 0x83); Imm(a, ResyncPendingVa - B);
		watch("resync waiting to start");
		a.Raw(0x8B, 0x83); Imm(a, ResyncAcceptsVa - B);
		watch("resync: players ready (host)");
		a.Mark("sw_end");
		a.Raw(0x61);
		a.Resolve();
		if (slot > WatchSlots) throw new Exception("too many watched values");
	}

	// The campaign map shows "waiting for players" (with the time left) while every PC loads a battle; it is opened
	// and closed by two listener functions (vtable slots at 1171534C / 11715358). Their slots now point at stubs
	// that log, then go on to the original function. The slots are covered by base relocations, which move the
	// stub addresses exactly as they moved the originals.
	const uint WaitingShowSlot = 0x1171534C;     // -> 11153FA0: MultiplayerSetWaitingForLoad(true)
	const uint WaitingHideSlot = 0x11715358;     // -> 11153F50: MultiplayerSetWaitingForLoad(false)

	static uint EmitWaitingLog(Asm a, uint original, string text) {
		uint at = a.Here;
		EmitLog(a, text, 0);
		a.Jmp(original);
		return at;
	}

	static void RedirectSlot(uint slot, uint expected, uint target) {
		int o = VaToOff(slot);
		if (BitConverter.ToUInt32(b, o) != expected) throw new Exception(string.Format("unexpected pointer at {0:X8}", slot));
		Array.Copy(BitConverter.GetBytes(target), 0, b, o, 4);
	}

	// ---- battle "gift units" with more than one spectator ---------------------------------------------
	// The gift button handler (FUN_110e5090) asks FUN_110de560 for the other party: it lists the session's
	// spectating players (FUN_10ef3150) and returns the FIRST one. So with several spectators only the first
	// could ever receive units, and a second spectator could not hand units back (their "source" was
	// spectator #1, and the engine only transfers units the local player holds).
	// Fix, only at the gift handler's call:
	//   - the player fighting: each press gives the selected units to the next spectator in turn
	//     (round-robin over the current spectator list; with one spectator nothing changes)
	//   - a spectator: the source is always the local player, so anyone can hand gifted units back
	const uint GiftPickCall = 0x110E50FA;    // call FUN_110de560 inside the gift handler
	const uint ListSpectators = 0x10EF3150;  // thiscall(session; vector<uint>* out) {cap, count, data}
	const uint EngineFree = 0x100F04D0;      // cdecl free
	const uint GiftCounterVa = 0x11CB1530;   // first dword after .data's VirtualSize (zeroed, writable)

	static uint EmitGiftPartnerPicker(Asm a) {
		// thiscall(ui; uint* out), ret 4 - same contract as FUN_110de560
		uint at = a.Here;
		a.Raw(0x53, 0x56, 0x57);                          // push ebx / push esi / push edi
		a.Raw(0x8B, 0x81, 0x10, 0x01, 0x00, 0x00);        // mov eax,[ecx+110h]      (battle setup)
		a.Raw(0x8B, 0x98, 0x48, 0x82, 0x02, 0x00);        // mov ebx,[eax+28248h]    (session)
		a.Raw(0x80, 0xB8, 0xE9, 0x81, 0x02, 0x00, 0x00);  // cmp byte ptr [eax+281E9h],0
		a.J32("spectator", 0x0F, 0x85);                   // jne spectator
		// --- fighting player: next spectator in turn ---
		a.Raw(0x6A, 0x00, 0x6A, 0x00, 0x6A, 0x00);        // push 0 x3               (list: cap, count, data)
		a.Raw(0x54);                                      // push esp
		a.Raw(0x8B, 0xCB);                                // mov ecx,ebx
		a.Call(ListSpectators);
		a.Raw(0x83, 0xCA, 0xFF);                          // or edx,-1               (none: -1, as the original)
		a.Raw(0x8B, 0x4C, 0x24, 0x04);                    // mov ecx,[esp+4]         (count)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "store");                              // jz store
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint popAt = a.Here;
		a.Raw(0x5F);                                      // pop edi
		a.Raw(0x81, 0xC7);                                // add edi, counter - popAt
		a.Bytes.AddRange(BitConverter.GetBytes(GiftCounterVa - popAt));
		a.Raw(0x8B, 0x07);                                // mov eax,[edi]
		a.Raw(0xFF, 0x07);                                // inc dword ptr [edi]
		a.Raw(0x33, 0xD2);                                // xor edx,edx
		a.Raw(0xF7, 0xF1);                                // div ecx                 (edx = turn % count)
		a.Raw(0x8B, 0x44, 0x24, 0x08);                    // mov eax,[esp+8]         (data)
		a.Raw(0x8B, 0x14, 0x90);                          // mov edx,[eax+edx*4]
		a.Mark("store");
		a.Raw(0x8B, 0x44, 0x24, 0x1C);                    // mov eax,[esp+1Ch]       (out: 0Ch list + 0Ch saves + ret)
		a.Raw(0x89, 0x10);                                // mov [eax],edx
		a.Raw(0x8B, 0x4C, 0x24, 0x08);                    // mov ecx,[esp+8]
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "freed");                              // jz freed
		a.Raw(0x51);                                      // push ecx
		a.Call(EngineFree);
		a.Raw(0x83, 0xC4, 0x04);                          // add esp,4
		a.Mark("freed");
		a.Raw(0x83, 0xC4, 0x0C);                          // add esp,0Ch
		a.J8(0xEB, "done");                               // jmp done
		// --- spectator: the units come from the local player ---
		a.Mark("spectator");
		a.Raw(0x8B, 0x8B, 0xF0, 0x96, 0x05, 0x00);        // mov ecx,[ebx+596F0h]    (network interface)
		a.Raw(0x83, 0xC8, 0xFF);                          // or eax,-1
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "spec_store");                         // jz spec_store
		a.Raw(0x8B, 0x01);                                // mov eax,[ecx]
		a.Raw(0xFF, 0x50, 0x10);                          // call [eax+10h]          (local player id)
		a.Mark("spec_store");
		a.Raw(0x8B, 0x54, 0x24, 0x10);                    // mov edx,[esp+10h]       (out)
		a.Raw(0x89, 0x02);                                // mov [edx],eax
		a.Mark("done");
		a.Raw(0x5F, 0x5E, 0x5B);                          // pop edi / pop esi / pop ebx
		a.Raw(0xC2, 0x04, 0x00);                          // ret 4
		a.Resolve();
		return at;
	}

	static void ReserveDataDword(uint va) {
		// grow .data's in-memory size (zero-filled, writable) so it covers va; must stay in .data's last page
		for (int i = 0; i < nSec; i++) {
			int s = secTable + i * 40;
			if (Encoding.ASCII.GetString(b, s, 5) != ".data") continue;
			uint vaStart = ImageBase + BitConverter.ToUInt32(b, s + 12);
			uint vs = BitConverter.ToUInt32(b, s + 8);
			uint nextVa = ImageBase + BitConverter.ToUInt32(b, s + 40 + 12);
			if (va < vaStart + vs || va + 4 > nextVa) throw new Exception("data slot not in .data's slack");
			if (va + 4 - vaStart > ((vs + 0xFFF) & ~0xFFFu)) throw new Exception("data slot would need another page");
			Array.Copy(BitConverter.GetBytes(va + 4 - vaStart), 0, b, s + 8, 4);
			return;
		}
		throw new Exception(".data not found");
	}

	// ---- extra code section --------------------------------------------------------------------------
	// The .text slack is used up by the lobby code, so this mod's other fixes live in a new section
	// ".s2mp" appended after the last one. Its code uses only relative calls/jumps (no relocations).
	static uint NewSectionVa() {
		uint sizeOfImage = BitConverter.ToUInt32(b, peOff + 24 + 56);
		return ImageBase + sizeOfImage;
	}

	static void AddSection(string name, List<byte> code) {
		int opt = peOff + 24;
		uint fileAlign = BitConverter.ToUInt32(b, opt + 36), sectAlign = BitConverter.ToUInt32(b, opt + 32);
		uint sizeOfImage = BitConverter.ToUInt32(b, opt + 56), sizeOfHeaders = BitConverter.ToUInt32(b, opt + 60);
		int hdr = secTable + nSec * 40;
		if (hdr + 40 > sizeOfHeaders) throw new Exception("no room for another section header");
		for (int i = 0; i < 40; i++) if (b[hdr + i] != 0) throw new Exception("section header slot not empty");
		uint rawPtr = (uint)b.Length;
		if (rawPtr % fileAlign != 0) throw new Exception("file end is not aligned");
		uint rawSize = ((uint)code.Count + fileAlign - 1) / fileAlign * fileAlign;
		var nb = new byte[b.Length + rawSize];
		Array.Copy(b, nb, b.Length);
		code.CopyTo(nb, (int)rawPtr);
		b = nb;
		Array.Copy(Encoding.ASCII.GetBytes(name.PadRight(8, '\0')), 0, b, hdr, 8);
		Array.Copy(BitConverter.GetBytes((uint)code.Count), 0, b, hdr + 8, 4);   // VirtualSize
		Array.Copy(BitConverter.GetBytes(sizeOfImage), 0, b, hdr + 12, 4);       // VirtualAddress
		Array.Copy(BitConverter.GetBytes(rawSize), 0, b, hdr + 16, 4);
		Array.Copy(BitConverter.GetBytes(rawPtr), 0, b, hdr + 20, 4);
		Array.Copy(BitConverter.GetBytes(0x60000020u), 0, b, hdr + 36, 4);       // code | execute | read
		nSec++;
		Array.Copy(BitConverter.GetBytes((ushort)nSec), 0, b, peOff + 6, 2);
		uint newImage = sizeOfImage + ((uint)code.Count + sectAlign - 1) / sectAlign * sectAlign;
		Array.Copy(BitConverter.GetBytes(newImage), 0, b, opt + 56, 4);
	}

	// ---- stage 2 (opt-in): per-panel kick buttons --------------------------------------------------
	const uint AddListener = 0x10090DD0;   // thiscall on (handlerObj ? handlerObj+0x14 : 0), arg = listener
	const uint KickHookAt = 0x10FD6350;    // click handler: first instr of "kick first non-host" (6 bytes)
	const uint KickResume = 0x10FD639A;    // click handler: common exit (string dtor + epilogue)
	const uint ShowHookAt = 0x10FD6681;    // per-member update: mov esi,[edi-50h] / lea ecx,[esp+34h] (7 bytes)
	const uint ShowResume = 0x10FD6688;    // per-member update: push "button_kick"

	static void ExpectBytes(uint va, params byte[] expected) {
		int o = VaToOff(va);
		for (int i = 0; i < expected.Length; i++)
			if (b[o + i] != expected[i]) throw new Exception(string.Format("unexpected bytes at {0:X8}", va));
	}

	// retarget an existing "call expected" at 'at' to 'target'
	static void RedirectCall(uint at, uint expected, uint target) {
		int o = VaToOff(at);
		if (b[o] != 0xE8 || (uint)(at + 5 + BitConverter.ToInt32(b, o + 1)) != expected)
			throw new Exception(string.Format("unexpected call at {0:X8}", at));
		Array.Copy(BitConverter.GetBytes((int)(target - (at + 5))), 0, b, o + 1, 4);
	}

	static void PutJmp(uint at, uint target, int patchLen) {
		int o = VaToOff(at);
		b[o] = 0xE9;
		Array.Copy(BitConverter.GetBytes((int)(target - (at + 5))), 0, b, o + 1, 4);
		for (int i = 5; i < patchLen; i++) b[o + i] = 0x90;   // nop the remainder of the replaced instructions
	}

	// "call $+5 / pop eax / add eax, imm32" -> eax = runtime address of data placed later (no relocation needed)
	static int EmitStrAddr(Asm a, out uint popAt) {
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);
		popAt = a.Here;
		a.Raw(0x58);                                       // pop eax
		int idx = a.Bytes.Count + 1;
		a.Raw(0x05, 0x00, 0x00, 0x00, 0x00);               // add eax, imm32 (filled later)
		return idx;
	}
	static void FixImm32(Asm a, int idx, uint value) {
		var v = BitConverter.GetBytes(value);
		for (int i = 0; i < 4; i++) a.Bytes[idx + i] = v[i];
	}
	static void FixRel8(Asm a, int idx, uint next, uint target) {
		int d = (int)target - (int)next;
		if (d < -128 || d > 127) throw new Exception("short jump out of range");
		a.Bytes[idx] = unchecked((byte)(sbyte)d);
	}
	static void FixRel32(Asm a, int idx, uint next, uint target) { FixImm32(a, idx, unchecked(target - next)); }

	// Stage 2 cave code, emitted right after the panel_player2 completion. Returns the entry points of the
	// click-handler and per-member-update detours.
	static void EmitStage2(Asm a, out uint handlerAt, out uint showAt) {
		var kickRefs = new List<KeyValuePair<int, uint>>();      // (imm32 index, pop address) needing &"button_kick"

		// --- binding loop for panels 3/4, also wiring each panel's own kick button ---------------------------
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint popNames = a.Here;
		a.Raw(0x5B);                                      // pop ebx
		int namesIdx = a.Bytes.Count + 2;
		a.Raw(0x81, 0xC3, 0, 0, 0, 0);                    // add ebx, imm32 (-> names)
		uint loopAt = a.Here;
		a.Raw(0x8B, 0x7E, 0x04);                          // mov edi,[esi+4]          (root)
		a.Raw(0x8D, 0x4C, 0x24, 0x14);                    // lea ecx,[esp+14h]
		a.Raw(0x53);                                      // push ebx                 (name)
		a.Call(StrCtor);
		a.Raw(0x8D, 0x44, 0x24, 0x14);                    // lea eax,[esp+14h]
		a.Raw(0x8B, 0xCF);                                // mov ecx,edi
		a.Raw(0x50);                                      // push eax
		a.Call(FindChild);
		a.Raw(0x89, 0x44, 0x24, 0x0C);                    // mov [esp+0Ch],eax        (panel)
		a.Raw(0x8D, 0x4C, 0x24, 0x14);                    // lea ecx,[esp+14h]
		a.Call(StrDtor);
		a.Raw(0x8B, 0x44, 0x24, 0x0C);                    // mov eax,[esp+0Ch]
		a.Raw(0x85, 0xC0);                                // test eax,eax
		int jzMissing = a.Bytes.Count + 2;
		a.Raw(0x0F, 0x84, 0, 0, 0, 0);                    // jz next                  (panel missing)
		uint jzMissingNext = a.Here;
		a.Raw(0x8D, 0x8E, 0x64, 0x01, 0x00, 0x00);        // lea ecx,[esi+164h]       (panel list)
		a.Raw(0x8D, 0x44, 0x24, 0x0C);                    // lea eax,[esp+0Ch]
		a.Raw(0x50);                                      // push eax
		a.Call(PushBack);
		uint pop1; int idx1 = EmitStrAddr(a, out pop1);   // eax = &"button_kick"
		kickRefs.Add(new KeyValuePair<int, uint>(idx1, pop1));
		a.Raw(0x50);                                      // push eax
		a.Raw(0x8D, 0x4C, 0x24, 0x18);                    // lea ecx,[esp+18h]        (temp string, +4 for the push)
		a.Call(StrCtor);
		a.Raw(0x8B, 0x4C, 0x24, 0x0C);                    // mov ecx,[esp+0Ch]        (search inside this panel)
		a.Raw(0x8D, 0x44, 0x24, 0x14);                    // lea eax,[esp+14h]
		a.Raw(0x50);                                      // push eax
		a.Call(FindChild);
		a.Raw(0x8B, 0xF8);                                // mov edi,eax              (kick button)
		a.Raw(0x8D, 0x4C, 0x24, 0x14);                    // lea ecx,[esp+14h]
		a.Call(StrDtor);
		a.Raw(0x85, 0xFF);                                // test edi,edi
		int jzNoKick = a.Bytes.Count + 1;
		a.Raw(0x74, 0x00);                                // jz next
		uint jzNoKickNext = a.Here;
		a.Raw(0x8B, 0x8F, 0x18, 0x03, 0x00, 0x00);        // mov ecx,[edi+318h]       (button handler object)
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		int jzNoHandler = a.Bytes.Count + 1;
		a.Raw(0x74, 0x00);                                // jz next
		uint jzNoHandlerNext = a.Here;
		a.Raw(0x83, 0xC1, 0x14);                          // add ecx,14h              (same adjustment init uses)
		a.Raw(0x8D, 0x46, 0x2C);                          // lea eax,[esi+2Ch]        (lobby's listener interface)
		a.Raw(0x50);                                      // push eax
		a.Call(AddListener);
		uint nextAt = a.Here;
		a.Raw(0x83, 0xC3, 0x0E);                          // add ebx,14               (next name)
		a.Raw(0x80, 0x3B, 0x00);                          // cmp byte ptr [ebx],0
		int jnzLoop = a.Bytes.Count + 2;
		a.Raw(0x0F, 0x85, 0, 0, 0, 0);                    // jnz loop
		uint jnzLoopNext = a.Here;
		a.Jmp(ResumeAt);
		uint namesAt = a.Here;
		foreach (var name in PanelNames) { a.Bytes.AddRange(Encoding.ASCII.GetBytes(name)); a.Raw(0x00); }
		a.Raw(0x00);
		FixImm32(a, namesIdx, namesAt - popNames);
		FixRel32(a, jzMissing, jzMissingNext, nextAt);
		FixRel8(a, jzNoKick, jzNoKickNext, nextAt);
		FixRel8(a, jzNoHandler, jzNoHandlerNext, nextAt);
		FixRel32(a, jnzLoop, jnzLoopNext, loopAt);

		// --- click handler: kick the player whose panel owns the clicked kick button ---------------------------
		// entry state: ebp = lobby+2Ch, frame = 10h saves + 30h locals, [esp+44h] = message, [message] = component
		handlerAt = a.Here;
		a.Raw(0x8B, 0x7C, 0x24, 0x44);                    // mov edi,[esp+44h]
		a.Raw(0x8B, 0x3F);                                // mov edi,[edi]            (clicked component)
		a.Raw(0xBE, 0x01, 0x00, 0x00, 0x00);              // mov esi,1                (panel 1 = host, never kicked)
		uint hLoop = a.Here;
		a.Raw(0x3B, 0xB5, 0x3C, 0x01, 0x00, 0x00);        // cmp esi,[ebp+13Ch]       (panel count)
		int jaeDone = a.Bytes.Count + 2;
		a.Raw(0x0F, 0x83, 0, 0, 0, 0);                    // jae done
		uint jaeDoneNext = a.Here;
		a.Raw(0x83, 0xEC, 0x10);                          // sub esp,10h              (temp string)
		uint pop2; int idx2 = EmitStrAddr(a, out pop2);
		kickRefs.Add(new KeyValuePair<int, uint>(idx2, pop2));
		a.Raw(0x50);                                      // push eax
		a.Raw(0x8D, 0x4C, 0x24, 0x04);                    // lea ecx,[esp+4]
		a.Call(StrCtor);
		a.Raw(0x8B, 0x85, 0x40, 0x01, 0x00, 0x00);        // mov eax,[ebp+140h]       (panel list data)
		a.Raw(0x8B, 0x0C, 0xB0);                          // mov ecx,[eax+esi*4]      (panel i)
		a.Raw(0x54);                                      // push esp                 (&temp)
		a.Call(FindChild);
		a.Raw(0x8B, 0xD8);                                // mov ebx,eax
		a.Raw(0x8B, 0xCC);                                // mov ecx,esp
		a.Call(StrDtor);
		a.Raw(0x83, 0xC4, 0x10);                          // add esp,10h
		a.Raw(0x3B, 0xDF);                                // cmp ebx,edi
		int jeKick = a.Bytes.Count + 1;
		a.Raw(0x74, 0x00);                                // je kick
		uint jeKickNext = a.Here;
		a.Raw(0x46);                                      // inc esi
		int jmpLoop = a.Bytes.Count + 1;
		a.Raw(0xEB, 0x00);                                // jmp loop
		uint jmpLoopNext = a.Here;
		uint kickAt = a.Here;
		a.Raw(0x8B, 0x4D, 0x5C);                          // mov ecx,[ebp+5Ch]        (session)
		a.Raw(0x8B, 0x89, 0xF0, 0x96, 0x05, 0x00);        // mov ecx,[ecx+596F0h]
		a.Raw(0x8B, 0x11);                                // mov edx,[ecx]
		a.Raw(0x56);                                      // push esi                 (slot = panel index)
		a.Raw(0xFF, 0x52, 0x68);                          // call [edx+68h]           (kick)
		uint doneAt = a.Here;
		a.Jmp(KickResume);
		FixRel32(a, jaeDone, jaeDoneNext, doneAt);
		FixRel8(a, jeKick, jeKickNext, kickAt);
		FixRel8(a, jmpLoop, jmpLoopNext, hLoop);

		// --- per-member update: show the kick button inside that member's own panel ---------------------------
		// entry state: edi = lobby+54h, ebx = member slot; original code would set esi = root
		showAt = a.Here;
		a.Raw(0x8B, 0x77, 0xB0);                          // mov esi,[edi-50h]        (root, as before)
		a.Raw(0x3B, 0x9F, 0x14, 0x01, 0x00, 0x00);        // cmp ebx,[edi+114h]       (slot < panel count?)
		int jaeRoot = a.Bytes.Count + 1;
		a.Raw(0x73, 0x00);                                // jae keep root
		uint jaeRootNext = a.Here;
		a.Raw(0x8B, 0x87, 0x18, 0x01, 0x00, 0x00);        // mov eax,[edi+118h]       (panel list data)
		a.Raw(0x8B, 0x34, 0x98);                          // mov esi,[eax+ebx*4]      (that member's panel)
		uint keepAt = a.Here;
		a.Raw(0x8D, 0x4C, 0x24, 0x34);                    // lea ecx,[esp+34h]        (replaced instruction)
		a.Jmp(ShowResume);
		FixRel8(a, jaeRoot, jaeRootNext, keepAt);

		// shared string
		uint kickStrAt = a.Here;
		a.Bytes.AddRange(Encoding.ASCII.GetBytes("button_kick")); a.Raw(0x00);
		foreach (var r in kickRefs) FixImm32(a, r.Key, kickStrAt - r.Value);
	}

	public static string Build(string inPath, string outPath) { return Build(inPath, outPath, true); }

	public static string Build(string inPath, string outPath, bool kickFix) {
		return BuildVersion(inPath, outPath, kickFix, true);
	}

	static string BuildVersion(string inPath, string outPath, bool kickFix, bool capacityFix) {
		b = File.ReadAllBytes(inPath);
		string inHash = Sha(b);
		if (inHash != OriginalSha256) throw new Exception("input is not the expected empire.retail.dll build (sha256 " + inHash + ")");

		peOff = BitConverter.ToInt32(b, 0x3C);
		nSec = BitConverter.ToUInt16(b, peOff + 6);
		secTable = peOff + 24 + BitConverter.ToUInt16(b, peOff + 20);
		if (Encoding.ASCII.GetString(b, secTable, 5) != ".text") throw new Exception("first section is not .text");

		// --- make the unused file tail of .text part of the loaded section -------------------------
		uint textRva = BitConverter.ToUInt32(b, secTable + 12);
		uint textVs = BitConverter.ToUInt32(b, secTable + 8);
		uint textRaw = BitConverter.ToUInt32(b, secTable + 16);
		uint nextRva = BitConverter.ToUInt32(b, secTable + 40 + 12);
		if (textRaw <= textVs || textRva + textRaw > nextRva) throw new Exception("no usable slack after .text");
		uint caveVa = (ImageBase + textRva + textVs + 15) & ~15u;   // 16-byte aligned start inside the slack

		// --- check the original bytes at the hook: E8 rel32 -> StrCtor ---------------------------------
		int hookOff = VaToOff(HookAt);
		int origRel = BitConverter.ToInt32(b, hookOff + 1);
		if (b[hookOff] != 0xE8 || (uint)(HookAt + 5 + origRel) != StrCtor) throw new Exception("unexpected bytes at hook site");

		// --- cave code ---------------------------------------------------------------------------------
		var a = new Asm { Va = caveVa };
		// finish the panel_player2 lookup exactly as the original code did
		a.Call(StrCtor);                                  // ecx = temp string, arg "panel_player2" already pushed
		a.Raw(0x8D, 0x44, 0x24, 0x14);                    // lea eax,[esp+14h]
		a.Raw(0x8B, 0xCF);                                // mov ecx,edi            (edi = root component)
		a.Raw(0x50);                                      // push eax
		a.Call(FindChild);
		a.Raw(0x89, 0x44, 0x24, 0x0C);                    // mov [esp+0Ch],eax
		a.Raw(0x8D, 0x8E, 0x64, 0x01, 0x00, 0x00);        // lea ecx,[esi+164h]     (panel list)
		a.Raw(0x8D, 0x44, 0x24, 0x0C);                    // lea eax,[esp+0Ch]
		a.Raw(0x50);                                      // push eax
		a.Call(PushBack);
		a.Raw(0x8D, 0x4C, 0x24, 0x14);                    // lea ecx,[esp+14h]
		a.Call(StrDtor);
		uint handlerAt = 0, showAt = 0;
		if (kickFix) EmitStage2(a, out handlerAt, out showAt);
		else {
		// panels 3..8: ebx walks a NUL-separated name list stored right after the code
		a.Raw(0xE8, 0x00, 0x00, 0x00, 0x00);              // call $+5
		uint popAt = a.Here;
		a.Raw(0x5B);                                      // pop ebx                (ebx = popAt; ebx is saved by the prologue)
		int addImmIndex = a.Bytes.Count + 2;
		a.Raw(0x83, 0xC3, 0x00);                          // add ebx, names - popAt (filled below)
		uint loopAt = a.Here;
		a.Raw(0x8B, 0x7E, 0x04);                          // mov edi,[esi+4]
		a.Raw(0x8D, 0x4C, 0x24, 0x14);                    // lea ecx,[esp+14h]
		a.Raw(0x53);                                      // push ebx               (name)
		a.Call(StrCtor);
		a.Raw(0x8D, 0x44, 0x24, 0x14);                    // lea eax,[esp+14h]
		a.Raw(0x8B, 0xCF);                                // mov ecx,edi
		a.Raw(0x50);                                      // push eax
		a.Call(FindChild);
		a.Raw(0x89, 0x44, 0x24, 0x0C);                    // mov [esp+0Ch],eax
		a.Raw(0x85, 0xC0);                                // test eax,eax
		int jzIndex = a.Bytes.Count + 1;
		a.Raw(0x74, 0x00);                                // jz skip                (panel missing: don't add it)
		uint jzNext = a.Here;
		a.Raw(0x8D, 0x8E, 0x64, 0x01, 0x00, 0x00);        // lea ecx,[esi+164h]
		a.Raw(0x8D, 0x44, 0x24, 0x0C);                    // lea eax,[esp+0Ch]
		a.Raw(0x50);                                      // push eax
		a.Call(PushBack);
		uint skipAt = a.Here;
		a.Raw(0x8D, 0x4C, 0x24, 0x14);                    // skip: lea ecx,[esp+14h]
		a.Call(StrDtor);
		a.Raw(0x83, 0xC3, 0x0E);                          // add ebx,14             (next name; each is 13 chars + NUL)
		a.Raw(0x80, 0x3B, 0x00);                          // cmp byte ptr [ebx],0
		int jnzIndex = a.Bytes.Count + 1;
		a.Raw(0x75, 0x00);                                // jnz loop
		uint jnzNext = a.Here;
		a.Jmp(ResumeAt);
		uint namesAt = a.Here;
		foreach (var name in PanelNames) {
			if (name.Length != 13) throw new Exception("name length must be 13");
			a.Bytes.AddRange(Encoding.ASCII.GetBytes(name)); a.Raw(0x00);
		}
		a.Raw(0x00);                                      // end of list

		// fill in the short displacements (imm8 of 'add ebx' is sign-extended, so it must stay <= 127)
		if (namesAt - popAt > 127) throw new Exception("name list too far from pop for an imm8");
		a.Bytes[addImmIndex] = (byte)(namesAt - popAt);
		a.Bytes[jzIndex] = checked((byte)(skipAt - jzNext));
		a.Bytes[jnzIndex] = unchecked((byte)(sbyte)checked((int)loopAt - (int)jnzNext));
		}

		uint caveEnd = caveVa + (uint)a.Bytes.Count;
		if (caveEnd > ImageBase + textRva + textRaw) throw new Exception("cave does not fit in .text slack");

		// --- apply --------------------------------------------------------------------------------------
		int caveOff = (int)(BitConverter.ToUInt32(b, secTable + 20) + (caveVa - ImageBase - textRva));
		for (int i = 0; i < a.Bytes.Count; i++) {
			if (b[caveOff + i] != 0) throw new Exception("slack is not empty at " + (caveVa + i).ToString("X8"));
			b[caveOff + i] = a.Bytes[i];
		}
		// grow .text's in-memory size to cover the cave (still below the next section)
		uint newVs = caveEnd - ImageBase - textRva;
		Array.Copy(BitConverter.GetBytes(newVs), 0, b, secTable + 8, 4);
		// hook: CALL StrCtor -> JMP cave (same 5 bytes)
		b[hookOff] = 0xE9;
		Array.Copy(BitConverter.GetBytes((int)(caveVa - (HookAt + 5))), 0, b, hookOff + 1, 4);
		if (kickFix) {
			// click handler: mov edx,[ebp+130h] -> jmp handler detour
			ExpectBytes(KickHookAt, 0x8B, 0x95, 0x30, 0x01, 0x00, 0x00);
			PutJmp(KickHookAt, handlerAt, 6);
			// per-member update: mov esi,[edi-50h] / lea ecx,[esp+34h] -> jmp show detour
			ExpectBytes(ShowHookAt, 0x8B, 0x77, 0xB0, 0x8D, 0x4C, 0x24, 0x34);
			PutJmp(ShowHookAt, showAt, 7);
		}

		if (capacityFix) {
			// The native host_campaign screen does NOT call Lua MPHostCampaign (11059200).
			// Its own settings ctor at 10FD2848 receives (players=2, spectators=0, sides=2).
			// Change only the player-count argument: push 2 / push 0 / push 2 -> ... / push 8.
			ExpectBytes(0x10FD283B, 0x6A, 0x02, 0x6A, 0x00, 0x6A, 0x02);
			b[VaToOff(0x10FD2840)] = (byte)MaxPlayers;
		}

		// --- engine fixes in the new section ----------------------------------------------------------
		var s = new Asm { Va = NewSectionVa() };
		EmitModLog(s);                                    // first: every later stub may log
		uint allyAt = EmitAllyResultFix(s);
		ExpectBytes(AllyHookAt, 0x33, 0xC0, 0x38, 0x44, 0x24, 0x1C, 0x0F, 0x94, 0xC0, 0x89, 0x43, 0x04);
		PutJmp(AllyHookAt, allyAt, 12);
		uint giftAt = EmitGiftPartnerPicker(s);
		RedirectCall(GiftPickCall, 0x110DE560, giftAt);
		foreach (uint c in OwnerCheckCalls) RedirectCall(c, 0x110DE560, giftAt);
		uint sideAt = EmitSideOrSelf(s);
		foreach (uint c in SideLookupCalls) RedirectCall(c, BattleSideOfClan, sideAt);
		uint errAt = EmitErrorLog(s);
		ExpectBytes(ErrorMessageAt, 0x83, 0xC4, 0x1C, 0x85, 0xC0, 0x74, 0x4D);
		PutJmp(ErrorMessageAt, errAt, 7);
		ReserveDataDword(GiftCounterVa);
		uint resyncAt = EmitAutoResync(s);
		RedirectCall(ResyncNotifyCall, NotifyListeners, resyncAt);
		uint inviteAt = EmitInviteWiring(s);
		ExpectBytes(InviteWireAt, 0x8B, 0x73, 0x04, 0x8D, 0x4C, 0x24, 0x20);
		PutJmp(InviteWireAt, inviteAt, 7);
		ExpectBytes(SaveScreenLocaleCategory, 0x6A, 0x00, 0xE8);
		b[VaToOff(SaveScreenLocaleCategory) + 1] = 5;
		uint slotFromFile, slotCore, byte30At;
		uint slotAt = EmitSaveSlot(s, out slotFromFile, out slotCore);
		RedirectCall(SaveSlotCallSession, SaveSlotOf, slotAt);
		RedirectCall(SaveSlotCallFile, SaveSlotOf, slotFromFile);
		uint byte24At = EmitSaveSlotByte(s, slotCore, out byte30At);
		RedirectCall(SaveByte24Call, SaveByte24, byte24At);
		RedirectCall(SaveByte30Call, SaveByte30, byte30At);
		reservedTestAt = EmitReservedTest(s);
		uint countPlusAt = EmitMemberCountPlus(s, reservedTestAt);
		RedirectCall(MemberCountCall1, MemberCount, countPlusAt);
		RedirectCall(MemberCountCall2, MemberCount, countPlusAt);
		uint entryPlusAt = EmitMemberEntryPlus(s, reservedTestAt);
		RedirectCall(MemberEntryCall, MemberEntry, entryPlusAt);
		uint framePinAt = EmitFramePin(s);
		RedirectCall(FrameUpdateCall, MultiplayerUpdate, framePinAt);
		uint tickPinAt = EmitBattleTickPin(s);
		ExpectBytes(BattleTickAt, 0xFF, 0x74, 0x24, 0x08, 0x8B, 0x4C, 0x24, 0x10);
		PutJmp(BattleTickAt, tickPinAt, 8);
		uint noKickAt = EmitBattleNoKick(s);
		ExpectBytes(DesyncKickAt, 0x8B, 0x8F, 0x74, 0xBE, 0x02, 0x00);
		PutJmp(DesyncKickAt, noKickAt, 6);
		uint reportNoKickAt = EmitReportNoKick(s);
		ExpectBytes(ReportKickAt, 0x55, 0x57, 0x8D, 0x44, 0x24, 0x10);
		PutJmp(ReportKickAt, reportNoKickAt, 6);
		uint forcedAt = EmitForcedResyncAnswer(s);
		RedirectCall(ResyncAnswerCall, ResyncAnswer, forcedAt);
		ReserveDataDword(ResyncKeyPrevVa);
		ExpectBytes(NonParticipantCountJump - 3, 0x83, 0xF9, 0x02, 0x0F, 0x85);
		b[VaToOff(NonParticipantCountJump) + 1] = 0x82;
		uint leaveAt = EmitLeaveBattleRule(s);
		ExpectBytes(LeaveBattleCountAt, 0x83, 0xF8, 0x02, 0x77, 0x0E);
		PutJmp(LeaveBattleCountAt, leaveAt, 5);
		uint coopAt = EmitCoopMember(s);
		RedirectCall(CoopMemberCall, IsCoopRecord, coopAt);
		uint loadAt = EmitLoadScreenPlayers(s);
		ExpectBytes(LoadPortraitsAt, 0x8B, 0x45, 0x4C, 0x33, 0xF6);
		PutJmp(LoadPortraitsAt, loadAt, 5);
		uint titleAt = EmitTitleCount(s);
		ExpectBytes(MemberUpdateAt, 0x81, 0xEC, 0x84, 0x00, 0x00, 0x00);
		PutJmp(MemberUpdateAt, titleAt, 6);
		ReserveDataDword(CrashPrevFilterVa);
		ReserveDataDword(CrashInstalledVa);
		ReserveDataDword(ResyncPendingVa);
		ReserveDataDword(StandInSideVa); ReserveDataDword(StandInSideVa + 4);
		uint lobbyRecordAt = EmitLobbyCountRecord(s);
		ExpectBytes(LobbyRecordAt, 0x83, 0xBB, 0x88, 0x00, 0x00, 0x00, 0x01);
		PutJmp(LobbyRecordAt, lobbyRecordAt, 7);
		ExpectBytes(ResumeJoinCheckJump, 0x75, 0x59);
		b[VaToOff(ResumeJoinCheckJump)] = 0xEB;
		ExpectBytes(ResumeLobbyCompatCmov, 0x0F, 0x44, 0xC1);
		for (int i = 0; i < 3; i++) b[VaToOff(ResumeLobbyCompatCmov) + i] = 0x90;
		ExpectBytes(ResumeListCmov, 0x0F, 0x45, 0xCA);
		b[VaToOff(ResumeListCmov)] = 0x8B; b[VaToOff(ResumeListCmov) + 1] = 0xCA; b[VaToOff(ResumeListCmov) + 2] = 0x90;
		uint statusAt = EmitStatusCount(s);
		RedirectCall(StatusStateCall, SetState, statusAt);
		ReserveDataDword(LobbyCursorVa);
		ReserveDataDword(LobbyTableVa + LobbySlots * 16 - 4);   // the whole table (zero-filled)
		uint saveNameAt = EmitSaveName(s);
		RedirectCall(SaveNameAssignCall, WStrAssign, saveNameAt);
		ReserveDataDword(ReconcileEnvVa); ReserveDataDword(ReconcileWaitVa);
		ReserveDataDword(ResyncAcceptsVa); ReserveDataDword(ResyncWaitVa);
		ReserveDataDword(PhantomVa + 0xFC);                     // the stand-in member entry
		uint aiPlayedAt = EmitAiPlayedCheck(s);
		ExpectBytes(AiPlayedCheck, 0x83, 0x79, 0x08, 0x02, 0x75, 0x12);
		PutJmp(AiPlayedCheck, aiPlayedAt, 6);
		uint chooserAt = EmitAiChooserTest(s, aiPlayedAt);
		uint npEntryAt = EmitNonParticipantEntry(s);
		ExpectBytes(NonParticipantEntry, 0x83, 0xEC, 0x0C, 0x56, 0x57);
		PutJmp(NonParticipantEntry, npEntryAt, 5);
		uint npAiAt = EmitNonParticipantAiCall(s, chooserAt);
		foreach (uint c in NonParticipantAiCalls) RedirectCall(c, AiPlayedCheck, npAiAt);
		RedirectCall(SideOfClanAiCall, AiPlayedCheck, EmitSideOfClanAiCall(s, chooserAt));
		ExpectBytes(AiArmyHumansJump - 3, 0x83, 0xFB, 0x02, 0x75, 0x23);
		b[VaToOff(AiArmyHumansJump)] = 0x72;
		ReserveDataDword(AskingClanVa);
		RedirectSlot(WaitingShowSlot, 0x11153FA0, EmitWaitingLog(s, 0x11153FA0, "campaign map: 'waiting for players' screen opened (every PC loads the battle)"));
		RedirectSlot(WaitingHideSlot, 0x11153F50, EmitWaitingLog(s, 0x11153F50, "campaign map: 'waiting for players' screen closed"));
		ReserveDataDword(LogStartedVa);
		ReserveDataDword(WatchVa + (WatchSlots - 1) * 4);
		AddSection(".s2mp", s.Bytes);

		File.WriteAllBytes(outPath, b);
		return string.Format("patched{7}: hook {0:X8} -> cave {1:X8}..{2:X8} ({3} bytes), .text VirtualSize {4:X8} -> {5:X8}; output sha256 {6}",
			HookAt, caveVa, caveEnd, a.Bytes.Count, textVs, newVs, Sha(b),
			kickFix ? string.Format(" (+kick fix: handler {0:X8}, show {1:X8})", handlerAt, showAt) : "");
	}
}
