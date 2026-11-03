# Gera o ícone e compila o Concorde.exe (usa só o que já vem no Windows)
# -Stage exe: só compila o Concorde.exe (o build automático do GitHub assina ele depois)
# -Stage site: não compila, só monta a pasta site com o Concorde.exe que já está lá (assinado)
param([string]$Stage = '')
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$root = Split-Path $src
Add-Type -AssemblyName System.Drawing

# ícone: quadrado azul arredondado com um monitor branco
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
$g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(88, 101, 242))), (RoundRect 8 8 240 240 80))
$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$g.FillPath($white, (RoundRect 48 64 160 108 24))
$g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(88, 101, 242))), (RoundRect 62 78 132 80 12))
$g.FillRectangle($white, 116, 172, 24, 22)
$g.FillPath($white, (RoundRect 84 190 88 18 12))
# "onda" de voz dentro da tela
$g.FillEllipse($white, 108, 98, 40, 40)
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
