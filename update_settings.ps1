$path = Join-Path $env:APPDATA "V-Notch\settings.json"
$json = Get-Content $path | ConvertFrom-Json

$json.LiquidGlassCustom.BlurAmount = 0.05
$json.LiquidGlassCustom.Refraction = 0
$json.LiquidGlassCustom.Distortion = 0
$json.LiquidGlassCustom.Specular = 0
$json.LiquidGlassCustom.EdgeHighlight = 0
$json.LiquidGlassCustom.Variant = 1
$json.LiquidGlassCustom.Opacity = 1

$json.LiquidGlass.BlurAmount = 0.05
$json.LiquidGlass.Refraction = 0
$json.LiquidGlass.Distortion = 0
$json.LiquidGlass.Specular = 0
$json.LiquidGlass.EdgeHighlight = 0
$json.LiquidGlass.Variant = 1
$json.LiquidGlass.Opacity = 1

$json.EnableSpotifyCanvas = $false
$json.ShowMediaArtBackground = $false
$json | ConvertTo-Json -Depth 10 | Set-Content $path
