@echo off
setlocal
rem Builds ScheduledClicker.exe using the C# compiler that ships with Windows.
rem No Visual Studio or SDK required.

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Could not find csc.exe. .NET Framework 4.x is required.
    pause
    exit /b 1
)

cd /d "%~dp0"
"%CSC%" /nologo /target:winexe /optimize+ /debug- /win32icon:mouse.ico /out:ScheduledClicker.exe ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll ^
  ScheduledClicker.cs

if errorlevel 1 (
    echo.
    echo BUILD FAILED
    pause
    exit /b 1
)

echo.
echo Built ScheduledClicker.exe
for %%F in (ScheduledClicker.exe) do echo Size: %%~zF bytes
pause
