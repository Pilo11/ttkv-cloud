#!/usr/bin/env bash
# Rolls a release out to a stage on this host. The workflow Release copies the release assets and
# settings.env into <DEPLOY_PATH>/releases/<version>/, unpacks the bundle there and runs this script
# from it with the stage's secrets as a tar on stdin:
#
#   <DEPLOY_PATH>/releases/<version>/deploy/scripts/rollout.sh production < secrets.tar
#
# 1. waits for the rollout lock, 2. refuses before anything changes (a pre-release, a missing secret,
# the .env of another stack, the tunnel token of another stack on this host, a port another project
# publishes, a DATA_DIR the rollout would delete), 3. loads the image, 4. stops the running version
# and copies DATA_DIR into <DEPLOY_PATH>/backups/, 5. installs deploy/ (keeps secrets/), the secrets
# and .env, 6. ./up.sh, 7. removes old images, releases and backups.
# Never uses root and prints no container log: production data never reaches the Actions log.
set -euo pipefail

stage="${1:-}"
here="$(cd "$(dirname "$0")/.." && pwd -P)"   # <DEPLOY_PATH>/releases/<version>/deploy
release="$(dirname "$here")"
conf="$here/stages/$stage.conf"
[[ "$stage" =~ ^[a-z]+$ && -f "$conf" ]] || { echo "usage: $0 <stage> < secrets.tar" >&2; exit 2; }
value() { sed -n "s/^$1=//p" "$2" | tail -n 1; }
root="$(value DEPLOY_PATH "$conf")"
data_dir="$(value DATA_DIR "$conf")"
project="$(value COMPOSE_PROJECT_NAME "$conf")"
port="$(value HTTP_PORT "$conf")"
version="$(cat "$here/VERSION")"
live="$root/deploy"
secret_names=(termine_admin_key cloudflare_tunnel_token)
step() { echo; echo "== $*"; }

step "rollout of $version to stage $stage ($root)"
[[ "$release" == "$(cd "$root" && pwd -P)/releases/$version" ]] || { echo "error: not unpacked in $root/releases/$version" >&2; exit 1; }
exec 9>"$HOME/.ttkv-cloud-rollout.lock"
flock -w 1800 9 || { echo "error: another rollout has held the lock for 30 minutes" >&2; exit 1; }

# ---------------------------------------------------------------- 2. guards, nothing changes yet
incoming="$(mktemp -d "$release/.secrets.XXXXXX")"
trap 'rm -rf "$incoming"' EXIT
tar -xf - -C "$incoming"
errors=()
me="$(id -u):$(id -g)"
case "$me" in 0:* | *:0) errors+=("the deploy user ($me) must not be root or in group 0") ;; esac
[[ "$version" != *-* ]] || errors+=("$version is a pre-release: production runs releases only")
for name in "${secret_names[@]}"; do
  [[ -s "$incoming/$name" ]] || errors+=("secret $name was not sent")
