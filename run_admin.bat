@echo off
setlocal
set SCRIPT_DIR=%~dp0
set ARGSTR=%*

rem Launch admin PowerShell to run dotnet; keep window open
powershell -NoProfile -Command ^
  "Start-Process -FilePath powershell.exe -Verb RunAs -ArgumentList @('-NoProfile','-Command','cd ''%SCRIPT_DIR%''; dotnet run -- %ARGSTR%; Write-Host \"`nDone. Press Enter to exit\"; Read-Host')"

endlocal
