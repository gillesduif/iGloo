@echo off
rem Dev launcher: runs the built CLI from its own output directory.
setlocal
for %%I in ("%~dp0..") do set "IGLOO_REPO=%%~fI"
if "%IGLOO_CONFIGURATION%"=="" set "IGLOO_CONFIGURATION=Debug"
set "IGLOO_EXE=%IGLOO_REPO%\src\Igloo.Cli\Igloo.Cli\bin\%IGLOO_CONFIGURATION%\portable\net10.0-windows10.0.19041.0\Igloo.Cli.exe"
if not exist "%IGLOO_EXE%" (
    echo No %IGLOO_CONFIGURATION% CLI build found. Run: dotnet build "%IGLOO_REPO%\src\Igloo.Cli\Igloo.Cli\Igloo.Cli.csproj" -c %IGLOO_CONFIGURATION% 1>&2
    exit /b 4
)
"%IGLOO_EXE%" %*
exit /b %ERRORLEVEL%
