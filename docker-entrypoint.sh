#!/bin/sh
# Makes the data folder usable on any host before starting the app, so a fresh
# install needs no manual chown. The app then runs as PUID:PGID (Unraid's
# nobody:users, 99:100, by default), never as root.
set -e

DATA_DIR="$(dirname "${MONEYSITE_DB_PATH:-/data/money.db}")"
PUID="${PUID:-99}"
PGID="${PGID:-100}"

if [ "$(id -u)" = "0" ]; then
    mkdir -p "$DATA_DIR"
    chown -R "$PUID:$PGID" "$DATA_DIR"
    exec setpriv --reuid="$PUID" --regid="$PGID" --clear-groups dotnet MoneySite.dll "$@"
fi

# Started with --user: run as that user and rely on the folder already being writable.
exec dotnet MoneySite.dll "$@"
