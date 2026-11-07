#ifndef AppVersion
  #define AppVersion "1.4.2"
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef ReleaseDir
  #error ReleaseDir is required
#endif
#define RepoDir AddBackslash(SourcePath) + ".."

[Setup]
AppId={{89D3E791-66C3-4608-9309-DC18D65F22F4}
AppName=EverythingonStart
AppVersion={#AppVersion}
AppPublisher=Hurt-In-Dream
AppPublisherURL=https://github.com/Hurt-In-Dream/EverythingonStart
AppSupportURL=https://github.com/Hurt-In-Dream/EverythingonStart/issues
AppUpdatesURL=https://github.com/Hurt-In-Dream/EverythingonStart/releases
DefaultDirName={localappdata}\Programs\EverythingonStart
DefaultGroupName=EverythingonStart
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
OutputDir={#ReleaseDir}
OutputBaseFilename=EverythingonStart-v{#AppVersion}-Setup-x64
SetupIconFile={#RepoDir}\Assets\AppIcon.ico
UninstallDisplayIcon={app}\EverythingonStart.exe
LicenseFile={#RepoDir}\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=no
RestartApplications=no
ChangesEnvironment=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\EverythingonStart.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoDir}\licenses\*.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{group}\EverythingonStart"; Filename: "{app}\EverythingonStart.exe"
Name: "{group}\Uninstall EverythingonStart"; Filename: "{uninstallexe}"
Name: "{userdesktop}\EverythingonStart"; Filename: "{app}\EverythingonStart.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\EverythingonStart.exe"; Description: "Launch EverythingonStart"; Flags: nowait postinstall skipifsilent unchecked

; No autostart changes and no deletion of %LOCALAPPDATA%\EverythingonStart\settings.json.
