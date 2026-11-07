@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"
title Dynamic Island - buat paket siap pakai
echo ==============================================
echo   Dynamic Island - buat paket untuk dibagikan
echo ==============================================
echo.
echo Membuat DynamicIsland.exe yang bisa langsung dijalankan
echo tanpa perlu install .NET. Tunggu sekitar 1-2 menit...
echo.

set "OUT=%~dp0paket\DynamicIsland"
set "ZIP=%~dp0DynamicIsland-paket.zip"
if exist "%~dp0paket" rmdir /s /q "%~dp0paket"
if exist "%ZIP%" del "%ZIP%"

dotnet publish -c Release -r win-x64 --self-contained true -nologo -nodeReuse:false ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none -p:UseSharedCompilation=false -o "%OUT%" > "%~dp0paket-log.txt" 2>&1
set "ERR=%errorlevel%"
rem exe khusus live wallpaper (nama jelas di Task Manager)
if "%ERR%"=="0" (
  dotnet publish "LiveWallpaper\DynamicIsland.LiveWallpaper.csproj" -c Release -r win-x64 --self-contained true -nologo -nodeReuse:false ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=none -p:UseSharedCompilation=false -o "%OUT%" >> "%~dp0paket-log.txt" 2>&1
  set "ERR=!errorlevel!"
)
dotnet build-server shutdown >nul 2>nul
if not "%ERR%"=="0" (
  echo GAGAL membuat paket. Detail ada di paket-log.txt
  findstr /i /c:"error" "%~dp0paket-log.txt"
  pause
  exit /b 1
)

rem --- pelengkap: ekstensi Chrome, pemulih taskbar, petunjuk ---
xcopy /e /i /q /y "%~dp0chrome-extension" "%OUT%\chrome-extension" >nul
(
  echo @echo off
  echo rem Mengembalikan taskbar Windows seperti semula ^(kalau Dynamic Island ditutup paksa^)
  echo taskkill /im DynamicIsland.exe /f ^>nul 2^>nul
  echo taskkill /im DynamicIsland.LiveWallpaper.exe /f ^>nul 2^>nul
  echo "%%~dp0DynamicIsland.exe" --restore-taskbar
  echo echo Taskbar Windows sudah dikembalikan.
  echo timeout /t 3 ^>nul
) > "%OUT%\kembalikan-taskbar.bat"
(
  echo DYNAMIC ISLAND untuk Windows 11
  echo ===============================
  echo.
  echo 1. Ekstrak zip ini ke folder mana saja, misalnya Documents\DynamicIsland.
  echo 2. Klik dua kali DynamicIsland.exe.
  echo    Kalau muncul "Windows protected your PC", klik More info lalu Run anyway.
  echo 3. Pengaturan: klik kanan notch atau dock, lalu pilih Pengaturan.
  echo    Bahasa bisa diganti ke English di Pengaturan, bagian Tampilan.
  echo.
  echo Dock menyembunyikan taskbar Windows. Taskbar kembali sendiri saat aplikasi ditutup.
  echo Kalau taskbar tidak muncul lagi, jalankan kembalikan-taskbar.bat.
  echo.
  echo Persen download yang tepat di Chrome: pasang ekstensi di folder chrome-extension
  echo ^(petunjuk ada di chrome-extension\CARA-PASANG.txt^).
) > "%OUT%\BACA-DULU.txt"

powershell -NoProfile -Command "Compress-Archive -Path '%OUT%' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
  echo Paket ada di folder paket\DynamicIsland, tapi zip gagal dibuat.
  pause
  exit /b 1
)
rmdir /s /q "%~dp0paket"
echo.
echo SELESAI. File siap dibagikan:
echo   %ZIP%
echo.
explorer /select,"%ZIP%"
pause
