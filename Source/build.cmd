@echo off
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo .NET Framework 4 compiler not found.
  exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /out:"..\UltraClick.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /main:UltraClick.Program Program.cs EngineTests.cs LiveReceiver.cs
exit /b %errorlevel%



