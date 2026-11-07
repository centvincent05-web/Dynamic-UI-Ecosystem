@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"
set "LOG=%~dp0build-log.txt"
title Dynamic Island - build dan jalankan

echo ==============================================
echo   Dynamic Island - build dan jalankan
echo ==============================================
echo.

rem --- tutup versi lama kalau masih berjalan ---
taskkill /im DynamicIsland.exe /f >nul 2>nul
taskkill /im DynamicIsland.LiveWallpaper.exe /f >nul 2>nul
rem sisa versi lama (hard link ke DynamicIsland.exe) harus dihapus sebelum build, jangan sampai tertimpa
if exist "%~dp0bin\Release\net8.0-windows10.0.22621.0\DynamicIsland.LiveWallpaper.exe" del /f /q "%~dp0bin\Release\net8.0-windows10.0.22621.0\DynamicIsland.LiveWallpaper.exe"

rem --- cek .NET 8 SDK ---
dotnet --list-sdks 2>nul | findstr /b "8." >nul
if errorlevel 1 goto install_sdk
goto build

:install_sdk
echo [1/3] .NET 8 SDK belum ada, menginstall lewat winget. Cukup sekali saja.
echo       Kalau muncul jendela izin admin, klik Yes.
winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
set "PATH=%PATH%;%ProgramFiles%\dotnet"
dotnet --list-sdks 2>nul | findstr /b "8." >nul
if errorlevel 1 (
  echo GAGAL install .NET SDK. > "%LOG%"
  echo Gagal menginstall .NET 8 SDK. Install manual dari https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)

:build
echo [2/3] Build... tunggu sebentar, pertama kali bisa 1-2 menit.
dotnet build -c Release -nologo -nodeReuse:false -p:UseSharedCompilation=false > "%LOG%" 2>&1
set "BUILDERR=%errorlevel%"
rem exe khusus live wallpaper (memakai DynamicIsland.dll yang baru dibuild)
if "%BUILDERR%"=="0" (
  dotnet build "LiveWallpaper\DynamicIsland.LiveWallpaper.csproj" -c Release -nologo -nodeReuse:false -p:UseSharedCompilation=false >> "%LOG%" 2>&1
  set "BUILDERR=!errorlevel!"
)
rem tutup proses build yang tertinggal di latar (.NET Host, bisa 100+ MB)
dotnet build-server shutdown >nul 2>nul
if not "%BUILDERR%"=="0" goto build_failed

echo [3/3] Menjalankan Dynamic Island...
if exist "%~dp0crash.log" del "%~dp0crash.log"
start "" "%~dp0bin\Release\net8.0-windows10.0.22621.0\DynamicIsland.exe"
echo.
echo BERHASIL. Island sudah berjalan. Jendela ini akan tertutup sendiri.
echo BUILD OK >> "%LOG%"
timeout /t 6 >nul
exit /b 0

:build_failed
echo.
echo BUILD GAGAL. Detail error tersimpan di build-log.txt
echo Kasih tahu Claude: "build gagal", nanti langsung diperbaiki.
echo.
findstr /i /c:"error" "%LOG%"
pause
exit /b 1
