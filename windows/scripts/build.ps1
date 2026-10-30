param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$windowsRoot = Split-Path $PSScriptRoot -Parent
$repoRoot = Split-Path $windowsRoot -Parent
$localDotnet = Join-Path $windowsRoot '.tools/dotnet/dotnet.exe'
if ($Dotnet -eq 'dotnet' -and (Test-Path -LiteralPath $localDotnet)) { $Dotnet = $localDotnet }
$revision = git -C $repoRoot rev-parse --short=12 HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine Git revision' }
if (git -C $repoRoot status --porcelain --untracked-files=normal) { $revision += '-dirty' }
Push-Location $windowsRoot
try {
    & $Dotnet build src/Plantain.Windows -c Release "-p:SourceRevisionId=$revision" -o artifacts/app
    if ($LASTEXITCODE -ne 0) { throw 'Windows build failed' }
    Write-Output (Join-Path $windowsRoot 'artifacts/app/Plantain.Windows.exe')
} finally { Pop-Location }
