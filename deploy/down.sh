#!/usr/bin/env bash
# Stops the stack (containers and network of both profiles). DATA_DIR and secrets/ stay, so ./up.sh
# afterwards is a plain restart. No script ever deletes DATA_DIR.
set -euo pipefail
cd "$(dirname "$0")"
docker compose --profile tunnel down --remove-orphans "$@"
