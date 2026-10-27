# Gathers everything a bug report needs into ONE plain-text file on the Desktop (Shogun2-8Player-Report.txt):
# mod version and hashes, MATCH CODE, other mods, the mod's event log, crash and error files, the game's desync
# files and the logs folder. Text only, so it can be attached anywhere (Discord, GitHub, AI assistants).
# Reads files only; changes nothing in the game.
#   powershell -ExecutionPolicy Bypass -File report.ps1 [-GamePath "D:\...\Total War SHOGUN 2"]
param(
	[string]$GamePath = "",
	[string]$Out = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Shogun2-8Player-Report.txt')
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
# a text file, or its last $tail lines
function Dump([string]$path, [int]$tail = 0) {
	if (-not (Test-Path $path)) { Line "(not found: $path)"; return }
	$f = Get-Item $path
	Line ("--- {0}  ({1} bytes, changed {2:yyyy-MM-dd HH:mm:ss})" -f $f.FullName, $f.Length, $f.LastWriteTime)
	$lines = @(Get-Content $path)
	if ($tail -gt 0 -and $lines.Count -gt $tail) { Line "(last $tail of $($lines.Count) lines)"; $lines = $lines[($lines.Count - $tail)..($lines.Count - 1)] }
	foreach ($l in $lines) { Line $l }
}
# files the game wrote into its own folder may have been redirected by Windows when the game is in Program Files
$virtual = Join-Path $env:LOCALAPPDATA ('VirtualStore\' + ($GamePath -replace '^[A-Za-z]:\\', ''))
function GameFile([string]$name) {
	foreach ($d in @($GamePath, $virtual)) { $p = Join-Path $d $name; if (Test-Path $p) { return $p } }
	return (Join-Path $GamePath $name)
}

$version = '?'
$m = Select-String -Path (Join-Path $lib 'Patcher.cs') -Pattern 'public const string Version = "([^"]+)"' | Select-Object -First 1
if ($m) { $version = $m.Matches[0].Groups[1].Value }
$match = Get-MatchInfo $GamePath $userScript

Line "Shogun 2 8-player co-op mod: bug report"
Line ("Written {0:yyyy-MM-dd HH:mm:ss} by report.ps1 of mod v{1}" -f (Get-Date), $version)
Section "Setup"
Line "Game folder      : $GamePath"
Line "MATCH CODE       : $($match.Code)   (every player must have the same code)"
Line "empire.retail.dll: $($match.DllSha)"
Line "eightplayer pack : $($match.PackSha)"
$orig = Join-Path $GamePath 'empire.retail.dll.8p-original'
Line ("Original backup  : {0}" -f $(if (Test-Path $orig) { (Get-FileHash $orig -Algorithm SHA256).Hash } else { 'none (mod not installed?)' }))
Line ("Other mods       : {0}" -f $(if ($match.Others.Count) { $match.Others -join ', ' } else { 'none' }))
try { Line ("Windows          : {0}" -f (Get-CimInstance Win32_OperatingSystem).Caption) } catch { }
try { Line ("CPU              : {0}" -f ((Get-CimInstance Win32_Processor | Select-Object -First 1).Name).Trim()) } catch { }

Section "Mod event log (eightplayer_log.txt): what happened in the game, newest at the bottom"
Dump (GameFile 'eightplayer_log.txt') 600
Section "Crash report (eightplayer_crash.txt)"
Dump (GameFile 'eightplayer_crash.txt')
$dmp = GameFile 'eightplayer_crash.dmp'
if (Test-Path $dmp) { Line ("(a crash dump also exists: {0}, {1} bytes; only needed if asked for)" -f $dmp, (Get-Item $dmp).Length) }
Section "Engine errors before an instant close (eightplayer_errors.txt)"
Dump (GameFile 'eightplayer_errors.txt') 200

Section "Desync reports"
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
	foreach ($f in Get-ChildItem $logs -File | Where-Object { $_.Extension -eq '.txt' -and $_.Name -notlike 'mp_*desync*' -and $_.Length -lt 200KB }) { Line; Dump $f.FullName 150 }
}
else { Line "(no logs folder)" }

[IO.File]::WriteAllText($Out, $sb.ToString(), (New-Object Text.UTF8Encoding $false))
""
"Report written: $Out"
"Attach this one file to your bug report (Discord or GitHub). It is plain text."
try { Start-Process explorer.exe "/select,`"$Out`"" } catch { }
