; ============================================================================
; iGloo Community installer script (Inno Setup 6)
; ----------------------------------------------------------------------------
; Builds iGloo-Community-<release>.exe from a `dotnet publish` output folder.
;
; 1. Publish (self-contained, includes the distros\ tree):
;      dotnet publish src\Igloo.Community.App\Igloo.Community.App.csproj -c Release -r win-x64 ^
;          --self-contained true -o installer\publish
;    NOTE: publish from a path WITHOUT an apostrophe. Under
;    C:\Users\O'Connor\... the SDK's publish Copy step collapses
;    %(RelativePath) and fails with MSB3094 ("DestinationFiles refers to 1
;    item(s)") - robocopy src\ + distros\ + Directory.Build.props +
;    .editorconfig to a clean path (e.g. C:\Temp\igloo-build) and publish
;    there. This is an SDK/publish-targets quirk with quoted paths, not an
;    iGloo bug; builds (non-publish) work fine from the real checkout.
;
; 2. Compile the installer:
;      ISCC.exe /DIglooPublishDir="installer\publish" installer\iGloo.iss
;    (override IglooPublishDir when publishing outside the repo, as above)
;
; Note: the Setup.exe is UNSIGNED. An unsigned installer for a tool that
; repartitions disks will trip Windows SmartScreen - expected until a
; code-signing certificate exists
; (see OSS_RELEASE_CHECKLIST.md section 7).
; ============================================================================

#ifndef IglooPublishDir
  #define IglooPublishDir "publish"
#endif
#ifndef IglooVersion
  #define IglooVersion "beta1"
#endif

[Setup]
AppId={{7B4E9C2A-6F3D-4A1B-9E5C-2D8F0A3B6C71}
AppName=iGloo Community
AppVersion={#IglooVersion}
; The name in Installed apps carries no release: that list has a column for it.
AppVerName=iGloo Community
AppPublisher=Gilles D'huyvetter
AppPublisherURL=https://github.com/gillesduif/iGloo
; The folder and the program file keep their names: the sign-in task and installations of earlier builds point at them.
DefaultDirName={autopf}\iGloo
; One shortcut in the Start menu, no folder around it.
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=output
OutputBaseFilename=iGloo-Community-{#IglooVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\iGloo.exe
VersionInfoVersion=1.0.0.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "{#IglooPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "__pycache__\*,*.pyc"

[InstallDelete]
; Shortcuts of builds that called the program iGloo.
Type: filesandordirs; Name: "{autoprograms}\iGloo"
Type: files; Name: "{autodesktop}\iGloo.lnk"

[Icons]
Name: "{autoprograms}\iGloo Community"; Filename: "{app}\iGloo.exe"
Name: "{autodesktop}\iGloo Community"; Filename: "{app}\iGloo.exe"; Tasks: desktopicon

[Run]
; shellexec is required: iGloo.exe has requireAdministrator in its manifest and a
; plain CreateProcess cannot raise a UAC prompt (error 740 on the finish page).
Filename: "{app}\iGloo.exe"; Description: "Launch iGloo Community"; Flags: nowait postinstall skipifsilent shellexec

[Registry]
; Holds the Fast Startup value from before a dual-boot installation; kept while Linux may still need it.
Root: HKLM; Subkey: "SOFTWARE\iGloo"; Flags: uninsdeletekeyifempty

[UninstallRun]
; The wizard schedules this task at Restart; it would start a file that no longer exists.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""iGloo boot entry cleanup"" /F"; Flags: runhidden; RunOnceId: "IglooBootEntryCleanupTask"
