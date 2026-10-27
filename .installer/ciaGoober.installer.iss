#ifndef AppVersion
#error AppVersion must be supplied by buildInstaller.ps1
#endif
#ifndef AppName
#define AppName "ciaGoober"
#endif
#ifndef AppExeName
#define AppExeName "ciaGoober.exe"
#endif
#ifndef AppPublisher
#define AppPublisher "fosterbarnes"
#endif
#ifndef AppURL
#define AppURL "https://github.com/fosterbarnes/ciaGoober"
#endif
#ifndef SetupIconFile
#define SetupIconFile "..\.res\icon\icon.ico"
#endif
#ifndef WizardImageFile
#define WizardImageFile "..\.res\icon\installer-wizard-large.png"
#endif
#ifndef WizardSmallImageFile
#define WizardSmallImageFile "..\.res\icon\installer-wizard-small.png"
#endif
#ifndef LicenseFile
#define LicenseFile "..\LICENSE"
#endif

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
UninstallDisplayIcon={app}\{#AppExeName}
WizardStyle=modern dark
WizardBackColor=#1B1B1B
WizardImageBackColor=#1B1B1B
WizardSmallImageBackColor=#1B1B1B
SetupIconFile={#SetupIconFile}
WizardImageFile={#WizardImageFile}
WizardSmallImageFile={#WizardSmallImageFile}
LicenseFile={#LicenseFile}
DisableWelcomePage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest

[Tasks]
Name: startmenuicon; Description: "Create a Start Menu shortcut"; GroupDescription: "{cm:AdditionalIcons}"
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure InitializeWizard;
begin
  WizardForm.LicenseAcceptedRadio.Checked := True;
end;
