; Build with windows/scripts/installer.ps1. All versions and paths are supplied by it.
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef NumericVersion
  #error NumericVersion is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef PackageDir
  #error PackageDir is required
#endif
#define ProductName "Подорожник"

[Setup]
AppId={{97E4A1C8-2D71-48DA-9B1B-BD68B12AA3AD}
AppName={#ProductName}
AppVersion={#AppVersion}
AppVerName={#ProductName} {#AppVersion}
AppPublisher=sirdimitry
AppPublisherURL=https://github.com/sirdimitry
AppSupportURL=https://github.com/sirdimitry/plantain/issues
AppUpdatesURL=https://github.com/sirdimitry/plantain/releases
VersionInfoVersion={#NumericVersion}
VersionInfoDescription=Подорожник — установка для Windows
DefaultDirName={localappdata}\Programs\Plantain
DefaultGroupName=Подорожник
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupArchitecture=x64
MinVersion=10.0.14393
OutputDir={#PackageDir}
OutputBaseFilename=Plantain-{#AppVersion}-win-x64-Setup
SetupIconFile=assets\Plantain.ico
WizardImageFile=assets\WizardBanner.png
WizardSmallImageFile=assets\WizardIcon.png
WizardStyle=modern dynamic
WizardSizePercent=110
DisableWelcomePage=no
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
UninstallDisplayName={#ProductName}
UninstallDisplayIcon={app}\app\Plantain.Windows.exe
UninstallFilesDir={app}\uninstall
AppMutex=Local\Plantain.Windows.97E4A1C8
SetupMutex=Local\Plantain.Setup.97E4A1C8
CloseApplications=no
RestartApplications=no
Compression=lzma2/normal
SolidCompression=yes
LZMANumBlockThreads=2
LZMADictionarySize=16384
DiskSpanning=no
AllowNoIcons=yes
ChangesAssociations=no
ChangesEnvironment=no
SetupLogging=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
russian.WelcomeLabel2=Подорожник скачивает публичные файлы и папки Яндекс Диска.%n%nУстановка только для вашей учётной записи. Среда .NET уже включена; подключение к интернету во время установки не требуется.
english.WelcomeLabel2=Plantain downloads public Yandex Disk files and folders.%n%nInstalls for your user account only. The .NET runtime is included; installation works offline.

[CustomMessages]
russian.DesktopIcon=Создать ярлык на рабочем столе
english.DesktopIcon=Create a desktop shortcut
russian.RunApp=Запустить Подорожник
english.RunApp=Launch Plantain
russian.NewerInstalled=Уже установлена более новая версия Подорожника. Используйте актуальный установщик.
english.NewerInstalled=A newer version of Plantain is already installed. Please use the latest installer.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\docs\INSTALLED.ru.txt"; DestDir: "{app}"; DestName: "Прочитайте меня.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\Подорожник"; Filename: "{app}\app\Plantain.Windows.exe"; WorkingDir: "{app}\app"; Comment: "Подорожник — скачает, когда Диск не может"
Name: "{autodesktop}\Подорожник"; Filename: "{app}\app\Plantain.Windows.exe"; WorkingDir: "{app}\app"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\sirdimitry\Plantain"; ValueType: string; ValueName: "PlantainVersionCode"; ValueData: "{#NumericVersion}"; Flags: uninsdeletevalue uninsdeletekeyifempty

[Run]
Filename: "{app}\app\Plantain.Windows.exe"; Description: "{cm:RunApp}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
function CompareNumericVersions(A, B: String): Integer;
var I, P, First, Second: Integer;
begin
  Result := 0;
  for I := 1 to 4 do begin
    P := Pos('.', A);
    if P = 0 then P := Length(A) + 1;
    First := StrToIntDef(Copy(A, 1, P - 1), 0);
    Delete(A, 1, P);
    P := Pos('.', B);
    if P = 0 then P := Length(B) + 1;
    Second := StrToIntDef(Copy(B, 1, P - 1), 0);
    Delete(B, 1, P);
    if First < Second then begin Result := -1; Exit; end;
    if First > Second then begin Result := 1; Exit; end;
  end;
end;

function InitializeSetup(): Boolean;
var InstalledVersion: String;
begin
  Result := True;
  if RegQueryStringValue(HKCU,
    'Software\sirdimitry\Plantain',
    'PlantainVersionCode', InstalledVersion) then
    if CompareNumericVersions(InstalledVersion, '{#NumericVersion}') > 0 then begin
      SuppressibleMsgBox(ExpandConstant('{cm:NewerInstalled}'), mbError, MB_OK, IDOK);
      Result := False;
    end;
end;
