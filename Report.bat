@echo off
rem Writes Shogun2-8Player-Report.txt (plain text) to the Desktop for a bug report.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0report.ps1" %*
pause
