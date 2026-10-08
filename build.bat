@echo off
rem Builds PortTray.exe with the C# compiler that ships inside Windows (.NET Framework 4.x).
rem No Visual Studio / SDK needed.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found - .NET Framework 4.x missing?
  pause
  exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /out:PortTray.exe /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll PortTray.cs
if errorlevel 1 ( pause & exit /b 1 )
echo Built PortTray.exe
start "" PortTray.exe
