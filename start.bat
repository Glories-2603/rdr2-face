@echo off
rem RDR 2 Face (http://localhost:8776)
cd /d "%~dp0"
python -c "import numpy, scipy" 2>nul || python -m pip install -r requirements.txt
start "" http://localhost:8776
python server.py %*
