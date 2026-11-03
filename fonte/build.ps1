# Gera o ícone e compila o Concorde.exe (usa só o que já vem no Windows)
# -Stage exe: só compila o Concorde.exe (o build automático do GitHub assina ele depois)
# -Stage site: não compila, só monta a pasta site com o Concorde.exe que já está lá (assinado)
param([string]$Stage = '')
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$root = Split-Path $src
Add-Type -AssemblyName System.Drawing

# ícone: o avião Concorde visto de cima (branco), num quadrado azul arredondado
# o mesmo desenho está no TelaCall.html (<symbol id="concordeJet">), numa caixa de 100x100 apontando pra cima
$size = 256
$bmp = New-Object System.Drawing.Bitmap $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.Clear([System.Drawing.Color]::Transparent)
function RoundRect($x, $y, $w, $h, $r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddArc($x, $y, $r, $r, 180, 90); $p.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
  $p.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90); $p.AddArc($x, $y + $h - $r, $r, $r, 90, 90)
  $p.CloseFigure(); return $p
}
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($size, $size)),
  ([System.Drawing.Color]::FromArgb(108, 121, 255)), ([System.Drawing.Color]::FromArgb(64, 72, 210))
$g.FillPath($grad, (RoundRect 8 8 240 240 80))
# asa delta "ogiva", bico comprido e os motores (dois de cada lado) aparecendo atrás da asa
$jet = @(
  @('L', 51.8, 9), @('L', 52.6, 20), @('L', 52.8, 32),
  @('C', 53.6, 44, 55.5, 52, 58.5, 60), @('C', 63, 68, 69.5, 76, 71, 84),
  @('L', 71.5, 89), @('L', 61.5, 89), @('L', 61.5, 94), @('L', 54.5, 94), @('L', 54.5, 89), @('L', 53, 89),
  @('L', 52, 99), @('L', 48, 99), @('L', 47, 89),
  @('L', 45.5, 89), @('L', 45.5, 94), @('L', 38.5, 94), @('L', 38.5, 89), @('L', 28.5, 89), @('L', 29, 84),
  @('C', 30.5, 76, 37, 68, 41.5, 60), @('C', 44.5, 52, 46.4, 44, 47.2, 32),
  @('L', 47.4, 20), @('L', 48.2, 9), @('L', 50, 2))
$plane = New-Object System.Drawing.Drawing2D.GraphicsPath
$cx = 50; $cy = 2
foreach ($s in $jet) {
  if ($s[0] -eq 'L') { $plane.AddLine($cx, $cy, $s[1], $s[2]); $cx = $s[1]; $cy = $s[2] }
  else { $plane.AddBezier($cx, $cy, $s[1], $s[2], $s[3], $s[4], $s[5], $s[6]); $cx = $s[5]; $cy = $s[6] }
}
$plane.CloseFigure()
$m = New-Object System.Drawing.Drawing2D.Matrix
$m.Translate(128, 128); $m.Rotate(45); $m.Scale(2.3, 2.3); $m.Translate(-50, -52)   # voando pra cima e pra direita
$plane.Transform($m)
$g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)), $plane)
$g.Dispose()
$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()

# ICO com PNG embutido
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]1)
$w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0)
$w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$png.Length); $w.Write([UInt32]22)
$w.Write($png); $w.Flush()
[System.IO.File]::WriteAllBytes("$src\TelaCall.ico", $ico.ToArray())

# Atualização automática:
# - versão do app: "const APP_VERSION = N;" no TelaCall.html (aumente a cada atualização)
# - versão do .exe: $LauncherVersion abaixo (aumente só quando mudar o Launcher.cs)
# - endereço de onde os apps buscam a versão nova: fonte\update-url.txt (o link do GitHub Pages)
$LauncherVersion = 15
$html = [System.IO.File]::ReadAllText("$root\TelaCall.html")
if ($html -notmatch 'const APP_VERSION = (\d+);') { throw "TelaCall.html sem APP_VERSION" }
$appVersion = [int]$Matches[1]
$updateUrl = ''
if (Test-Path "$src\update-url.txt") { $updateUrl = (Get-Content "$src\update-url.txt" -Raw).Trim() }
if ($updateUrl -and -not $updateUrl.EndsWith('/')) { $updateUrl += '/' }
if ($updateUrl -and $updateUrl -notmatch '^https://') { throw "update-url.txt precisa começar com https://" }
$config = @"
// gerado pelo build.ps1
static class Config
{
    public const string UpdateUrl = "$updateUrl";
    public const int LauncherVersion = $LauncherVersion;
}
"@
[System.IO.File]::WriteAllText("$src\Config.cs", $config, (New-Object System.Text.UTF8Encoding $false))

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$exe = "$root\Concorde.exe"
# um .exe só: é o programa, e com "setup" no nome do arquivo é o instalador (um arquivo pra assinar por versão)
if ($Stage -ne 'site') {
  & $csc /nologo /target:winexe /optimize+ /out:"$exe" /win32icon:"$src\TelaCall.ico" `
    /resource:"$root\TelaCall.html,TelaCall.html" /resource:"$src\TelaCall.ico,Concorde.ico" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:Microsoft.CSharp.dll /r:System.Core.dll `
    "$src\Launcher.cs" "$src\AppAudio.cs" "$src\Activity.cs" "$src\Setup.cs" "$src\Config.cs"
  if ($LASTEXITCODE -ne 0) { throw "falhou a compilação" }
  Write-Host "OK: $exe (app v$appVersion, exe v$LauncherVersion)"
}
if ($Stage -eq 'exe') { return }   # build automático: para aqui, assina o .exe e chama de novo com -Stage site

# instalador = o mesmo arquivo com outro nome
$setup = "$root\Concorde Setup.exe"
Copy-Item $exe $setup -Force
Write-Host "OK: $setup"

# cópia do instalador direto em Documentos, pra achar fácil
$docs = Split-Path $root
try { Copy-Item $setup "$docs\Concorde Setup.exe" -Force -ErrorAction Stop; Write-Host "OK: $docs\Concorde Setup.exe" }
catch { Write-Host "AVISO: não deu pra copiar o instalador pra Documentos" }

# pasta "site": é ela que vai pro GitHub. O link abre o app no navegador
# e os .exe buscam aqui as atualizações (version.json + index.html + o .exe novo)
New-Item -ItemType Directory -Force "$root\site" | Out-Null
Copy-Item "$root\TelaCall.html" "$root\site\index.html" -Force
Copy-Item $exe "$root\site\concorde.exe" -Force
Copy-Item $setup "$root\site\concorde-setup.exe" -Force
$version = "{`"version`": $appVersion, `"launcher`": $LauncherVersion, `"exe`": `"concorde.exe`"}"
[System.IO.File]::WriteAllText("$root\site\version.json", $version, (New-Object System.Text.UTF8Encoding $false))
Write-Host "OK: $root\site (index.html, version.json, concorde.exe, concorde-setup.exe)"
if (-not $updateUrl) { Write-Host "AVISO: fonte\update-url.txt vazio, a atualização automática fica desligada" }
