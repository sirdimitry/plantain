param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$windowsRoot = Split-Path $PSScriptRoot -Parent
$repoRoot = Split-Path $windowsRoot -Parent
$localDotnet = Join-Path $windowsRoot '.tools/dotnet/dotnet.exe'
if ($Dotnet -eq 'dotnet' -and (Test-Path -LiteralPath $localDotnet)) { $Dotnet = $localDotnet }
$version = (Get-Content (Join-Path $windowsRoot 'VERSION') -Raw).Trim()
$revision = git -C $repoRoot rev-parse --short=12 HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine Git revision' }
if (git -C $repoRoot status --porcelain --untracked-files=normal) { $revision += '-dirty' }
$name = "Plantain-$version-win-x64"
$folder = Join-Path $windowsRoot "artifacts/portable/$revision/$name"
$packageRoot = Join-Path $windowsRoot "artifacts/packages/$revision"
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Push-Location $windowsRoot
try {
    & $Dotnet publish src/Plantain.Windows -c Release -r win-x64 --self-contained true "-p:SourceRevisionId=$revision" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $folder
    if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed' }
    foreach ($required in @('Plantain.Windows.exe')) {
        if (!(Test-Path -LiteralPath (Join-Path $folder $required))) { throw "Missing portable component: $required" }
    }
    Copy-Item -LiteralPath (Join-Path $windowsRoot 'docs/PORTABLE.ru.txt') -Destination (Join-Path $folder 'Прочитайте меня.txt')
    $archive = Join-Path $packageRoot "$name.zip"
    if (Test-Path -LiteralPath $archive) { throw 'Archive exists; preserve it or use a new commit before packaging again' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($folder, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($archive + '.sha256', "$hash  $name.zip`n", [Text.UTF8Encoding]::new($false))
    Write-Output "Portable: $folder"
    Write-Output "ZIP: $archive"
    Write-Output "SHA256: $hash"
} finally { Pop-Location }
