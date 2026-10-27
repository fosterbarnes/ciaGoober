#ifndef AppVersion
#error AppVersion must be supplied by buildInstaller.ps1
#endif
[Setup]
AppId={{F98559B7-1963-4643-A378-8737B1D9331A}
DefaultDirName={userappdata}\ciaGoober
OutputDir=Output
OutputBaseFilename=ciaGoober-x64-installer
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Uninstallable=yes
[Files]
Source: "..\publish\build\x64\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
#include "ciaGoober.installer.iss"
