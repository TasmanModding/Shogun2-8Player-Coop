# Build the 8-player MP campaign lobby layout from the game's original
# ui/frontend ui/mp_grand_campaign (Version054), using lib/LayoutTool.cs.
#
# Changes:
#   - main_panel widened from 1104 to the full 1280 px (frame pieces stretched/shifted to match)
#   - every player panel compacted from 176x260 to 176x134: portrait and flag side by side, name and clan
#     dropdown underneath (same components, only moved; 9-slice backgrounds shortened)
#   - eight panels in two rows where the original single row was:
#         row 1:  P1  P3 | map | P2  P4
#         row 2:  P5  P7 | map | P6  P8
#     The engine fills panels by session slot (panels[slot]); slot 0 is the host = panel_player1.
#   - panel_player3..8 = copies of panel_player2 (+ the frame/invite component behind it) with fresh uids
#   - the copied invite frames are named invite_frame3..8 (the engine patch wires their invite buttons)
#   - title "Eight Player Campaign" instead of "Two Player Campaign"
#   - victory-condition boxes kept for P1 and P2 only (under the left/right blocks); the copies' are hidden
#   - settings / difficulty / ready button re-centred between those two boxes
param(
	[string]$Original = (Join-Path $PSScriptRoot "re\mp_grand_campaign.orig"),
	[string]$Out = (Join-Path $PSScriptRoot "pack_src\ui\frontend ui\mp_grand_campaign")
)
$ErrorActionPreference = 'Stop'
if (-not ('Layout' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot "lib\LayoutTool.cs") }

$root = [Layout]::Read([IO.File]::ReadAllBytes($Original))
$main = [Layout]::FindComponent($root, 'main_panel')
$kids = $main.List('children')
function Child($name) { $c = $kids | Where-Object { $_.Str('name') -eq $name } | Select-Object -First 1; if (-not $c) { throw "no child '$name'" }; $c }
function Sub($node, $name) { $c = $node.List('children') | Where-Object { $_.Str('name') -eq $name } | Select-Object -First 1; if (-not $c) { throw "no '$name' under $($node.Str('name'))" }; $c }
function SetI($node, $field, $value) { $node.Field($field).V = [int]$value }
function SetXY($node, $x, $y) { SetI $node 'x' $x; SetI $node 'y' $y }

# ---- 1. widen main_panel: x 88 -> 0, width 1104 -> 1280 ------------------------------------------
$delta = 176
$oldX = $main.I('x')                     # 88
SetI $main 'x' 0
$state = $main.List('states')[0]
$oldW = $state.I('w')                    # 1104
SetI $state 'w' ($oldW + $delta)
foreach ($si in $state.List('state_images')) {
	$x = $si.I('x'); $w = $si.I('w')
	if ($x -le 16 -and ($x + $w) -ge ($oldW - 16)) { SetI $si 'w' ($w + $delta) }      # fill, top/bottom edges
	elseif ($x -ge ($oldW / 2)) { SetI $si 'x' ($x + $delta) }                        # right-hand corners/edge/fitting
}
foreach ($c in $kids) { SetI $c 'x' ($c.I('x') + $oldX) }   # keep every child at the same screen x

# ---- 2. compact panels -----------------------------------------------------------------------------
$panelH = 134
# shorten a 9-slice background: bottom row moves up, middle pieces shrink
function Shorten($node, $newH) {
	foreach ($st in $node.List('states')) {
		$h = $st.I('h'); $d = $h - $newH; SetI $st 'h' $newH
		foreach ($si in $st.List('state_images')) {
			if ($si.I('y') -ge ($h / 2)) { SetI $si 'y' ($si.I('y') - $d) }
			elseif (($si.I('y') + $si.I('h')) -gt ($h / 2)) { SetI $si 'h' ($si.I('h') - $d) }
		}
	}
}
# rearrange one panel's contents (positions are relative to the panel / to 'player')
function Compact($panel) {
	Shorten $panel $panelH
	SetXY (Sub $panel 'player_slot') 6 6
	$pl = Sub $panel 'player'; SetXY $pl 8 8                   # portrait: panel 8..72, 8..72
	SetXY (Sub $pl 'icon_voicechat') 68 2                      # panel 76..92,  10..26
	SetXY (Sub $pl 'status') 65 34                             # panel 73..95,  42..64 (ready tick)
	SetXY (Sub $pl 'flag') 90 6                                # panel 98..162, 14..78 (clan flag)
	SetXY (Sub $pl 'dy_player_name') -5 74                     # panel 3..173,  82..102
	SetXY (Sub $pl 'faction_dropdown') -4 96                   # panel 4..169,  104..131
	$vc = $panel.List('children') | Where-Object { $_.Str('name') -eq 'victory_conditions' } | Select-Object -First 1
	if ($vc) { SetI $vc 'y' (335 - 65) }                       # box stays at screen y 335 (under the block)
}

# ---- 3. columns and rows (absolute x == relative x now) --------------------------------------------
$colW = 176; $gap = 6
$map = Child 'maps'                                          # 380..900
$colX = @(($map.I('x') - 2 * ($colW + $gap)), ($map.I('x') - ($colW + $gap)), ($map.I('x') + 520 + $gap), ($map.I('x') + 520 + $gap + $colW + $gap))
$rowY = @(65, (65 + $panelH + 2))                            # 65..199, 201..335
# slot -> (column, row): P1 c0r0, P2 c2r0, P3 c1r0, P4 c3r0, P5 c0r1, P6 c2r1, P7 c1r1, P8 c3r1
$pos = @{ 1 = @(0, 0); 2 = @(2, 0); 3 = @(1, 0); 4 = @(3, 0); 5 = @(0, 1); 6 = @(2, 1); 7 = @(1, 1); 8 = @(3, 1) }

