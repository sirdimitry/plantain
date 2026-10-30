param([string]$Dotnet = 'dotnet', [string]$Iscc = '', [switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$windowsRoot = Split-Path $PSScriptRoot -Parent
$repoRoot = Split-Path $windowsRoot -Parent
$version = (Get-Content (Join-Path $windowsRoot 'VERSION') -Raw).Trim()
if ($version -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-dev\.(\d+))?$') { throw 'Unsupported installer version format' }
$sequence = if ($Matches[4]) { [int]$Matches[4] } else { 65535 }
$numeric = "$($Matches[1]).$($Matches[2]).$($Matches[3]).$sequence"
foreach ($part in $numeric.Split('.')) { if ([int]$part -gt 65535) { throw 'Version part exceeds Windows version limits' } }
$revision = git -C $repoRoot rev-parse --short=12 HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine Git revision' }
if (git -C $repoRoot status --porcelain --untracked-files=normal) { $revision += '-dirty' }
$localDotnet = Join-Path $windowsRoot '.tools/dotnet/dotnet.exe'
if ($Dotnet -eq 'dotnet' -and (Test-Path -LiteralPath $localDotnet)) { $Dotnet = $localDotnet }
if (!$Iscc) {
    $bundled = Join-Path $windowsRoot '.tools/inno-7.1.1/ISCC.exe'
    if (Test-Path -LiteralPath $bundled) { $Iscc = $bundled }
    else { $Iscc = (Get-Command ISCC.exe -ErrorAction Stop).Source }
}
$payload = Join-Path $windowsRoot "artifacts/installer-payload/$revision/win-x64"
$packages = Join-Path $windowsRoot "artifacts/packages/$revision"
$name = "Plantain-$version-win-x64-Setup.exe"
$setup = Join-Path $packages $name
if (Test-Path -LiteralPath $setup) { throw 'Installer exists; preserve it or package a new commit' }
New-Item -ItemType Directory -Path $packages -Force | Out-Null
Push-Location $windowsRoot
try {
    if (!$SkipPublish) {
        & $Dotnet publish src/Plantain.Windows -c Release -r win-x64 --self-contained true "-p:SourceRevisionId=$revision" -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $payload
        if ($LASTEXITCODE -ne 0) { throw 'Installer payload publish failed' }
    }
    foreach ($required in @('Plantain.Windows.exe', 'coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Missing installer runtime component: $required" }
    }
    & $Iscc /Q "/DAppVersion=$version" "/DNumericVersion=$numeric" "/DPayloadDir=$payload" "/DPackageDir=$packages" (Join-Path $windowsRoot 'installer/Plantain.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
    $hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($setup + '.sha256', "$hash  $name`n", [Text.UTF8Encoding]::new($false))
    [ordered]@{ version = $version; revision = $revision; architecture = 'x64'; minimumWindowsBuild = 14393; sha256 = $hash; payloadFiles = @(Get-ChildItem -LiteralPath $payload -File -Recurse).Count } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packages 'installer-build.json') -Encoding utf8
    Write-Output "Installer: $setup"
    Write-Output "SHA256: $hash"
} finally { Pop-Location }
