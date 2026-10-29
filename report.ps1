# Gathers everything a bug report needs into ONE plain-text file on the Desktop (Shogun2-8Player-Report.txt):
# what the player says happened, an automatic summary of the mod's event log, mod version and hashes, MATCH CODE,
# other mods and game packs, PC details that matter for desyncs (Windows number format, CPU, overlays), the
# game's multiplayer settings, the event log itself, crash/error/desync files and the game's logs folder.
# Text only, so it can be attached anywhere (Discord, GitHub, AI assistants). Reads files only; changes nothing.
#   powershell -ExecutionPolicy Bypass -File report.ps1 [-GamePath "D:\...\Total War SHOGUN 2"] [-NoQuestions]
param(
	[string]$GamePath = "",
	[string]$Out = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Shogun2-8Player-Report.txt'),
	[switch]$NoQuestions
)
$ErrorActionPreference = 'Stop'
$lib = Join-Path $PSScriptRoot 'lib'
. (Join-Path $lib 'MatchCode.ps1')
$appData = Join-Path $env:APPDATA 'The Creative Assembly\Shogun2'
$userScript = Join-Path $appData 'scripts\user.script.txt'
if (-not $GamePath) { $GamePath = Find-ShogunFolder }
if (-not $GamePath) { throw "Couldn't find Total War: Shogun 2. Run with -GamePath ""<your game folder>""." }

