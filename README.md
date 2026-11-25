# ttkv-cloud

ASP.NET Core Razor Pages app for TTKV. No authentication yet.

## Pages

- **Startseite** (`/`): short German overview of the menu buttons.
- **myTT-Verwaltung** (`/mytt`): click-TT admin in an iframe (`http://ttvn.click-tt.de/admin`) through a same-origin reverse proxy at `/click-tt/...`, plus CSV Begegnungen import.
- **Terminplanung** (`/termine`, public): appointment matrix per Spielplan (date × home team). A Mannschaftsführer picks their team, offers home Termine on any non-blocked day and books free Termine of other teams as guest. Bookings are final for them; one booked match per team per day.
- **Staffelleitung** (`/staffelleitung/{key}`, secret): create and edit Spielpläne (name, fixed date range of the Halbserie, gesperrte Tage, Begegnungen CSV with columns `Heim`, `Gast`, optional `Nr`), free or delete booked Termine, and export booked Termine as CSV for the myTT import (`Datum;Uhrzeit;Nr;Heim;Gast`).

### Staffelleitung URL

There is no login. The Staffelleitung pages only answer under a secret key; any other key returns 404, and no public page links to them.

- Set the key with `Termine__AdminKey` (env) or `Termine:AdminKey` (config): at least 16 characters, `A-Z a-z 0-9 - _`.
- Without a configured key, a random key is generated on first start and stored as `staffelleitung.key` in the Termine data directory (persisted in the Docker volume).
- The full path is logged at startup: `Staffelleitung erreichbar unter /staffelleitung/…`.

A cross-origin iframe cannot be filled or clicked by page buttons (browser same-origin policy). The proxy exists so controls above the iframe can use `iframe.contentDocument` on the embedded page. Login traffic therefore passes through this app.

click-TT admin redirects to click-TT ID on `ttde-id.liga.nu`. That OAuth client only allows `redirect_uri=https://ttvn.click-tt.de/.../oAuthLogin`. The proxy keeps that official callback in the authorize request, serves the ID login itself under `/liga-id/...`, then sends the code callback back through `/click-tt/...` so the iframe stays on localhost.

Spielplan data is JSON under `CACHE_ROOT/termine/` (locally `Data/local-cache/termine`). Docker uses a named volume at `/var/lib/cloud-services-cache`.

## Run locally

```bash
dotnet run --project TtkvCloud/CloudServices
```

Then open http://localhost:8080.

## Run with Docker

```bash
docker compose up --build
```

Then open http://localhost:8080.

## Production rollout

The app speaks plain HTTP on port 8080. In production it runs in Docker behind a reverse proxy that terminates HTTPS.

### 1. Prepare the server

- Linux host with Docker and the Compose plugin.
- A DNS name pointing to the server, e.g. `termine.example.de`.
- Outbound HTTPS to `ttvn.click-tt.de` and `ttde-id.liga.nu` (myTT proxy).

### 2. Get the code

```bash
git clone git@github.com:Pilo11/ttkv-cloud.git
cd ttkv-cloud
```

### 3. Create the Staffelleitung key (never commit it)

```bash
echo "TERMINE_ADMIN_KEY=$(openssl rand -base64 24 | tr '+/' '-_' | tr -d '=')" > .env
chmod 600 .env
```

`.env` is git-ignored. Without a key the app generates one into the data volume and logs it at startup.

### 4. Add server-specific settings

Create `docker-compose.override.yml` next to `docker-compose.yml` (git-ignored, merged automatically by `docker compose`):

```yaml
services:
  cloud-services:
    ports: !override
      - "127.0.0.1:8080:8080"   # only the reverse proxy may reach the app
    environment:
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"   # trust X-Forwarded-Proto → https links and Secure cookies
      AllowedHosts: termine.example.de
      Termine__AdminKey: ${TERMINE_ADMIN_KEY}
    volumes:
      - dataprotection_keys:/root/.aspnet/DataProtection-Keys   # forms stay valid across redeploys

volumes:
  dataprotection_keys:
```

Without `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, the copied Mannschaftsführer link would start with `http://`, and the click-TT login cookies would lose their `Secure` flag.

### 5. Reverse proxy with HTTPS

Caddy (gets certificates automatically and forwards Host and `X-Forwarded-Proto`):

```
termine.example.de {
    reverse_proxy 127.0.0.1:8080
}
```

For nginx, add `proxy_set_header Host $host;` and `proxy_set_header X-Forwarded-Proto $scheme;`.

### 6. Start and check

```bash
docker compose up -d --build
docker compose logs cloud-services | grep "Staffelleitung erreichbar"
```

- `https://termine.example.de/` and `/termine` load.
- `https://termine.example.de/staffelleitung/<key>` opens the Staffelleitung; any other key gives 404.
- Create a test Spielplan; the copied Mannschaftsführer link starts with `https://`.
- myTT: log in to click-TT inside the iframe.

Share the Staffelleitung URL only with the Staffelleiter.

### Updates and rollback

```bash
git pull
docker compose up -d --build
```

Spielpläne, the key and the data-protection keys live in Docker volumes and survive rebuilds. To roll back: `git checkout <previous commit>` and run `docker compose up -d --build` again.

### Backups

All Spielplan data is in the `cloud_services_cache` volume (`docker volume ls` shows the full name, e.g. `ttkv-cloud_cloud_services_cache`):

```bash
docker run --rm -v ttkv-cloud_cloud_services_cache:/data -v "$PWD":/backup alpine \
  tar czf /backup/ttkv-termine-$(date +%F).tar.gz -C /data .
```

### Security notes

- There is no login. Anyone with the public link can act for any Mannschaft; only the secret key protects the Staffelleitung.
- The repository is public: keep the key in `.env` or the data volume, never in the repo.
- click-TT credentials typed into the myTT iframe pass through this server (same-origin proxy).
