# Map the patched empire.retail.dll with the real Windows loader (no code runs: DONT_RESOLVE_DLL_REFERENCES)
# and check the patch in memory. Must run in 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -ExecutionPolicy Bypass -File verify_load.ps1 <dll>
param([Parameter(Mandatory = $true)][string]$Dll)
if ([IntPtr]::Size -ne 4) { throw "run this in 32-bit PowerShell (SysWOW64)" }
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Ld {
	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	public static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
	[DllImport("kernel32.dll")] public static extern bool FreeLibrary(IntPtr h);
	public static byte[] Read(IntPtr p, int n) { var b = new byte[n]; Marshal.Copy(p, b, 0, n); return b; }
}
"@
$h = [Ld]::LoadLibraryExW((Resolve-Path $Dll), [IntPtr]::Zero, 0x1)   # DONT_RESOLVE_DLL_REFERENCES
if ($h -eq [IntPtr]::Zero) { throw "LoadLibraryEx failed, error $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
$base = [uint32]$h.ToInt32()
$delta = [int64]$base - 0x10000000
"mapped at 0x{0:X8} (preferred 0x10000000, delta {1})" -f $base, $delta
function At([uint32]$va, [int]$n) { [Ld]::Read([IntPtr]([int64]$va - 0x10000000 + $base), $n) }
function Hex($b) { ($b | ForEach-Object { $_.ToString('X2') }) -join ' ' }

$ok = $true
# 1. kept original push of "panel_player2": its imm32 must have been relocated by the loader
$push = At 0x11029CB5 5
$imm = [BitConverter]::ToUInt32($push, 1)
$want = [uint32](0x116F1D84 + $delta)
"push panel_player2 : {0}  imm {1:X8} expected {2:X8}" -f (Hex $push), $imm, $want
if ($push[0] -ne 0x68 -or $imm -ne $want) { $ok = $false }
# 2. hook jmp: relative, so unchanged by relocation, and must land on the cave in memory
$jmp = At 0x11029CBA 5
$dest = [int64]0x11029CBF + [BitConverter]::ToInt32($jmp, 1)
"hook               : {0}  -> {1:X8} (expected 115A0C40)" -f (Hex $jmp), $dest
if ($jmp[0] -ne 0xE9 -or $dest -ne 0x115A0C40) { $ok = $false }
# 3. cave bytes must be mapped (not zero) and identical to the file (nothing in it is relocatable)
$cave = At 0x115A0C40 440
$file = [IO.File]::ReadAllBytes((Resolve-Path $Dll))
$fileCave = New-Object byte[] 440; [Array]::Copy($file, 0x15A0040, $fileCave, 0, 440)
$same = [Linq.Enumerable]::SequenceEqual([byte[]]$cave, [byte[]]$fileCave)
"cave mapped        : first bytes {0} ...; identical to file: {1}" -f (Hex $cave[0..7]), $same
if (-not $same -or ($cave | Measure-Object -Maximum).Maximum -eq 0) { $ok = $false }
# 4. native campaign hosting must request eight players, zero spectators, two sides.
$capacity = At 0x10FD283B 6
"campaign capacity  : {0} (expected 6A 02 6A 00 6A 08)" -f (Hex $capacity)
if ((Hex $capacity) -ne '6A 02 6A 00 6A 08') { $ok = $false }
# 5. engine fixes in the new .s2mp section: hooks must land in it and its code must be mapped as in the file
$pe = [BitConverter]::ToInt32($file, 0x3C); $nSec = [BitConverter]::ToUInt16($file, $pe + 6)
$sec = $pe + 24 + [BitConverter]::ToUInt16($file, $pe + 20) + ($nSec - 1) * 40
$secName = [Text.Encoding]::ASCII.GetString($file, $sec, 5)
$secVa = [uint32](0x10000000 + [BitConverter]::ToUInt32($file, $sec + 12)); $secLen = [BitConverter]::ToInt32($file, $sec + 8)
$secRaw = [BitConverter]::ToInt32($file, $sec + 20)
$fileSec = New-Object byte[] $secLen; [Array]::Copy($file, $secRaw, $fileSec, 0, $secLen)
$sameSec = [Linq.Enumerable]::SequenceEqual([byte[]](At $secVa $secLen), [byte[]]$fileSec)
"fix section        : {0} at {1:X8}, {2} bytes; identical to file: {3}" -f $secName, $secVa, $secLen, $sameSec
if ($secName -ne '.s2mp' -or -not $sameSec) { $ok = $false }
foreach ($hook in @(@(0x11107F30, 'ally battle result'), @(0x110E50FA, 'gift recipient'), @(0x10F05914, 'auto resync'), @(0x10FD5B70, 'invite buttons'), @(0x10EF9A20, 'session update (per frame)'), @(0x10227D40, 'tick fp pin'), @(0x10EE9A71, 'battle no-kick'), @(0x10EF9A87, 'report no-kick'), @(0x10F10E2D, 'forced resync'), @(0x1068E124, 'co-op team'), @(0x10FC93B7, 'load screen players'), @(0x10FD6650, 'title count'), @(0x10F1F91E, 'leave battle (campaign)'), @(0x100C1B2B, 'engine error log'), @(0x110FCCAE, 'gift owner check'), @(0x11101142, 'command check 1'), @(0x111011EE, 'command check 2'), @(0x105B50D7, 'side lookup 1'), @(0x105B5120, 'side lookup 2'), @(0x10FF5337, 'lobby count record'), @(0x11049DF4, 'games list count'), @(0x10EF1DAF, 'resume faction (session)'), @(0x10EF49F1, 'resume faction (file)'), @(0x10F2C8AC, 'resume slot byte 24h'), @(0x10F2C8BE, 'resume slot byte 30h'), @(0x10EEBD77, 'save list count 1'), @(0x10EEC13A, 'save list count 2'), @(0x10EEBD87, 'save list entry'), @(0x106A3DFD, 'save name'), @(0x105CBB50, 'play-as-AI check'), @(0x105CEFA0, 'non-fighter entry'), @(0x105CF092, 'non-fighter AI 1'), @(0x105CF0C2, 'non-fighter AI 2'), @(0x105B7BB1, 'side of clan AI'))) {
	$ins = At $hook[0] 5
	$dest = [int64]$hook[0] + 5 + [BitConverter]::ToInt32($ins, 1)
	$inSec = $dest -ge $secVa -and $dest -lt ($secVa + $secLen)
	"hook {0,-14}: {1:X8} {2} -> {3:X8} (in .s2mp: {4})" -f $hook[1], $hook[0], (Hex $ins[0..0]), $dest, $inSec
	if (-not $inSec) { $ok = $false }
}
# listener slots redirected to logging stubs: absolute pointers, so the loader must have relocated them into .s2mp
foreach ($slot in @(@(0x1171534C, 'waiting screen opened'), @(0x11715358, 'waiting screen closed'))) {
	$dest = [int64][BitConverter]::ToUInt32((At $slot[0] 4), 0) - $delta
	$inSec = $dest -ge $secVa -and $dest -lt ($secVa + $secLen)
	"slot {0,-21}: {1:X8} -> {2:X8} (in .s2mp: {3})" -f $slot[1], $slot[0], $dest, $inSec
	if (-not $inSec) { $ok = $false }
}
# save/load screens must switch only LC_TIME (push 5), not LC_ALL (push 0)
$loc = At 0x110B84E1 3
"save screen locale : {0} (expected 6A 05 E8)" -f (Hex $loc)
if ((Hex $loc) -ne '6A 05 E8') { $ok = $false }
# the gift turn counter lives just past .data's original size; it must be mapped and zero
$counter = [BitConverter]::ToUInt32((At 0x11CB1530 4), 0)
"gift counter       : {0}" -f $counter
if ($counter -ne 0) { $ok = $false }
[void][Ld]::FreeLibrary($h)
if ($ok) { "RESULT: patched DLL loads and the patch is intact in memory" } else { "RESULT: PROBLEM - see above"; exit 1 }
