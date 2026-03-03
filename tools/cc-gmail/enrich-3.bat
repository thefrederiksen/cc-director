@echo off
cd /d "%~dp0"
python enrich_contacts.py --reset --limit 3 --account personal
pause