done
# One tunnel serves one stack: Cloudflare would split the traffic between two connectors.
for other in "$HOME"/*/deploy/secrets/cloudflare_tunnel_token; do
  [[ -f "$other" && "$other" != "$live/secrets/cloudflare_tunnel_token" ]] || continue
  ! cmp -s "$incoming/cloudflare_tunnel_token" "$other" ||
    errors+=("the tunnel token is the one of ${other%/deploy/secrets/*}: every stack needs its own tunnel")
done
for key in COMPOSE_PROJECT_NAME DATA_DIR; do
  [[ ! -f "$live/.env" || "$(value "$key" "$live/.env")" == "$(value "$key" "$conf")" ]] ||
    errors+=("$live/.env has another $key than stage $stage: this folder belongs to another stack")
done
# Step 5 replaces deploy/ and step 7 prunes releases/ and backups/: data in there would be deleted.
# A relative DATA_DIR would be resolved by Compose inside deploy/.
[[ "$data_dir" == /* && "${data_dir%/}" != "${root%/}" ]] ||
  errors+=("DATA_DIR ($data_dir) in deploy/stages/$stage.conf must be an absolute path other than DEPLOY_PATH")
for pruned in "$live" "$root/releases" "$root/backups"; do
  [[ "${data_dir%/}/" != "$pruned/"* ]] || errors+=("DATA_DIR ($data_dir) lies in $pruned, which every rollout replaces or prunes")
done
others="$(docker ps --filter "publish=$port" --format '{{.Label "com.docker.compose.project"}}|{{.Names}}' | grep -v "^$project|" | cut -d '|' -f 2 || true)"
[[ -z "$others" ]] || errors+=("port $port is published by the container(s) $(tr '\n' ' ' <<<"$others"): set another HTTP_PORT in deploy/stages/$stage.conf")
case "$(uname -m)" in x86_64) arch=amd64 ;; aarch64 | arm64) arch=arm64 ;; *) arch="$(uname -m)" ;; esac
images="$release/ttkv-cloud-image-$version-linux-$arch.tar.gz"
[[ -f "$images" ]] || errors+=("release $version has no image for linux/$arch")
if (( ${#errors[@]} )); then
  printf 'error: %s\n' "${errors[@]}" >&2
  echo "Nothing was changed; the running stack is untouched." >&2
  exit 1
fi

# ---------------------------------------------------------------- 3. to 7.
step "load the image"
(cd "$release" && sha256sum -c --quiet SHA256SUMS)
docker load -i "$images"

previous=""
[[ -f "$live/VERSION" ]] && previous="$(cat "$live/VERSION")"
if [[ -x "$live/down.sh" ]]; then
  step "stop ${previous:-the running version}"
  "$live/down.sh"
fi
if [[ -d "$data_dir" ]]; then
  step "copy the data of ${previous:-the running version} into $root/backups"
  backup="$root/backups/$(date -u +%Y%m%dT%H%M%SZ)-${previous:-unknown}.tar.gz"
  (umask 077 && mkdir -p "$root/backups" && tar -czf "$backup" -C "$data_dir" .)
  echo "$backup"
fi

step "install $version"
mkdir -p "$live"
find "$live" -mindepth 1 -maxdepth 1 ! -name secrets -exec rm -rf {} +
(cd "$here" && tar -cf - --exclude=./secrets .) | (cd "$live" && tar -xpf -)
mkdir -p "$live/secrets" "$data_dir"
chmod 0700 "$live/secrets" "$data_dir"
for file in "$incoming"/*; do
  cmp -s "$file" "$live/secrets/${file##*/}" || { cp "$file" "$live/secrets/"; echo "secret ${file##*/} written"; }
done
{
  echo "# Written by deploy/scripts/rollout.sh (stage $stage, release $version); edits are overwritten."
  grep -E '^[A-Z_]+=' "$conf" | grep -v '^DEPLOY_PATH='
  # Only PUBLIC_URL comes from the GitHub environment; it cannot override the stage file.
  grep -E '^PUBLIC_URL=' "$release/settings.env" || true
  echo "DATA_USER=$me"
} > "$live/.env"

step "start $version"
if ! (cd "$live" && ./up.sh); then
  echo "error: the start failed. Container states (the logs stay on the host: cd $live && docker compose logs):" >&2
  (cd "$live" && docker compose ps -a --format 'table {{.Service}}\t{{.State}}\t{{.Status}}') >&2 || true
  echo "Roll back: run the workflow Release by hand with ${previous:-the previous version}; the data before this rollout is in $root/backups." >&2
  exit 1
fi

step "clean up"
docker image ls --format '{{.Repository}}:{{.Tag}}' | grep -E '^ttkv-cloud:' | while IFS= read -r image; do
  # An image a container uses is refused here and kept.
  [[ "${image#*:}" == "$version" || "${image#*:}" == "$previous" ]] || docker image rm "$image" >/dev/null 2>&1 || true
done || true
docker image prune -f >/dev/null
ls -1dt "$root/releases"/*/ 2>/dev/null | tail -n +4 | while IFS= read -r old; do rm -rf "$old"; done || true
ls -1t "$root/backups"/*.tar.gz 2>/dev/null | tail -n +11 | while IFS= read -r old; do rm -f "$old"; done || true
step "stage $stage runs $version"
