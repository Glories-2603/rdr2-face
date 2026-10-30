#!/bin/sh
# RDR 2 Face: ./start.sh [--host 0.0.0.0 --password <pw>] [--port 8776] [--data DIR]
cd "$(dirname "$0")"
if [ ! -x venv/bin/python ]; then
    python3 -m venv venv && venv/bin/pip install -q -r requirements.txt || exit 1
fi
chmod +x bin/linux-x64/RdrFaceTool 2>/dev/null
[ "$1" = "--setup-only" ] && exit 0
exec venv/bin/python server.py "$@"
