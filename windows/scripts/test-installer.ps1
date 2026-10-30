param([Parameter(Mandatory)][string]$Setup, [string]$PreviousSetup = '')
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path $PSScriptRoot -Parent
$version = (Get-Content (Join-Path $windowsRoot 'VERSION') -Raw).Trim()
$setupFile = (Resolve-Path -LiteralPath $Setup).Path
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{97E4A1C8-2D71-48DA-9B1B-BD68B12AA3AD}_is1'
$versionKey = 'HKCU:\Software\sirdimitry\Plantain'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Подорожник.lnk'
if (Test-Path -LiteralPath $uninstallKey) { throw 'An installed copy already exists; preserve it and test in another account/VM' }
if (Test-Path -LiteralPath $versionKey) { throw 'An existing version record was found; preserve it and test in another account/VM' }
if (Test-Path -LiteralPath $desktopShortcut) { throw 'A desktop shortcut already exists; preserve it and test in another account/VM' }
if (Get-Process -Name Plantain.Windows -ErrorAction SilentlyContinue) { throw 'Close existing app instances before installer tests' }
$id = [Guid]::NewGuid().ToString('N')
$testRoot = [IO.Path]::GetFullPath((Join-Path $windowsRoot "artifacts/installer-tests/$id"))
$destination = Join-Path $testRoot 'Программа с пробелами'
$downloads = Join-Path $testRoot 'Скачанные файлы'
$group = "Подорожник"
$startShortcut = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('Programs')) "$group/Подорожник.lnk"))
if (Test-Path -LiteralPath (Split-Path $startShortcut -Parent)) { throw "A Start menu group already exists; preserve it and test in another account/VM" }
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
$sentinel = Join-Path $downloads 'не удалять.txt'
[IO.File]::WriteAllText($sentinel, 'downloaded user content')
$results = [System.Collections.Generic.List[object]]::new()
$appProcess = $null
$completed = $false

