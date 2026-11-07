@echo off
rem Mengembalikan taskbar Windows seperti semula (kalau Dynamic Island ditutup paksa / crash).
taskkill /im DynamicIsland.exe /f >nul 2>nul
"%~dp0bin\Release\net8.0-windows10.0.22621.0\DynamicIsland.exe" --restore-taskbar
echo Taskbar Windows sudah dikembalikan.
timeout /t 3 >nul
