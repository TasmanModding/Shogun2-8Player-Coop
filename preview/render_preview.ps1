# Draws the visible components of a lobby layout as labelled boxes (no game artwork) for a quick check.
param([string]$LayoutFile, [string]$Png)
Add-Type -AssemblyName System.Drawing
if (-not ('Layout' -as [type])) { Add-Type -Path (Join-Path (Split-Path $PSScriptRoot -Parent) "lib\LayoutTool.cs") }
$root = [Layout]::Read([IO.File]::ReadAllBytes($LayoutFile))
$main = [Layout]::FindComponent($root, 'main_panel')
$bmp = New-Object Drawing.Bitmap 1280, 720; $g = [Drawing.Graphics]::FromImage($bmp); $g.Clear([Drawing.Color]::FromArgb(30, 28, 26))
$font = New-Object Drawing.Font 'Segoe UI', 8
$colors = @{ panel = 'Goldenrod'; maps = 'SteelBlue'; settings = 'SeaGreen'; victory_conditions = 'IndianRed' }
function D($n, $ax, $ay, $depth) {
	if ($depth -ge 2 -and $n.Field("f3") -and $n.Field("f3").V -eq 0) { return }
	$x = $ax + $n.I('x'); $y = $ay + $n.I('y'); $s = $n.List('states')
	$name = $n.Str('name')
	if ($s.Count -and $depth -le 3 -and $name -notmatch '^(border|border_1px|hr|row_|popup|TextView|Text|vslider)') {
		$w = $s[0].I('w'); $h = $s[0].I('h')
		$key = if ($name -like 'panel_player*') { 'panel' } else { $name }
		$c = if ($colors.ContainsKey($key)) { [Drawing.Color]::FromName($colors[$key]) } else { [Drawing.Color]::FromArgb(150, 150, 150) }
		$g.DrawRectangle((New-Object Drawing.Pen $c), $x, $y, $w, $h)
		if ($depth -le 2) { $g.DrawString($name, $font, (New-Object Drawing.SolidBrush $c), $x + 2, $y + 1) }
	}
	foreach ($ch in $n.List('children')) { if ($ch.Str('name') -ne 'popup_menu') { D $ch $x $y ($depth + 1) } }
}
D $main 0 0 0
$bmp.Save($Png, [Drawing.Imaging.ImageFormat]::Png); "preview: $Png"
