@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo جاري بناء البرنامج...
dotnet publish DiskRecovery.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
if errorlevel 1 (
  echo.
  echo فشل البناء. تأكد من تثبيت .NET 10 SDK
  pause
  exit /b 1
)
echo.
echo تم! البرنامج في: %~dp0publish\DiskRecovery.exe
explorer publish
pause
