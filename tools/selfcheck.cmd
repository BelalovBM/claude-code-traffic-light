@echo off
rem Put this file next to ClaudeCodeTrafficLight.exe and double-click it. For about 40 seconds the program opens
rem and photographs its windows with made-up sessions (do not touch the mouse or keyboard meanwhile), then shows
rem the folder with the pictures and report.txt. Nothing is changed on the computer; Claude Code is not needed.
cd /d "%~dp0"
start "" "%~dp0ClaudeCodeTrafficLight.exe" --selfcheck
