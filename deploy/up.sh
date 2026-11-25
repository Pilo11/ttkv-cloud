#!/usr/bin/env bash
# Starts or updates the stack: the first start and every later one (after ./down.sh, a rollout or a
# reboot). The data in DATA_DIR and secrets/ are reused, nothing is asked for. The app answers on
# http://127.0.0.1:HTTP_PORT; COMPOSE_PROFILES=tunnel (.env on the server) adds the Cloudflare Tunnel.
# A checkout builds the image and generates a missing Staffelleitung key; a release bundle (VERSION)
# starts the image its rollout loaded, with the secrets the rollout wrote, and never generates one.
set -euo pipefail
cd "$(dirname "$0")"

command -v docker >/dev/null 2>&1 || { echo "docker is required" >&2; exit 1; }
docker info >/dev/null 2>&1 || { echo "The Docker daemon is not running." >&2; exit 1; }
me="$(id -u):$(id -g)"
case "$me" in 0:* | *:0) echo "error: run as a normal user, never root or group 0" >&2; exit 1 ;; esac

# The shell's value, else .env's (the rollout writes it on the server), else the default.
setting() {
  local value="${!1:-}"
  [[ -n "$value" || ! -f .env ]] || value="$(sed -n "s/^$1=//p" .env | tail -n 1)"
  echo "${value:-$2}"
}
data_dir="$(setting DATA_DIR ./data)"
DATA_USER="$(setting DATA_USER "$me")"
export DATA_USER
[[ "$DATA_USER" == "$me" ]] || { echo "error: DATA_USER is $DATA_USER, but this is $me: start the stack as its deploy user" >&2; exit 1; }
if [[ -f VERSION ]]; then
  TTKV_VERSION="$(cat VERSION)" build=--no-build
  export TTKV_VERSION
else
  build=--build
fi

# Created here as the deploy user, so Docker never creates them as root.
mkdir -p "$data_dir/cache" "$data_dir/dataprotection-keys"
chmod 0700 "$data_dir/cache" "$data_dir/dataprotection-keys"

# The directory keeps other host users out; the files are 0644 because cloudflared reads its token as
# its own user (65532) through the bind mount.
mkdir -p secrets
chmod 0700 secrets
if [[ ! -s secrets/termine_admin_key ]]; then
  if [[ -f VERSION ]]; then
    echo "error: secrets/termine_admin_key is missing; on the server the rollout writes it from the GitHub environment production" >&2
    exit 1
  fi
  openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | head -c 32 > secrets/termine_admin_key
  echo "created secrets/termine_admin_key"
fi
profiles="$(setting COMPOSE_PROFILES "")"
if [[ ",$profiles," == *,tunnel,* && ! -s secrets/cloudflare_tunnel_token ]]; then
  echo "error: profile tunnel without secrets/cloudflare_tunnel_token" >&2
  exit 1
fi
chmod 0644 secrets/*

docker compose up -d "$build" --wait --wait-timeout 180 "$@"

echo
echo "Stack is up: http://127.0.0.1:$(setting HTTP_PORT 18090) (Staffelleitung: /staffelleitung/\$(cat secrets/termine_admin_key))"
[[ ",$profiles," != *,tunnel,* ]] || echo "Public through the Cloudflare Tunnel: $(setting PUBLIC_URL '<PUBLIC_URL>')"
