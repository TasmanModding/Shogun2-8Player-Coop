# Build the multiplayer Load Game screen for up to 8 players from the game's original
# ui/frontend ui/mp_load_game (Version054), using lib/LayoutTool.cs.
#
# The original shows two big portraits (you, and one other player) either side of the map. Here every player
# gets a compact row: portrait 50x50, clan name and player name beside it, small clan symbol in the corner.
#         left column:  P1  P3  P5  P7        right column:  P2  P4  P6  P8
# portrait_player3..8 are copies (fresh uids), hidden until the engine patch fills them from the save.
param(
	[string]$Original = (Join-Path $PSScriptRoot "re\mp_load_game.orig"),
	[string]$Out = (Join-Path $PSScriptRoot "pack_src\ui\frontend ui\mp_load_game")
)
$ErrorActionPreference = 'Stop'
if (-not ('Layout' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot "lib\LayoutTool.cs") }

$root = [Layout]::Read([IO.File]::ReadAllBytes($Original))
$info = [Layout]::FindComponent($root, 'info_section')
$kids = $info.List('children')
function Child($name) { $c = $kids | Where-Object { $_.Str('name') -eq $name } | Select-Object -First 1; if (-not $c) { throw "no child '$name'" }; $c }
function Sub($node, $name) { $c = $node.List('children') | Where-Object { $_.Str('name') -eq $name } | Select-Object -First 1; if (-not $c) { throw "no '$name' under $($node.Str('name'))" }; $c }
function SetI($node, $field, $value) { $node.Field($field).V = [int]$value }
function SetXY($node, $x, $y) { SetI $node 'x' $x; SetI $node 'y' $y }

$rowH = 50; $rowGap = 56; $top = 23
# scale every state image of a component from (oldW x oldH) to (w x h)
function Rescale($node, $w, $h) {
	foreach ($st in $node.List('states')) {
		$ow = $st.I('w'); $oh = $st.I('h'); SetI $st 'w' $w; SetI $st 'h' $h
		foreach ($img in $st.List('state_images')) {
			SetI $img 'x' ([int]($img.I('x') * $w / $ow)); SetI $img 'y' ([int]($img.I('y') * $h / $oh))
			SetI $img 'w' ([Math]::Max(1, [int]($img.I('w') * $w / $ow))); SetI $img 'h' ([Math]::Max(1, [int]($img.I('h') * $h / $oh)))
		}
	}
}
function Compact($p) {
	foreach ($st in $p.List('states')) {
		SetI $st 'w' 132; SetI $st 'h' $rowH
		foreach ($img in $st.List('state_images')) { SetXY $img 0 0; SetI $img 'w' $rowH; SetI $img 'h' $rowH }   # portrait 50x50
	}
	$b = Sub $p 'border'; SetXY $b 0 0
	foreach ($st in $b.List('states')) {   # frame: keep 3 px edges, fitted to 50x50
		SetI $st 'w' $rowH; SetI $st 'h' $rowH
		foreach ($img in $st.List('state_images')) {
			$x = $img.I('x'); $y = $img.I('y'); $w = $img.I('w'); $h = $img.I('h')
			if ($x -ge 64) { SetI $img 'x' ($rowH - 3) }; if ($y -ge 64) { SetI $img 'y' ($rowH - 3) }
			if ($w -gt 3) { SetI $img 'w' ($rowH - 6) }; if ($h -gt 3) { SetI $img 'h' ($rowH - 6) }
		}
	}
	$mon = Sub $p 'dy_mon'; SetXY $mon 32 32; Rescale $mon 18 18
	$clan = Sub $p 'dy_clan_name'; SetXY $clan 54 4; foreach ($st in $clan.List('states')) { SetI $st 'w' 78 }
	$name = Sub $p 'player_name'; SetXY $name 54 24; foreach ($st in $name.List('states')) { SetI $st 'w' 78 }
}

$p1 = Child 'portrait_player1'; $p2 = Child 'portrait_player2'
$leftX = $p1.I('x'); $rightX = $p2.I('x')
Compact $p1; Compact $p2
SetXY $p1 $leftX $top; SetXY $p2 $rightX $top

$taken = New-Object 'System.Collections.Generic.HashSet[uint32]'
foreach ($u in [Layout]::AllU32($root)) { [void]$taken.Add($u) }
$script:nextUid = [uint32]0x7E4E0001
$alloc = [Func[uint32]] { while ($taken.Contains($script:nextUid)) { $script:nextUid++ }; $u = $script:nextUid; [void]$taken.Add($u); $script:nextUid++; return $u }

$insertAt = $kids.IndexOf($p2) + 1
foreach ($n in 3..8) {
	$src = if ($n % 2) { $p1 } else { $p2 }
	$c = [Layout]::DeepClone($src); [void][Layout]::Reassign($c, $alloc)
	$c.SetStr('name', "portrait_player$n")
	SetXY $c $src.I('x') ($top + [Math]::Floor(($n - 1) / 2) * $rowGap)
	$c.Field('f3').V = [byte]0                       # hidden until filled
	$kids.Insert($insertAt, $c); $insertAt++
}

# the two background boxes behind the portraits (main_panel/portrait_bg and its child copy): one row -> four rows
$bg = [Layout]::FindComponent($root, 'portrait_bg')
foreach ($b in @($bg) + @($bg.List('children') | Where-Object { $_.Str('name') -eq 'portrait_bg' })) { Rescale $b 132 (3 * $rowGap + $rowH + 2) }

$bytes = [Layout]::Write($root)
$check = [Layout]::Read($bytes)
$all = @(); foreach ($c in [Layout]::Components($check)) { $all += $c.U('uid'); foreach ($s in $c.List('states')) { $all += $s.U('uid') }; foreach ($i in $c.List('images')) { $all += $i.U('uid') } }
$dups = $all | Group-Object | Where-Object Count -gt 1
if ($dups) { throw "duplicate uids after build: $(($dups | ForEach-Object { '{0:X8}' -f [uint32]$_.Name }) -join ', ')" }
foreach ($n in 1..8) { if (-not [Layout]::FindComponent($check, "portrait_player$n")) { throw "portrait_player$n missing" } }
New-Item -ItemType Directory -Force (Split-Path $Out -Parent) | Out-Null
[IO.File]::WriteAllBytes($Out, $bytes)
"wrote {0} ({1:N0} bytes, original {2:N0}); {3} uids, all unique" -f $Out, $bytes.Length, (Get-Item $Original).Length, $all.Count
