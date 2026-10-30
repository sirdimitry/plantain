param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path $PSScriptRoot -Parent
$repoRoot = Split-Path $windowsRoot -Parent
$version = (Get-Content (Join-Path $windowsRoot 'VERSION') -Raw).Trim()
$revision = git -C $repoRoot rev-parse --short=12 HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine revision' }
if (git -C $repoRoot status --porcelain --untracked-files=normal) { throw 'Release requires a clean working tree' }
$name = "Plantain-$version-win-x64.zip"
$zip = Join-Path $windowsRoot "artifacts/packages/$revision/$name"
$sha = $zip + '.sha256'
if (!(Test-Path -LiteralPath $zip) -or !(Test-Path -LiteralPath $sha)) { throw 'Run publish.ps1 for this commit first' }
$expected = ((Get-Content -LiteralPath $sha -Raw).Split(' ')[0]).Trim()
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $expected) { throw 'Archive checksum mismatch' }
$tag = "windows-v$version"
$notes = Join-Path $windowsRoot "artifacts/packages/$revision/release-notes.md"
$body = "Подорожник для Windows $version.`n`nАвтономная сборка Windows x64: распакуйте ZIP целиком и запустите Plantain.Windows.exe. .NET отдельно не требуется. SHA-256 находится рядом.`n`nЕсть версия для macOS: https://github.com/sirdimitry/plantain/blob/main/README.md`n`nИзменения и ограничения: https://github.com/sirdimitry/plantain/blob/windows/windows/CHANGELOG.md`n"
[IO.File]::WriteAllText($notes, $body, [Text.UTF8Encoding]::new($false))
if (!$Publish) {
    Write-Output "Ready: $tag; ZIP + SHA256. To publish explicitly, run release.ps1 -Publish."
    exit 0
}
$fullRevision = git -C $repoRoot rev-parse HEAD
$arguments = @('release','create',$tag,'--repo','sirdimitry/plantain','--target',$fullRevision,'--latest=false','--title',"Подорожник $version — Windows",'--notes-file',$notes,$zip,$sha)
if ($version.Contains('-')) { $arguments += '--prerelease' }
& gh @arguments
if ($LASTEXITCODE -ne 0) { throw 'GitHub release publication failed' }
