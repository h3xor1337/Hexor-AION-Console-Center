@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Guided-AionAutomationInventory.ps1" %*
set "hexor_exit_code=%ERRORLEVEL%"
if "%HEXOR_INVENTORY_NO_PAUSE%"=="" pause
exit /b %hexor_exit_code%
