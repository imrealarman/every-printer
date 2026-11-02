; Inno Setup script for Every Printer. Build with:  ISCC.exe installer\EveryPrinter.iss
; Expects the self-contained publish output in ..\publish (see build-installer.ps1).

#define AppName "Every Printer"
#define AppVersion "1.2.0"
#define AppExe "EveryPrinter.exe"

[Setup]
AppId={{B4E7C2D1-6A3F-4D58-9C0B-2F81E5A7D3C6}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Arman Soltani
AppContact=imrealarman@gmail.com
AppCopyright=Copyright (C) 2026 Arman Soltani
VersionInfoCompany=Arman Soltani
VersionInfoDescription=Every Printer Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Installs for the current user without admin rights; the wizard offers "all users" if the user wants it.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=Output
OutputBaseFilename=EveryPrinter-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "printer"; Description: "Add the &Every Printer printer (asks for administrator permission once)"; GroupDescription: "Printing:"
Name: "shellprint"; Description: "Add ""Print with Every Printer"" to the right-click menu of PDFs, pictures and text files"; GroupDescription: "Printing:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; One verb per file type, under the current user, so right-clicking a file offers it without taking over the default app.
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\EveryPrinter"; ValueType: string; ValueName: ""; ValueData: "Print with Every Printer"; Flags: uninsdeletekey; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\EveryPrinter"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\EveryPrinter\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\EveryPrinter"; ValueType: string; ValueName: ""; ValueData: "Print with Every Printer"; Flags: uninsdeletekey; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\EveryPrinter"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.png\shell\EveryPrinter\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\EveryPrinter"; ValueType: string; ValueName: ""; ValueData: "Print with Every Printer"; Flags: uninsdeletekey; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\EveryPrinter"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpg\shell\EveryPrinter\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\EveryPrinter"; ValueType: string; ValueName: ""; ValueData: "Print with Every Printer"; Flags: uninsdeletekey; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\EveryPrinter"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.jpeg\shell\EveryPrinter\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\EveryPrinter"; ValueType: string; ValueName: ""; ValueData: "Print with Every Printer"; Flags: uninsdeletekey; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\EveryPrinter"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.bmp\shell\EveryPrinter\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.gif\shell\EveryPrinter"; ValueType: string; ValueName: ""; ValueData: "Print with Every Printer"; Flags: uninsdeletekey; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.gif\shell\EveryPrinter"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.gif\shell\EveryPrinter\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.tif\shell\EveryPrinter"; ValueType: string; ValueName: ""; ValueData: "Print with Every Printer"; Flags: uninsdeletekey; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.tif\shell\EveryPrinter"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.tif\shell\EveryPrinter\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.tiff\shell\EveryPrinter"; ValueType: string; ValueName: ""; ValueData: "Print with Every Printer"; Flags: uninsdeletekey; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.tiff\shell\EveryPrinter"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.tiff\shell\EveryPrinter\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.webp\shell\EveryPrinter"; ValueType: string; ValueName: ""; ValueData: "Print with Every Printer"; Flags: uninsdeletekey; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.webp\shell\EveryPrinter"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.webp\shell\EveryPrinter\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.txt\shell\EveryPrinter"; ValueType: string; ValueName: ""; ValueData: "Print with Every Printer"; Flags: uninsdeletekey; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.txt\shell\EveryPrinter"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.txt\shell\EveryPrinter\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.log\shell\EveryPrinter"; ValueType: string; ValueName: ""; ValueData: "Print with Every Printer"; Flags: uninsdeletekey; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.log\shell\EveryPrinter"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.log\shell\EveryPrinter\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.md\shell\EveryPrinter"; ValueType: string; ValueName: ""; ValueData: "Print with Every Printer"; Flags: uninsdeletekey; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.md\shell\EveryPrinter"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.md\shell\EveryPrinter\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.csv\shell\EveryPrinter"; ValueType: string; ValueName: ""; ValueData: "Print with Every Printer"; Flags: uninsdeletekey; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.csv\shell\EveryPrinter"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"""; Tasks: shellprint
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.csv\shell\EveryPrinter\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: shellprint

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Assets\printer-setup.ps1"" -Action Install -Inbox ""{userdocs}\Every Printer\Inbox"" -Exe ""{app}\{#AppExe}"" -User ""{computername}\{username}"""; StatusMsg: "Adding the Every Printer printer..."; Flags: shellexec runhidden waituntilterminated; Verb: "runas"; Tasks: printer
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Assets\printer-setup.ps1"" -Action Remove -Inbox ""{userdocs}\Every Printer\Inbox"""; Flags: shellexec runhidden waituntilterminated; Verb: "runas"; RunOnceId: "RemovePrinter"

; Settings, themes and history (%AppData%\EveryPrinter) and your saved files are deliberately left in place on uninstall.
