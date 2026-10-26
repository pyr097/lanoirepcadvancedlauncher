@echo off
setlocal
cd /d "%~dp0"
dotnet publish -c Release -o "%~dp0_pub" || (echo Build failed. & pause & exit /b 1)
if not exist dist mkdir dist
copy /y "_pub\LANoireLauncher.exe" "dist\L.A. Noire Advanced Launcher.exe" >nul
rmdir /s /q _pub bin obj
echo.
echo Done: dist\L.A. Noire Advanced Launcher.exe
pause
