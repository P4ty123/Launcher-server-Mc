@echo off
setlocal
cd /d "%~dp0"
title Compilazione MineServer Manager

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
  echo Non trovo il compilatore C# di .NET Framework 4 ^(csc.exe^).
  echo Apri "Attiva o disattiva funzionalita' di Windows" e attiva ".NET Framework 4.8 Advanced Services".
  if not defined CI pause
  exit /b 1
)

echo Compilo MineServerManager.exe ...
"%FW%\csc.exe" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /warn:0 ^
  /win32icon:app.ico ^
  /out:MineServerManager.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:"%FW%\System.Web.Extensions.dll" /r:"%FW%\System.IO.Compression.dll" /r:"%FW%\System.IO.Compression.FileSystem.dll" ^
  src\*.cs

if errorlevel 1 (
  echo.
  echo *** Compilazione NON riuscita: vedi gli errori qui sopra. ***
  if not defined CI pause
  exit /b 1
)

echo.
echo Fatto! Ora puoi avviare MineServerManager.exe
if not defined CI pause
exit /b 0
