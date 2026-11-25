# Deployment: production on a server, behind a Cloudflare Tunnel

The root `docker-compose.yml` is the local quick start. This folder is the production stack:

- **Cloudflare Tunnel is the only public entry.** `cloudflared` (profile `tunnel`) dials out to
  Cloudflare and forwards the public host name to `app:8080`. The server opens no public port; TLS
  ends at Cloudflare. The app is also on `127.0.0.1:18090` for checks on the server.
- **Secrets are files**, never environment variables or `.env`: `secrets/termine_admin_key` (the
  Staffelleitung key, read by the app as `Termine:AdminKey`) and `secrets/cloudflare_tunnel_token`.
  On the server the rollout writes them from the GitHub environment `production`.
- **All data lives in `DATA_DIR`** (`/home/server/ttkv-cloud/data`): the Spielpläne (`cache/termine`)
  and the data-protection keys. The app runs as the deploy user, never root, read-only otherwise.
  These are bind mounts of host directories, not Docker volumes: `docker compose restart`,
  `./down.sh` + `./up.sh`, even `docker compose down -v` or `docker volume prune` leave them alone,
  and the read-only container has nowhere else to keep anything. The rollout refuses a `DATA_DIR`
  inside `deploy/` or `releases/`, which it replaces or prunes.
- **Releases**: only a pull request merged into `main` releases. The workflow `Merged` starts
  `Release`, which runs the CI on the merge commit, builds the image `ttkv-cloud:<version>`, keeps it
  in a GitHub release (no registry), copies it over SSH to the server and runs
  `scripts/rollout.sh production` there. There is no beta stage.

| Command | What it does |
| --- | --- |
| `./up.sh` | Every start. A checkout builds the image and generates a missing key; a release bundle starts its loaded image |
| `./down.sh` | Stops; `DATA_DIR` and `secrets/` stay |
| `scripts/rollout.sh production` | Run by the workflow on the server: guards, load, stop, install, start, clean up |

Values of the stack on the server (path, project, port) are code in `stages/production.conf`.

## Try it locally

```bash
cd deploy
./up.sh      # builds, generates secrets/termine_admin_key -> http://127.0.0.1:18090
./down.sh    # data stays in deploy/data
```

## First deployment, step by step

| Where | What |
| --- | --- |
| Your workstation | the SSH key pair, the Staffelleitung key (then in your password manager) |
| Cloudflare dashboard | the tunnel `ttkv-cloud`, its route and token |
| The server | Docker, the user `server`, the public SSH key. Everything else is written by the rollout |
| GitHub, environment `production` | everything the release needs: SSH, URL, tunnel token, Staffelleitung key, who may deploy |
| GitHub, repository secret | `RENOVATE_TOKEN` only |

### 1. The server (once)

- Linux on amd64, Docker Engine with the Compose plugin, usable by `server` without `sudo` (group
  `docker`), `flock` and `curl` (both part of every Debian/Ubuntu).
- SSH with key authentication, reachable from GitHub's runners (no fixed addresses).
- `127.0.0.1:18090` free. Another user than `server` or another port: change
  `stages/production.conf` (`DEPLOY_PATH`, `DATA_DIR`, `HTTP_PORT`) in the same pull request.
- HTTPS out to Docker Hub (`cloudflare/cloudflared`), `ttvn.click-tt.de` and `ttde-id.liga.nu`.

**Already running by hand** (the old `git clone` + `docker compose up` setup in `~/ttkv-cloud`)? Move
it out of the way and take its Spielpläne along, before the first release:

```bash
cd ~/ttkv-cloud && docker compose down && cd ~ && mv ttkv-cloud ttkv-cloud-manual
mkdir -p ~/ttkv-cloud/data/cache && chmod 0700 ~/ttkv-cloud/data ~/ttkv-cloud/data/cache
docker run --rm --user "$(id -u):$(id -g)" -v ttkv-cloud_cloud_services_cache:/from:ro \
  -v ~/ttkv-cloud/data/cache:/to alpine:3.22 cp -R /from/. /to/
grep TERMINE_ADMIN_KEY ~/ttkv-cloud-manual/.env   # keep this key (step 4) so the Staffelleitung URL stays
```

### 2. Cloudflare: a tunnel of its own

