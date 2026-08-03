@echo off
setlocal
rem ==========================================================================
rem iGloo setup builder - one command from source tree to iGloo-Setup-<ver>.exe
rem
rem   1. Copies src\ + distros\ to C:\Temp\igloo-build  (publishing straight
rem      from this checkout fails: the SDK's publish Copy step chokes on the
rem      apostrophe in "Gilles D'huyvetter" with MSB3094 - SDK quirk, not ours)
rem   2. Reads the version from src\Igloo.App\Igloo.App.csproj (<Version>)
rem   3. dotnet publish  (win-x64, self-contained - this is what makes the
rem      installer work on PCs WITHOUT .NET installed; a plain VS build does
rem      NOT produce this payload)
rem   4. Copies the publish output into installer\publish
rem   5. Compiles installer\iGloo.iss with Inno Setup
rem
rem Result: installer\output\iGloo-Setup-<version>.exe + SHA256 on screen.
rem
rem Version bumps: edit <Version> in src\Igloo.App\Igloo.App.csproj only.
rem (iGloo.iss picks it up via /DIglooVersion; update VersionInfoVersion in
rem the .iss by hand when major.minor changes.)
rem ==========================================================================

set "ROOT=%~dp0.."
set "BUILD=C:\Temp\igloo-build"
set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
set "PATH=C:\Program Files\dotnet;%PATH%"
rem Belt-and-braces: dotnet's NuGet restore resolves %ProgramData%\NuGet etc.
rem via Environment.GetFolderPath; a shell missing these variables fails with
rem "NuGet.targets(782,5): Value cannot be null. (Parameter 'path1')". Setting
rem them explicitly makes this script work from ANY shell.
set "ProgramData=C:\ProgramData"
set "PUBLIC=C:\Users\Public"
set "ProgramFiles=C:\Program Files"
set "ProgramFiles(x86)=C:\Program Files (x86)"
set "CommonProgramFiles=C:\Program Files\Common Files"
set "CommonProgramFiles(x86)=C:\Program Files (x86)\Common Files"
rem Lingering MSBuild/compiler servers keep the env of whatever shell spawned
rem them first (they idle for 15 minutes) - shut them down so they respawn
rem with THIS environment.
dotnet build-server shutdown >nul 2>&1

echo [1/5] Broncode kopieren naar %BUILD% ...
if exist "%BUILD%" rmdir /s /q "%BUILD%"
mkdir "%BUILD%"
robocopy "%ROOT%\src" "%BUILD%\src" /E /NFL /NDL /NJH /NJS /XD bin obj
robocopy "%ROOT%\distros" "%BUILD%\distros" /E /NFL /NDL /NJH /NJS
copy /y "%ROOT%\Directory.Build.props" "%BUILD%\" >nul
copy /y "%ROOT%\.editorconfig" "%BUILD%\" >nul
rem robocopy exit codes 0-7 are success; do not errorlevel-check them

echo [2/5] Versie lezen uit csproj ...
set "PSH=C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
for /f "usebackq delims=" %%v in (`%PSH% -NoProfile -Command "([xml](Get-Content '%BUILD%\src\Igloo.App\Igloo.App.csproj')).Project.PropertyGroup.Version | Select-Object -First 1"`) do set "VER=%%v"
if "%VER%"=="" (echo FOUT: geen versie gevonden & exit /b 1)
echo     Versie: %VER%

echo [3/5] dotnet publish (win-x64, self-contained) ...
pushd "%BUILD%"
dotnet publish src\Igloo.App\Igloo.App.csproj -c Release -r win-x64 --self-contained true -o publish --nologo -v q
if errorlevel 1 (echo FOUT: publish mislukt & popd & exit /b 1)
popd

echo [4/5] Publish kopieren naar installer\publish ...
if exist "%~dp0publish" rmdir /s /q "%~dp0publish"
robocopy "%BUILD%\publish" "%~dp0publish" /E /NFL /NDL /NJH /NJS

echo [5/5] Inno Setup compileren ...
if not exist "%ISCC%" (echo FOUT: Inno Setup niet gevonden op %ISCC% & exit /b 1)
"%ISCC%" "/DIglooPublishDir=%~dp0publish" "/DIglooVersion=%VER%" "%~dp0iGloo.iss"
if errorlevel 1 (echo FOUT: ISCC mislukt & exit /b 1)

echo.
echo Klaar: installer\output\iGloo-Setup-%VER%.exe
echo SHA256:
certutil -hashfile "%~dp0output\iGloo-Setup-%VER%.exe" SHA256 | findstr /v ":"
endlocal
