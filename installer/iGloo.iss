; ============================================================================
; iGloo installer script (Inno Setup 6)
; ----------------------------------------------------------------------------
; Builds iGloo-Setup-<version>.exe from a `dotnet publish` output folder.
;
; 1. Publish (self-contained, includes the distros\ tree):
;      dotnet publish src\Igloo.App\Igloo.App.csproj -c Release -r win-x64 ^
;          --self-contained true -o installer\publish
;    NOTE: publish from a path WITHOUT an apostrophe. Under
;    C:\Users\Gilles D'huyvetter\... the SDK's publish Copy step collapses
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
; Alpha note: the Setup.exe is UNSIGNED. An unsigned installer for a tool that
; repartitions disks will trip Windows SmartScreen - expected and acceptable
; for a source-first alpha; revisit when a code-signing certificate exists
; (see OSS_RELEASE_CHECKLIST.md section 7).
; ============================================================================

#ifndef IglooPublishDir
  #define IglooPublishDir "publish"
#endif
#ifndef IglooVersion
  #define IglooVersion "0.2-alpha"
#endif

[Setup]
AppId={{7B4E9C2A-6F3D-4A1B-9E5C-2D8F0A3B6C71}
AppName=iGloo
AppVersion={#IglooVersion}
; Display name in "Apps & Features" is just "iGloo" - the version has its own
; column there and does not belong in the product name.
AppVerName=iGloo
AppPublisher=Gilles D'huyvetter
AppPublisherURL=https://github.com/gillesduif/iGloo
DefaultDirName={autopf}\iGloo
DefaultGroupName=iGloo
LicenseFile=..\LICENSE
OutputDir=output
OutputBaseFilename=iGloo-Setup-{#IglooVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\iGloo.exe
VersionInfoVersion=0.2.0.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "{#IglooPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "__pycache__\*,*.pyc"

[Icons]
Name: "{group}\iGloo"; Filename: "{app}\iGloo.exe"
Name: "{autodesktop}\iGloo"; Filename: "{app}\iGloo.exe"; Tasks: desktopicon

[Run]
; shellexec is required: iGloo.exe has requireAdministrator in its manifest and a
; plain CreateProcess cannot raise a UAC prompt (error 740 on the finish page).
Filename: "{app}\iGloo.exe"; Description: "Launch iGloo"; Flags: nowait postinstall skipifsilent shellexec