function Check([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw $Name }
    $results.Add([ordered]@{ name = $Name; passed = $true })
    Write-Output "PASS INSTALLER $Name"
}
function RunInstaller([string]$File, [string[]]$Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(60000)) { $process.Kill(); throw 'Installer test process timed out' }
    return $process.ExitCode
}
function Install([string]$File) {
    RunInstaller $File @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/LANG=russian', ('/DIR="' + $destination + '"'), ('/GROUP="' + $group + '"'), '/TASKS=desktopicon')
}
try {
    if ($PreviousSetup) {
        Check ((Install (Resolve-Path -LiteralPath $PreviousSetup).Path) -eq 0) 'previous installer succeeds'
        $old = Get-ItemProperty -LiteralPath $uninstallKey
        Check ($old.DisplayVersion -ne $version) 'previous version registered before upgrade'
    }
    Check ((Install $setupFile) -eq 0) 'offline installation succeeds in a path with spaces and Cyrillic'
    $exe = [IO.Path]::GetFullPath((Join-Path $destination 'app/Plantain.Windows.exe'))
    Check ((Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath (Join-Path $destination 'app/coreclr.dll'))) 'application and private runtime installed'
    $installed = Get-ItemProperty -LiteralPath $uninstallKey
    Check ($installed.DisplayVersion -eq $version -and $installed.InstallLocation.TrimEnd('\') -eq $destination.TrimEnd('\')) 'correct version and path registered in current-user Apps'
    Check ((Test-Path -LiteralPath $startShortcut) -and (Test-Path -LiteralPath $desktopShortcut)) 'Start menu and optional desktop shortcuts created'
    if (!('Plantain.InstallerTests.Shortcut' -as [type])) { Add-Type -Path (Join-Path $windowsRoot 'tests/Installer/Shortcut.cs') }
    Check ([Plantain.InstallerTests.Shortcut]::Target($startShortcut) -eq $exe) 'Unicode shortcut points to installed application'
    Check ((Install $setupFile) -eq 0) 'same-version reinstall succeeds without duplicate product entry'
    if ($PreviousSetup) {
        Check ((Install (Resolve-Path -LiteralPath $PreviousSetup).Path) -ne 0) 'downgrade to previous version is blocked'
        Check ((Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion -eq $version) 'blocked downgrade preserves installed version'
    }
    $launchInfo = [Diagnostics.ProcessStartInfo]::new($exe)
    $launchInfo.UseShellExecute = $false
    $launchInfo.WorkingDirectory = Join-Path $destination 'app'
    # Force discovery away from any system .NET. Self-contained execution must still work.
    $launchInfo.Environment['DOTNET_ROOT'] = Join-Path $testRoot 'no-dotnet-installed'
    $launchInfo.Environment['DOTNET_ROOT_X64'] = $launchInfo.Environment['DOTNET_ROOT']
    $launchInfo.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $launchInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $appProcess = [Diagnostics.Process]::Start($launchInfo)
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do { Start-Sleep -Milliseconds 200; $appProcess.Refresh() } while (!$appProcess.HasExited -and $appProcess.MainWindowTitle -ne 'Подорожник' -and [DateTime]::UtcNow -lt $deadline)
    Check (!$appProcess.HasExited -and $appProcess.MainWindowTitle -eq 'Подорожник') 'installed app opens without global .NET discovery'
    $runtime = $appProcess.Modules | Where-Object ModuleName -eq 'coreclr.dll' | Select-Object -First 1 -ExpandProperty FileName
    Check ([IO.Path]::GetFullPath($runtime) -eq [IO.Path]::GetFullPath((Join-Path $destination 'app/coreclr.dll'))) 'runtime loaded from installation directory'
    $marker = $null
    Check ([Threading.Mutex]::TryOpenExisting('Local\Plantain.Windows.97E4A1C8', [ref]$marker)) 'running app exposes installer protection marker'
    $marker.Dispose()
    Check ((Install $setupFile) -ne 0) 'installer refuses to replace a running app'
    $appProcess.Refresh()
    Check (!$appProcess.HasExited) 'blocked installer does not force-close application'
    $uninstaller = Join-Path $destination 'uninstall/unins000.exe'
    Check ((RunInstaller $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')) -ne 0) 'uninstaller refuses to remove a running app'
    $appProcess.CloseMainWindow() | Out-Null
    Check ($appProcess.WaitForExit(10000)) 'application closes normally'
    $appProcess = $null
    Check ((RunInstaller $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')) -eq 0) 'uninstallation succeeds'
    Check (!(Test-Path -LiteralPath $exe) -and !(Test-Path -LiteralPath $uninstallKey) -and !(Test-Path -LiteralPath $versionKey)) 'uninstall removes installed app and product registration'
    Check (!(Test-Path -LiteralPath $startShortcut) -and !(Test-Path -LiteralPath $desktopShortcut)) 'uninstall removes created shortcuts'
    Check ([IO.File]::ReadAllText($sentinel) -eq 'downloaded user content') 'downloaded user files survive installation upgrade and uninstall'
    $completed = $true
} finally {
    if ($appProcess -and !$appProcess.HasExited) { $appProcess.CloseMainWindow() | Out-Null; $appProcess.WaitForExit(10000) | Out-Null }
    if (!$completed) {
        $testUninstall = Join-Path $destination 'uninstall/unins000.exe'
        if (Test-Path -LiteralPath $testUninstall) {
            RunInstaller $testUninstall @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') | Out-Null
        }
    }
    $report = [ordered]@{ version = $version; setup = [IO.Path]::GetFileName($setupFile); sha256 = (Get-FileHash -LiteralPath $setupFile -Algorithm SHA256).Hash.ToLowerInvariant(); utc = [DateTimeOffset]::UtcNow; completed = $completed; windows = [Environment]::OSVersion.VersionString; passed = $results.Count; results = $results }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'result.json') -Encoding utf8
    Write-Output "Installer report: $testRoot/result.json"
}
