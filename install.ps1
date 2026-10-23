# 8-player MP campaign co-op mod for Total War: Shogun 2 (experimental) - installer / uninstaller.
# Builds on the 4-player mod: if that mod is installed it is removed first (its DLL backup is the original).
#
# Works entirely from THIS player's own installed game: it extracts the lobby layout from data\data.pack,
# builds the 8-player version, writes data\eightplayer_coop.pack (a movie pack: the game loads it by itself), and
# patches empire.retail.dll after backing it up. Every player must install it (identical output on
# identical game versions), or the game will refuse to connect them.
#
#   powershell -ExecutionPolicy Bypass -File install.ps1              # install
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall   # restore everything
param(
	[string]$GamePath = "",
	[string]$UserScript = (Join-Path $env:APPDATA "The Creative Assembly\Shogun2\scripts\user.script.txt"),
	[switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$lib = Join-Path $PSScriptRoot 'lib'
foreach ($t in @(@('Layout', 'LayoutTool'), @('Pack', 'PackTools'), @('PackWriter', 'PackWriter'), @('EightPlayerPatcher', 'Patcher'))) {
	if (-not ($t[0] -as [type])) { Add-Type -Path (Join-Path $lib "$($t[1]).cs") }
}

# find the game in any Steam library if no path was given
if (-not $GamePath) {
	$roots = @()
	foreach ($k in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
		try { $p = (Get-ItemProperty $k -ErrorAction Stop); if ($p.SteamPath) { $roots += $p.SteamPath }; if ($p.InstallPath) { $roots += $p.InstallPath } } catch { }
	}
	$roots += "C:\Program Files (x86)\Steam"
	$libs = @()
	foreach ($r in ($roots | Select-Object -Unique)) {
		$libs += $r
		$vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
		if (Test-Path $vdf) { foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs += $m.Groups[1].Value.Replace('\\', '\') } }
	}
	foreach ($l in ($libs | Select-Object -Unique)) {
		$c = Join-Path $l 'steamapps\common\Total War Shogun 2'
		if (Test-Path (Join-Path $c 'empire.retail.dll')) { $GamePath = $c; break }
	}
	if (-not $GamePath) { throw "Couldn't find Total War: Shogun 2. Run with -GamePath ""<your game folder>""." }
}
"Game folder: $GamePath"

$dll = Join-Path $GamePath 'empire.retail.dll'
$backup = "$dll.8p-original"
$backup4p = "$dll.4p-original"
$packName = 'eightplayer_coop.pack'
$packPath = Join-Path $GamePath "data\$packName"
$modLine = "mod `"$packName`";"
$pack4p = Join-Path $GamePath 'data\fourplayer_campaign.pack'
$modLine4p = 'mod "fourplayer_campaign.pack";'
$layoutName = 'ui\frontend ui\mp_grand_campaign'
$orig = [EightPlayerPatcher]::OriginalSha256

function Sha($p) { (Get-FileHash $p -Algorithm SHA256).Hash }
function ReadScriptLines {
	if (-not (Test-Path $UserScript)) { return @() }
	return @(Get-Content $UserScript)          # Get-Content detects the BOM (the game uses UTF-16 LE)
}
function WriteScriptLines($lines) {
	New-Item -ItemType Directory -Force (Split-Path $UserScript -Parent) | Out-Null
	Set-Content -Path $UserScript -Value $lines -Encoding Unicode   # UTF-16 LE with BOM, as the game expects
}
function RemoveModLine($line) {
	$lines = ReadScriptLines
	if ($lines -contains $line) { WriteScriptLines (@($lines | Where-Object { $_ -ne $line })); "Removed '$line' from user.script.txt." }
}

if (Get-Process -Name 'shogun2*' -ErrorAction SilentlyContinue) { throw "Close Shogun 2 before running this." }
if (-not (Test-Path $dll)) { throw "empire.retail.dll not found in '$GamePath' (pass -GamePath)." }

if ($Uninstall) {
	if (Test-Path $backup) {
		if ((Sha $backup) -ne $orig) { throw "Backup doesn't match the original game DLL; not restoring. Use Steam 'Verify integrity' instead." }
		Copy-Item $backup $dll -Force
		Remove-Item $backup
		"Restored the original empire.retail.dll."
	}
	else { "No backup found; empire.retail.dll left as it is." }
	if (Test-Path $packPath) { Remove-Item $packPath; "Removed data\$packName." }
	RemoveModLine $modLine
	"Uninstalled."
	return
}

# ---- 0. remove the 4-player mod if present (this mod replaces it) ----------------------------------
if (Test-Path $backup4p) {
	if ((Sha $backup4p) -ne $orig) { throw "Found the 4-player mod's DLL backup but it isn't the original game DLL. Use Steam 'Verify integrity', then run this again." }
	if (-not (Test-Path $backup)) { Copy-Item $backup4p $backup }
	Copy-Item $backup4p $dll -Force
	Remove-Item $backup4p
	"Removed the 4-player mod's engine patch (restored the original DLL)."
}
if (Test-Path $pack4p) { Remove-Item $pack4p; "Removed data\fourplayer_campaign.pack." }
RemoveModLine $modLine4p

# ---- 1. engine DLL --------------------------------------------------------------------------------
$tmp = "$dll.8p-tmp"
$current = Sha $dll
if ($current -eq $orig) {
	if (-not (Test-Path $backup)) { Copy-Item $dll $backup }
	[EightPlayerPatcher]::Build($backup, $tmp) | Out-Host
	Move-Item $tmp $dll -Force
	"Patched empire.retail.dll (original saved as $(Split-Path $backup -Leaf))."
}
elseif ((Test-Path $backup) -and (Sha $backup) -eq $orig) {
	[EightPlayerPatcher]::Build($backup, $tmp) | Out-Null
	if ($current -eq (Sha $tmp)) { Remove-Item $tmp; "empire.retail.dll is already patched." }
	else {
		# an older build of this mod: rebuild from the original backup
		Move-Item $tmp $dll -Force
		"Updated empire.retail.dll to this version of the mod."
	}
}
else { throw "empire.retail.dll is not the version this mod was made for (sha256 $current). Not touching it." }
"empire.retail.dll sha256 $(Sha $dll). All players should see the same hash."

# ---- 2. lobby layout -> mod pack ------------------------------------------------------------------
$dataPack = Join-Path $GamePath 'data\data.pack'
$entry = [Pack]::Index($dataPack) | Where-Object { $_.Name -eq $layoutName } | Select-Object -First 1
if (-not $entry) { throw "'$layoutName' not found in data.pack" }
$work = Join-Path ([IO.Path]::GetTempPath()) ("s2_8p_" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
	$inFile = Join-Path $work 'original_layout'; $outFile = Join-Path $work 'layout_8p'
	[IO.File]::WriteAllBytes($inFile, [Pack]::Read($dataPack, $entry))
	& (Join-Path $PSScriptRoot 'build_lobby_layout.ps1') -Original $inFile -Out $outFile | Out-Host
	$list = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,byte[]]]'
	$list.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,byte[]]' $layoutName, ([IO.File]::ReadAllBytes($outFile))))
	# the Load Game screen with a row for every player
	$loadName = 'ui\frontend ui\mp_load_game'
	$loadEntry = [Pack]::Index($dataPack) | Where-Object { $_.Name -eq $loadName } | Select-Object -First 1
	if (-not $loadEntry) { throw "'$loadName' not found in data.pack" }
	$loadIn = Join-Path $work 'original_load'; $loadOut = Join-Path $work 'load_8p'
	[IO.File]::WriteAllBytes($loadIn, [Pack]::Read($dataPack, $loadEntry))
	& (Join-Path $PSScriptRoot 'build_load_layout.ps1') -Original $loadIn -Out $loadOut | Out-Host
	$list.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,byte[]]' $loadName, ([IO.File]::ReadAllBytes($loadOut))))
	[PackWriter]::Write($packPath, $list)
	"Wrote data\$packName (sha256 $(Sha $packPath)). All players should see the same hash."
}
finally {
	$resolvedWork = [IO.Path]::GetFullPath($work)
	$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
	if ((Split-Path $resolvedWork -Parent) -eq $tempRoot -and (Split-Path $resolvedWork -Leaf) -match '^s2_8p_[0-9a-f]{32}$') {
		Remove-Item -LiteralPath $resolvedWork -Recurse -Force -ErrorAction SilentlyContinue
	}
}

# ---- 3. the pack is a movie pack: the game loads it by itself --------------------------------------
RemoveModLine $modLine          # left over from older versions of this mod (mod-type pack)

# ---- 4. other mods: different data on different PCs is a classic cause of desyncs ----------------------
# One short code from this mod's DLL and pack plus every other enabled mod (name and file hash): players with
# the same code run the same game data.
$others = @(ReadScriptLines | Where-Object { $_ -match '^\s*mod\s+"([^"]+)"' } | ForEach-Object { ([regex]::Match($_, '"([^"]+)"')).Groups[1].Value } | Where-Object { $_ -ne 'eightplayer_coop_lobby.pack' })   # this mod's Workshop item
# drag-and-drop mods: movie packs (type 4) in data\ other than the game's own movies*.pack and this mod's
foreach ($f in Get-ChildItem (Join-Path $GamePath 'data') -Filter *.pack) {
	if ($f.Name -like 'movies*.pack' -or $f.Name -eq $packName -or $f.Name -eq 'eightplayer_coop_lobby.pack' -or $others -contains $f.Name) { continue }
	$h = New-Object byte[] 8
	$fs = [IO.File]::OpenRead($f.FullName); try { [void]$fs.Read($h, 0, 8) } finally { $fs.Close() }
	if ([BitConverter]::ToUInt32($h, 4) -eq 4) { $others += $f.Name }
}
$parts = @((Sha $dll), (Sha $packPath))
foreach ($o in ($others | Sort-Object)) {
	$p = Join-Path $GamePath "data\$o"
	$parts += $o.ToLowerInvariant() + '=' + $(if (Test-Path $p) { Sha $p } else { 'missing' })
}
$code = (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($parts -join '|'))) -Algorithm SHA256).Hash.Substring(0, 8)
""
if ($others.Count) {
	Write-Warning "Other mods are enabled: $($others -join ', ')"
	Write-Warning "Every player must have exactly the same ones, or the game will desync. Turn them off (Mod Manager / Workshop) if unsure."
}
else { "No other mods enabled (good: fewer chances of a desync)." }
"MATCH CODE: $code   <- compare this with your friends. Same code = same game files."

""
"Installed. Reminders:"
"  - Every player needs this mod installed (identical files), or they can't join each other."
"  - EXPERIMENTAL. Use spare Steam accounts: Shogun 2 multiplayer is VAC-protected and this modifies the engine DLL."
"  - Undo at any time:  powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall"
