param([string]$Package, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
if (!$Package) { $Package = Join-Path (Split-Path $PSScriptRoot -Parent) '.tools/ffmpeg' }
$Package = (Resolve-Path -LiteralPath $Package).Path
$files = 'ffmpeg.exe', 'COPYING.LGPLv2.1', 'BUILD.txt', 'ffmpeg-9.1.tar.xz'
foreach ($file in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $Package $file) -PathType Leaf)) { throw "Incomplete bundled FFmpeg: $file" }
}
$component = New-Item -ItemType Directory -Force -Path (Join-Path $Destination 'ffmpeg')
foreach ($file in $files) {
    Copy-Item -LiteralPath (Join-Path $Package $file) -Destination $component.FullName
}