$p1 = Child 'panel_player1'; $p2 = Child 'panel_player2'
$p2Frame = $kids | Where-Object { $_.Str('name') -eq 'border' -and $_.I('x') -eq $p2.I('x') -and $_.I('y') -eq $p2.I('y') } | Select-Object -First 1
if (-not $p2Frame) { throw "frame behind panel_player2 not found" }
Compact $p1; Compact $p2; Shorten $p2Frame $panelH
# the frame is the empty-slot look: an empty portrait slot and the invite button (where the flag goes)
SetXY (Sub $p2Frame 'player_slot') 6 6
SetXY (Sub $p2Frame 'button_invite') 98 14
SetXY $p1 $colX[$pos[1][0]] $rowY[$pos[1][1]]
SetXY $p2 $colX[$pos[2][0]] $rowY[$pos[2][1]]; SetXY $p2Frame $p2.I('x') $p2.I('y')

# ---- 4. fresh uids (deterministic, so every player builds byte-identical output) -------------------
$taken = New-Object 'System.Collections.Generic.HashSet[uint32]'
foreach ($u in [Layout]::AllU32($root)) { [void]$taken.Add($u) }
$script:nextUid = [uint32]0x7E4D0001
$alloc = [Func[uint32]] { while ($taken.Contains($script:nextUid)) { $script:nextUid++ }; $u = $script:nextUid; [void]$taken.Add($u); $script:nextUid++; return $u }

# ---- 5. panel_player3..8 = copies of the (already compacted) panel_player2 + its frame ------------
$insertAt = $kids.IndexOf($p2) + 1
foreach ($n in 3..8) {
	$x = $colX[$pos[$n][0]]; $y = $rowY[$pos[$n][1]]
	$frame = [Layout]::DeepClone($p2Frame); [void][Layout]::Reassign($frame, $alloc); SetXY $frame $x $y
	# unique name so the engine patch can find this frame's own invite button and wire it
	$frame.SetStr('name', "invite_frame$n")
	$panel = [Layout]::DeepClone($p2);      [void][Layout]::Reassign($panel, $alloc); SetXY $panel $x $y
	$panel.SetStr('name', "panel_player$n")
	$vc = $panel.List('children') | Where-Object { $_.Str('name') -eq 'victory_conditions' } | Select-Object -First 1
	if ($vc) { $vc.Field('f3').V = [byte]0; SetI $vc 'y' 2000 }   # no room: hide and park off-screen
	$kick = $panel.List('children') | Where-Object { $_.Str('name') -eq 'button_kick' } | Select-Object -First 1
	if ($kick) { $kick.Field('f3').V = [byte]0 }                   # the engine shows it to the host per filled slot
	$kids.Insert($insertAt, $frame); $kids.Insert($insertAt + 1, $panel); $insertAt += 2
}

# ---- 6. re-centre settings / difficulty / ready between the victory boxes of P1 and P2 ------------
$centre = [int](( ($p1.I('x') + $colW) + $p2.I('x') ) / 2)
foreach ($name in 'settings', 'difficulty_settings', 'button_ready') {
	$c = Child $name; $w = $c.List('states')[0].I('w'); SetI $c 'x' ($centre - [int]($w / 2))
}

# ---- 7. title: "Eight Player Campaign" ---------------------------------------------------------------
# The state's localisation key would replace the text with "Two Player Campaign", so clear it and use our own text.
function SetW($node, $field, $text) { $node.Field($field).V = [Text.Encoding]::Unicode.GetBytes($text) }
$heading = Child 'panel_heading'
$hs = $heading.List('states')[0]
SetW $hs 'text' 'Eight Player Campaign'
SetW $hs 'w3' ''
$headW = 320
SetI $heading 'x' ($heading.I('x') + [int]($hs.I('w') / 2) - [int]($headW / 2))   # keep it centred
SetI $hs 'w' $headW
SetW $heading 'w1' 'Up to 8 players: play against each other, or team up against the AI.'
SetW $heading 'w2' ''

# ---- 8. write and verify ---------------------------------------------------------------------------
$bytes = [Layout]::Write($root)
$check = [Layout]::Read($bytes)
$all = @(); foreach ($c in [Layout]::Components($check)) { $all += $c.U('uid'); foreach ($s in $c.List('states')) { $all += $s.U('uid') }; foreach ($i in $c.List('images')) { $all += $i.U('uid') } }
$dups = $all | Group-Object | Where-Object Count -gt 1
if ($dups) { throw "duplicate uids after build: $(($dups | ForEach-Object { '{0:X8}' -f [uint32]$_.Name }) -join ', ')" }
foreach ($n in 1..8) { if (-not [Layout]::FindComponent($check, "panel_player$n")) { throw "panel_player$n missing" } }

New-Item -ItemType Directory -Force (Split-Path $Out -Parent) | Out-Null
[IO.File]::WriteAllBytes($Out, $bytes)
"wrote {0} ({1:N0} bytes, original {2:N0}); {3} uids, all unique; columns {4}; rows {5}; settings centre {6}" -f `
	$Out, $bytes.Length, (Get-Item $Original).Length, $all.Count, ($colX -join ','), ($rowY -join ','), $centre