$sb = New-Object Text.StringBuilder
function Line([string]$s = "") { [void]$sb.AppendLine($s) }
function Section([string]$title) { Line; Line ("=" * 100); Line "== $title"; Line ("=" * 100) }
function Try-Line([string]$label, [scriptblock]$value) { try { Line ("{0,-22}: {1}" -f $label, (& $value)) } catch { Line ("{0,-22}: (unknown)" -f $label) } }
# a text file, or its last $tail lines
function Dump([string]$path, [int]$tail = 0) {
	if (-not (Test-Path $path)) { Line "(not found: $path)"; return }
	$f = Get-Item $path
	Line ("--- {0}  ({1} bytes, changed {2:yyyy-MM-dd HH:mm:ss})" -f $f.FullName, $f.Length, $f.LastWriteTime)
	$lines = @(Get-Content $path)
	if ($tail -gt 0 -and $lines.Count -gt $tail) { Line "(last $tail of $($lines.Count) lines)"; $lines = $lines[($lines.Count - $tail)..($lines.Count - 1)] }
	foreach ($l in $lines) { Line $l }
}
# the mod's event log, with runs of the same message (timestamp aside) merged into one line with a count
function Read-MergedLog([string]$path) {
	$out = New-Object Collections.Generic.List[string]
	$prev = $null; $first = $null; $last = $null; $count = 0
	$flush = {
		if ($count -eq 0) { return }
		if ($count -eq 1) { $out.Add($first) }
		else { $out.Add("$first   [repeated $count times, last at $($last.Substring(0, [Math]::Min(12, $last.Length)))]") }
	}
	foreach ($l in [IO.File]::ReadLines($path)) {
		$msg = if ($l -match '^\d\d:\d\d:\d\d\.\d{3}\s+(.*)$') { $Matches[1] } else { $l }
		if ($msg -eq $prev) { $count++; $last = $l; continue }
		& $flush
		$prev = $msg; $first = $l; $last = $l; $count = 1
	}
	& $flush
	return , $out
}
# files the game wrote into its own folder may have been redirected by Windows when the game is in Program Files
$virtual = Join-Path $env:LOCALAPPDATA ('VirtualStore\' + ($GamePath -replace '^[A-Za-z]:\\', ''))
function GameFile([string]$name) {
	foreach ($d in @($GamePath, $virtual)) { $p = Join-Path $d $name; if (Test-Path $p) { return $p } }
	return (Join-Path $GamePath $name)
}

# ---- what the player says ----------------------------------------------------------------------------------
$answers = [ordered]@{}
if (-not $NoQuestions) {
	""
	"A few questions (press Enter to skip any):"
	foreach ($q in @('Were you the host? (y/n)', 'How many players were in the game?', 'Co-op or head-to-head?',
			'Did anyone choose "Play as AI" in the last battle? (y/n)', 'What happened, and when? (one line)')) {
		try { $answers[$q] = Read-Host "  $q" } catch { $answers[$q] = '' }
	}
}

$version = '?'
$m = Select-String -Path (Join-Path $lib 'Patcher.cs') -Pattern 'public const string Version = "([^"]+)"' | Select-Object -First 1
if ($m) { $version = $m.Matches[0].Groups[1].Value }
$match = Get-MatchInfo $GamePath $userScript
$logPath = GameFile 'eightplayer_log.txt'
$merged = if (Test-Path $logPath) { Read-MergedLog $logPath } else { New-Object Collections.Generic.List[string] }

Line "Shogun 2 8-player co-op mod: bug report"
Line ("Written {0:yyyy-MM-dd HH:mm:ss} (UTC{0:zzz}) by report.ps1 of mod v{1}" -f (Get-Date), $version)

Section "What the player says"
if ($answers.Count) { foreach ($k in $answers.Keys) { Line ("{0} {1}" -f $k, $(if ($answers[$k]) { $answers[$k] } else { '(skipped)' })) } }
else { Line "(no answers given)" }

# ---- automatic summary of the event log ---------------------------------------------------------------------
Section "Automatic summary (read this first)"
$notes = New-Object Collections.Generic.List[string]
if (-not (Test-Path $logPath)) { $notes.Add("No eightplayer_log.txt: the mod never wrote to its log on this PC (not installed, or no multiplayer game since v1.13).") }
else {
	$all = [IO.File]::ReadAllLines($logPath)
	$runs = @($all | Where-Object { $_ -match '=== Shogun 2 8-player co-op mod v(\S+): game started' })
	Line ("Event log: {0} lines, {1} game launches; last launch ran mod v{2}" -f $all.Count, $runs.Count, $(if ($runs.Count) { ([regex]::Match($runs[-1], 'v(\S+):')).Groups[1].Value } else { '?' }))
	# only the last launch matters most
	$lastStart = 0; for ($i = $all.Count - 1; $i -ge 0; $i--) { if ($all[$i] -match 'game started ===') { $lastStart = $i; break } }
	$cur = $all[$lastStart..($all.Count - 1)]
	$state = [ordered]@{}
	foreach ($l in $cur) {
		if ($l -match '^\S+\s+(multiplayer session|this PC is the host|players|game state \(3 = [^)]*\)|campaign environment|battle environment.*?|resync state.*?|campaign map shown|battle shown|resync waiting to start|resync: players ready \(host\)|battle over.*?|sync check: players out of sync with the host) (\d+)\s*$') { $state[$Matches[1].Trim()] = $Matches[2] }
	}
	$count = { param($rx) @($cur | Where-Object { $_ -match $rx }).Count }
	Line ("Last launch: {0} lines" -f $cur.Count)
	Line ("  desyncs detected on this PC     : {0} log lines" -f (& $count 'DESYNC detected'))
	Line ("  out-of-sync sync checks         : {0} log lines" -f (& $count 'DESYNC: sync check found'))
	Line ("  Ctrl+Shift+F9 presses           : {0}" -f (& $count 'Ctrl\+Shift\+F9'))
	Line ("  resyncs started (auto / key)    : {0} / {1}" -f (& $count 'automatic resync started'), (& $count 'Ctrl\+Shift\+F9: resync started'))
	Line ("  'waiting for players' opened    : {0}, closed {1}" -f (& $count "screen opened"), (& $count "screen closed"))
	Line ("  battle role changes             : {0}" -f (& $count 'battle role check'))
	if ($state.Count) { Line "  last known state:"; foreach ($k in $state.Keys) { Line ("    {0,-60} {1}" -f $k, $state[$k]) } }
	if ((& $count 'DESYNC reported by another player') -gt 0) { $notes.Add("'DESYNC reported by another player' lines (written by mod v1.13) are NOT desyncs: they were written after every routine sync check, about 3 times a second. Only 'DESYNC detected on this PC' and (v1.14+) 'DESYNC: sync check found ...' lines are real.") }
	$real = (& $count 'DESYNC detected|DESYNC: sync check found')
	if ($cur.Count -gt 1 -and $state.Count -eq 0) { $notes.Add("No state lines in this launch: the mod's per-frame code did not run (the v1.13 bug fixed in v1.14). Ctrl+Shift+F9 and the automatic resync could not work. Update to v1.14 or later.") }
	if ($real -gt 0 -and (& $count 'resync started') -eq 0) { $notes.Add("Desyncs happened but no resync was started. The 'why' lines (game state x10 + busy flag) say what blocked it.") }
	if ((& $count 'battle role check \(battle choices x100 \+ someone plays the AI army x10 \+ this PC plays it\) \d*1\d$') -gt 0) { $notes.Add("Someone played as the AI army in a battle (battle role check ...1x).") }
	$open = & $count "screen opened"; $closed = & $count "screen closed"
	if ($open -gt $closed) { $notes.Add("The 'waiting for players' screen opened more often than it closed: a battle load probably hung on this PC.") }
	if ($runs.Count -and $runs[-1] -notmatch "v$([regex]::Escape($version)):") { $notes.Add("The last game launch ran a different mod version than this report script (v$version). Everyone must reinstall the same version.") }
}
if ($match.Others.Count) { $notes.Add("Other mods are enabled: $($match.Others -join ', '). Every player must have exactly the same ones (same MATCH CODE).") }
$orig = Join-Path $GamePath 'empire.retail.dll.8p-original'
if (-not (Test-Path $orig)) { $notes.Add("No original DLL backup: the mod may not be installed on this PC.") }
if ($notes.Count) { Line; Line "Notes:"; foreach ($n in $notes) { Line "  - $n" } } else { Line; Line "Notes: nothing unusual found automatically." }

# ---- setup ----------------------------------------------------------------------------------------------------
Section "Setup"
Line "Game folder           : $GamePath"
Line "MATCH CODE            : $($match.Code)   (every player must have the same code)"
Line "empire.retail.dll     : $($match.DllSha)"
Line "eightplayer pack      : $($match.PackSha)"
Line ("Original backup       : {0}" -f $(if (Test-Path $orig) { (Get-FileHash $orig -Algorithm SHA256).Hash } else { 'none (mod not installed?)' }))
Line ("Other mods            : {0}" -f $(if ($match.Others.Count) { $match.Others -join ', ' } else { 'none' }))
Try-Line 'Shogun2.exe' { $f = Get-Item (Join-Path $GamePath 'Shogun2.exe'); "{0} bytes, {1:yyyy-MM-dd}, version {2}" -f $f.Length, $f.LastWriteTime, $f.VersionInfo.FileVersion }

Section "PC (things that can make PCs calculate differently)"
Try-Line 'Windows' { $o = Get-CimInstance Win32_OperatingSystem; "{0} build {1}, {2}" -f $o.Caption, $o.BuildNumber, $o.OSArchitecture }
Try-Line 'CPU' { ((Get-CimInstance Win32_Processor | Select-Object -First 1).Name).Trim() }
Try-Line 'GPU' { (@(Get-CimInstance Win32_VideoController | ForEach-Object { "$($_.Name) (driver $($_.DriverVersion))" }) -join '; ') }
Try-Line 'RAM' { "{0:N1} GB" -f ((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB) }
Try-Line 'Language / numbers' { $c = Get-Culture; "{0}; decimal '{1}', thousands '{2}'" -f $c.Name, $c.NumberFormat.NumberDecimalSeparator, $c.NumberFormat.NumberGroupSeparator }
Try-Line 'Time zone' { [TimeZoneInfo]::Local.DisplayName }
Try-Line 'Overlays running' {
	$known = 'Discord', 'RTSS', 'MSIAfterburner', 'Overwolf', 'NVIDIA Share', 'nvcontainer', 'GameBar', 'XboxGameBar', 'RazerCortex', 'obs64', 'obs32', 'Medal', 'Fraps', 'ReShade', 'EpicGamesLauncher', 'EADesktop', 'RadeonSoftware'
	$p = @(Get-Process | Where-Object { $known -contains $_.ProcessName } | Select-Object -ExpandProperty ProcessName -Unique)
	if ($p.Count) { $p -join ', ' } else { 'none of the known ones' }
}
Try-Line 'DLLs in game folder' { $d = @(Get-ChildItem $GamePath -Filter *.dll | Where-Object { 'd3d9.dll', 'd3d11.dll', 'dxgi.dll', 'dinput8.dll', 'version.dll', 'winmm.dll' -contains $_.Name.ToLower() } | Select-Object -ExpandProperty Name); if ($d.Count) { "$($d -join ', ') (graphics/injector mods such as ReShade)" } else { 'no injector DLLs' } }

Section "Game packs in data\ (type 4 = loaded automatically; others only when enabled)"
foreach ($f in Get-ChildItem (Join-Path $GamePath 'data') -Filter *.pack | Sort-Object Name) {
	$h = New-Object byte[] 8
	try { $fs = [IO.File]::OpenRead($f.FullName); try { [void]$fs.Read($h, 0, 8) } finally { $fs.Close() } } catch { }
	$type = [BitConverter]::ToUInt32($h, 4)
	Line ("{0,-50} {1,14:N0} bytes  type {2}  {3:yyyy-MM-dd}" -f $f.Name, $f.Length, $type, $f.LastWriteTime)
}

Section "Game settings that matter for multiplayer"
Line "--- $userScript"
if (Test-Path $userScript) { foreach ($l in Get-Content $userScript) { Line $l } } else { Line "(none)" }
$prefs = Join-Path $appData 'scripts\preferences.script.txt'
Line; Line "--- $prefs (multiplayer, network and graphics API lines)"
if (Test-Path $prefs) { foreach ($l in (Get-Content $prefs | Where-Object { $_ -match 'mp_|network|sync|dx|direct|gfx_api|desync|kick' })) { Line $l } } else { Line "(not found)" }

Section "Newest multiplayer saves"
$saves = Join-Path $appData 'save_games'
if (Test-Path $saves) { foreach ($f in Get-ChildItem $saves -File | Where-Object { $_.Extension -match 'multiplayer' } | Sort-Object LastWriteTime -Descending | Select-Object -First 8) { Line ("{0:yyyy-MM-dd HH:mm:ss}  {1,12:N0} bytes  {2}" -f $f.LastWriteTime, $f.Length, $f.Name) } }
else { Line "(no save_games folder)" }

Section "Mod event log (eightplayer_log.txt), repeats merged, newest at the bottom"
if (Test-Path $logPath) {
	$f = Get-Item $logPath
	Line ("--- {0}  ({1} bytes, changed {2:yyyy-MM-dd HH:mm:ss})" -f $f.FullName, $f.Length, $f.LastWriteTime)
	$tail = 3000
	if ($merged.Count -gt $tail) { Line "(last $tail of $($merged.Count) entries)"; $merged = $merged.GetRange($merged.Count - $tail, $tail) }
	foreach ($l in $merged) { Line $l }
}
else { Line "(not found: $logPath)" }

Section "Crash report (eightplayer_crash.txt)"
Dump (GameFile 'eightplayer_crash.txt')
$dmp = GameFile 'eightplayer_crash.dmp'
if (Test-Path $dmp) { Line ("(a crash dump also exists: {0}, {1} bytes; only needed if asked for)" -f $dmp, (Get-Item $dmp).Length) }
Section "Engine errors before an instant close (eightplayer_errors.txt)"
Dump (GameFile 'eightplayer_errors.txt') 300

Section "Desync reports written by the game"
$found = $false
foreach ($d in @($GamePath, $virtual, (Join-Path $appData 'logs'))) {
	if (-not (Test-Path $d)) { continue }
	foreach ($f in Get-ChildItem $d -Filter 'mp_*desync*.txt' -ErrorAction SilentlyContinue) { Dump $f.FullName 300; $found = $true }
}
if (-not $found) { Line "(no desync files)" }

Section "Game logs folder ($appData\logs)"
$logs = Join-Path $appData 'logs'
if (Test-Path $logs) {
	foreach ($f in Get-ChildItem $logs -File) { Line ("{0,-40} {1,10} bytes  {2:yyyy-MM-dd HH:mm:ss}" -f $f.Name, $f.Length, $f.LastWriteTime) }
	$mod = Join-Path $logs 'modified.log'
	if (Test-Path $mod) {
		$ml = @(Get-Content $mod)
		Line; Line "modified.log: $($ml.Count) changed or extra game files; packs among them:"
		foreach ($l in ($ml | Where-Object { $_ -match '\.pack\b' })) { Line "  $l" }
	}
	foreach ($f in Get-ChildItem $logs -File | Where-Object { $_.Extension -eq '.txt' -and $_.Name -notlike 'mp_*desync*' -and $_.Name -ne 'cef.log.txt' -and $_.Length -lt 200KB }) { Line; Dump $f.FullName 150 }
}
else { Line "(no logs folder)" }

Section "End"
Line "Send this file from EVERY player in the game, and say which one was the host."

[IO.File]::WriteAllText($Out, $sb.ToString(), (New-Object Text.UTF8Encoding $false))
""
"Report written: $Out"
"Attach this one file to your bug report (Discord or GitHub). It is plain text."
try { Start-Process explorer.exe "/select,`"$Out`"" } catch { }
