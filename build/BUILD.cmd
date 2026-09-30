@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BUILD.ps1" %*
pause
