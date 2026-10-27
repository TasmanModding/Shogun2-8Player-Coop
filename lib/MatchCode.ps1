# MATCH CODE: one short code from this mod's DLL and pack plus every other enabled mod (name and file hash).
# Players with the same code run the same game data. Used by install.ps1 and report.ps1.
function Get-MatchInfo([string]$GamePath, [string]$UserScript) {
	$dll = Join-Path $GamePath 'empire.retail.dll'
	$pack = Join-Path $GamePath 'data\eightplayer_coop.pack'
	$lines = @(); if (Test-Path $UserScript) { $lines = @(Get-Content $UserScript) }
	$others = @($lines | Where-Object { $_ -match '^\s*mod\s+"([^"]+)"' } | ForEach-Object { ([regex]::Match($_, '"([^"]+)"')).Groups[1].Value } | Where-Object { $_ -ne 'eightplayer_coop_lobby.pack' })   # this mod's Workshop item
	# drag-and-drop mods: movie packs (type 4) in data\ other than the game's own movies*.pack and this mod's
	foreach ($f in Get-ChildItem (Join-Path $GamePath 'data') -Filter *.pack) {
		if ($f.Name -like 'movies*.pack' -or $f.Name -eq 'eightplayer_coop.pack' -or $f.Name -eq 'eightplayer_coop_lobby.pack' -or $others -contains $f.Name) { continue }
		$h = New-Object byte[] 8
		$fs = [IO.File]::OpenRead($f.FullName); try { [void]$fs.Read($h, 0, 8) } finally { $fs.Close() }
		if ([BitConverter]::ToUInt32($h, 4) -eq 4) { $others += $f.Name }
	}
	$sha = { param($p) if (Test-Path $p) { (Get-FileHash $p -Algorithm SHA256).Hash } else { 'missing' } }
	$parts = @((& $sha $dll), (& $sha $pack))
	foreach ($o in ($others | Sort-Object)) { $parts += $o.ToLowerInvariant() + '=' + (& $sha (Join-Path $GamePath "data\$o")) }
	$code = (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($parts -join '|'))) -Algorithm SHA256).Hash.Substring(0, 8)
	New-Object PSObject -Property @{ Code = $code; Others = $others; DllSha = $parts[0]; PackSha = $parts[1] }
}

# the game folder from the Steam libraries (first one that has Shogun 2), or "" if none
function Find-ShogunFolder {
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
		if (Test-Path (Join-Path $c 'empire.retail.dll')) { return $c }
	}
	return ""
}
