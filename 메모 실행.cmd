@echo off
setlocal
set "MEMO_EXE=%~dp0src\WindowsMemo\bin\Release\net10.0-windows\WindowsMemo.exe"
set "MEMO_MODE="
set "MEMO_BUILD_ONLY="
if "%~1"=="" goto ready
if /i "%~1"=="--window" goto window_mode
if /i "%~1"=="--build-only" goto build_mode
echo Usage: "%~nx0" [--window ^| --build-only]
exit /b 2

:window_mode
set "MEMO_MODE=--window"
goto ready

:build_mode
set "MEMO_BUILD_ONLY=1"

:ready
pushd "%~dp0"
if errorlevel 1 exit /b 1
if defined MEMO_BUILD_ONLY goto build
if not exist "%MEMO_EXE%" goto build
tasklist /FI "IMAGENAME eq WindowsMemo.exe" /FI "USERNAME eq %USERDOMAIN%\%USERNAME%" /NH 2>nul | find /I "WindowsMemo.exe" >nul
if not errorlevel 1 goto already_running

:build
set "MEMO_DOTNET=dotnet"
where dotnet >nul 2>nul
if not errorlevel 1 goto compile
set "MEMO_DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if exist "%MEMO_DOTNET%" goto compile
echo .NET SDK 10.0.401 is required. Install it, then run this launcher again.
goto failed

:compile
"%MEMO_DOTNET%" build src\WindowsMemo\WindowsMemo.csproj --configuration Release --nologo -p:RestoreLockedMode=true
if errorlevel 1 goto failed
if defined MEMO_BUILD_ONLY goto complete
goto launch

:already_running
echo Windows Memo is already running. Opening its note list.
echo To use changed source code, save and exit the running app, then run this launcher again.

:launch
start "" "%MEMO_EXE%" %MEMO_MODE%
if errorlevel 1 goto failed

:complete
popd
exit /b 0

:failed
echo.
echo Build or launch failed. Existing app files were not started.
echo If Windows Memo is running, save and exit it before rebuilding.
popd
if not defined MEMO_BUILD_ONLY pause
exit /b 1
