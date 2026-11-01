@echo off
rem ===========================================================================
rem  build.bat - rebuild Chat.exe with the C# compiler shipped in Windows
rem  (no Visual Studio, no Python, no downloads needed)
rem  NOTE: this file is intentionally ASCII-only so it displays correctly on any
rem        Windows code page. Chinese docs are in the txt files.
rem ===========================================================================
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe not found. Please install .NET Framework 4.x.
  pause
  exit /b 1
)

echo Compiler: %CSC%

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\System.Web.Extensions.dll" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"

"%CSC%" /nologo /noconfig /target:winexe /optimize+ /codepage:65001 ^
  /out:"Chat.exe" ^
  /r:"%FW%\mscorlib.dll" ^
  /r:"%FW%\System.dll" ^
  /r:"%FW%\System.Core.dll" ^
  /r:"%FW%\System.Drawing.dll" ^
  /r:"%FW%\System.Windows.Forms.dll" ^
  /r:"%FW%\System.Web.Extensions.dll" ^
  "Chat.cs"

if errorlevel 1 (
  echo.
  echo [FAILED] compile error, see messages above.
  pause
  exit /b 1
)

echo.
echo [OK] Chat.exe generated.
echo      run:      Chat.exe
echo      selftest: Chat.exe --selftest  (writes selftest.log)
echo.
pause

rem --- If the target PC is 32-bit, add  /platform:x86  to the csc line above. ---
