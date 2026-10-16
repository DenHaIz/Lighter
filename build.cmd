@echo off
rem Builds bin\Lighter.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe not found. .NET Framework 4.x is required.
  exit /b 1
)

if not exist bin mkdir bin
if not exist obj mkdir obj

set "OPTS=/nologo /codepage:65001 /optimize+ /platform:anycpu"
set "REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll"

rem 1. The .exe icon is drawn by the same code as the in-app logo (src\Logo.cs).
"%CSC%" %OPTS% /target:exe /out:obj\MakeIcon.exe %REFS% tools\MakeIcon.cs src\Logo.cs src\Theme.cs
if errorlevel 1 goto failed
obj\MakeIcon.exe obj\app.ico
if errorlevel 1 goto failed

rem 2. The application.
"%CSC%" %OPTS% /target:winexe /out:bin\Lighter.exe /win32manifest:app.manifest /win32icon:obj\app.ico %REFS% src\*.cs
if errorlevel 1 goto failed

echo Build OK: %CD%\bin\Lighter.exe
exit /b 0

:failed
echo Build FAILED
exit /b 1