The domain's DNS must be on Cloudflare. Use a host name one level below the domain
(`termine.example.org`, not `termine.ttkv.example.org`): the free certificate covers only `*.example.org`.

Zero Trust → Networks → Tunnels → Create a tunnel → Cloudflared, name `ttkv-cloud`:

1. **Token:** the install command it shows ends in a long string (`cloudflared service install
   eyJh…`). Copy only that string. Install nothing: the stack runs the connector.
2. **Route** (public hostname / published application): your host name, no path, service type
   **HTTP**, URL **`app:8080`**. Cloudflare creates the DNS record.

Never reuse another stack's tunnel (dfzh's): Cloudflare would split the traffic between both
connectors. The rollout refuses a token another stack on the server already uses.

### 3. SSH key for the workflow

```bash
ssh-keygen -t ed25519 -N '' -C ttkv-cloud-release -f ttkv-cloud-release
ssh-keyscan -t ed25519 <server> > known_hosts      # add -p <port> for another port
ssh-keygen -lf known_hosts                         # must match, on the server:
                                                   #   ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

On the server, as `server`, append one line to `~/.ssh/authorized_keys`: `restrict ` followed by the
content of `ttkv-cloud-release.pub`.

### 4. The Staffelleitung key

```bash
openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | head -c 32; echo
```

Or keep the one already in use (step 1). At least 24 characters of `A-Z a-z 0-9 - _`. It is the
Staffelleitung URL: `https://<host>/staffelleitung/<key>`. A new key makes the old URL a 404.

### 5. GitHub settings

Settings → Environments → New environment **`production`**:

| Name | Kind | Value |
| --- | --- | --- |
| Deployment branches and tags | rule | Selected branches and tags: `main` |
| `PUBLIC_URL` | variable | `https://termine.example.org` (the route's host, no trailing slash) |
| `DEPLOY_SSH_PORT` | variable | only when not 22 |
| `DEPLOY_SSH_HOST` | secret | the server's host name or IP |
| `DEPLOY_SSH_USER` | secret | `server` |
| `DEPLOY_SSH_KEY` | secret | the whole file `ttkv-cloud-release` (private key) |
| `DEPLOY_SSH_KNOWN_HOSTS` | secret | the whole file `known_hosts` |
| `CLOUDFLARE_TUNNEL_TOKEN` | secret | the token from step 2 |
| `TERMINE_ADMIN_KEY` | secret | the key from step 4 |

Settings → Secrets and variables → Actions → Repository secrets: `RENOVATE_TOKEN`, a fine-grained
token for this repository (permissions: `.github/workflows/renovate.yml`). Settings → General →
Features: Issues on (Renovate's Dependency Dashboard).

Settings → Rules → Rulesets: a branch ruleset for `main` (require a pull request, block force
pushes and deletions) and a tag ruleset for `v*` (restrict updates and deletions).

The release's job `settings` checks all of this on every run and stops before anything is built,
listing what is missing or malformed (never a value).

### 6. First release

Merge a pull request into `main`: `v1.0.0`. The first rollout creates `/home/server/ttkv-cloud`
(`deploy/`, `data/`, `releases/`), writes the secrets and `.env` and starts the stack.
Then check `https://<host>/`, the Staffelleitung URL (its menu also leads to the myTT-Verwaltung,
`/staffelleitung/<key>/mytt`), and that a copied Mannschaftsführer link starts with `https://`.

## Releases, day to day

- Every pull request merged into `main` is a release (`1.0.1`, …); `+semver: minor` or
  `+semver: major` in a commit message bumps more. A release only moves forward.
- The newest five releases stay on GitHub, the newest three on the server.
- **Redeploy or roll back:** Actions → Release → Run workflow (branch `main`), the version (empty:
  the newest).
- **Renovate** opens one pull request with every update into `main` (Mondays, or Actions →
  Renovate → Run workflow). It is never merged automatically; merged, it goes to production.

A rollout waits for the server's rollout lock, refuses before anything changes (a pre-release, a
missing secret, another stack's folder or tunnel token, a port another container publishes), loads
the image, stops the stack, installs the release with its `.env`, starts it and removes old images.
It never touches `DATA_DIR` and makes no backups. A failed start prints the container states only;
the logs stay on the server (`cd ~/ttkv-cloud/deploy && docker compose logs`).
