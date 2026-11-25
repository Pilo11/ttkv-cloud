# ttkv-cloud

ASP.NET Core Razor Pages app for TTKV. No authentication yet.

## Pages

- **Startseite** (`/`): short German overview of the public menu buttons.
- **Terminplanung** (`/termine`, public): appointment matrix per Spielplan (date × home team). A Mannschaftsführer picks their team, offers home Termine on any non-blocked day and books free Termine of other teams as guest. Bookings are final for them; one booked match per team per day.
- **Staffelleitung** (`/staffelleitung/{key}`, secret): create and edit Spielpläne (name, fixed date range of the Halbserie, gesperrte Tage, Begegnungen CSV with columns `Heim`, `Gast`, optional `Nr`), free or delete booked Termine, and export booked Termine as CSV for the myTT import (`Datum;Uhrzeit;Nr;Heim;Gast`).
- **myTT-Verwaltung** (`/staffelleitung/{key}/mytt`, secret): click-TT admin in an iframe (`http://ttvn.click-tt.de/admin`) through a same-origin reverse proxy at `/click-tt/...`, plus CSV Begegnungen import. `/mytt` no longer exists.

### Staffelleitung URL

There is no login. The Staffelleitung pages (including the myTT-Verwaltung) only answer under a secret key; any other key returns 404, and no public page links to them. The menu shows Staffelleitung and myTT-Verwaltung only on pages already under the key.

- Set the key with `Termine__AdminKey` (env) or `Termine:AdminKey` (config): at least 16 characters, `A-Z a-z 0-9 - _`.
- Without a configured key, a random key is generated on first start and stored as `staffelleitung.key` in the Termine data directory.
- The full path is logged at startup: `Staffelleitung erreichbar unter /staffelleitung/…`.

A cross-origin iframe cannot be filled or clicked by page buttons (browser same-origin policy). The proxy exists so controls above the iframe can use `iframe.contentDocument` on the embedded page. Login traffic therefore passes through this app.

click-TT admin redirects to click-TT ID on `ttde-id.liga.nu`. That OAuth client only allows `redirect_uri=https://ttvn.click-tt.de/.../oAuthLogin`. The proxy keeps that official callback in the authorize request, serves the ID login itself under `/liga-id/...`, then sends the code callback back through `/click-tt/...` so the iframe stays on localhost.

Spielplan data is JSON under `CACHE_ROOT/termine/` (locally `Data/local-cache/termine`). The root `docker-compose.yml` keeps it in the named volume `cloud_services_cache` (a restart keeps it, `docker compose down -v` deletes it); production keeps it in a host directory, see [deploy/README.md](deploy/README.md).

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

## Production

Production runs on a server behind a Cloudflare Tunnel. Every pull request merged into `main` is
released (GitHub release `v<x.y.z>`) and rolled out over SSH; there is no beta stage. Secrets
(SSH, tunnel token, Staffelleitung key) live in the GitHub environment `production`. Renovate opens
one pull request with every dependency update. Setup and day-to-day: [deploy/README.md](deploy/README.md).

## Security notes

- There is no login. Anyone with the public link can act for any Mannschaft; only the secret key protects the Staffelleitung.
- The repository is public: the key lives in the GitHub environment `production` (or the data volume locally), never in the repo.
- click-TT credentials typed into the myTT iframe pass through this server (same-origin proxy).
- The proxy itself (`/click-tt/...`, `/liga-id/...`) is not behind the key: anyone can reach the click-TT login through it, and click-TT's own login still protects the data. It drops the path of this app's own pages from the `Referer` it sends upstream, and the myTT page sends no Referer, so the key never reaches click-TT that way. click-TT's scripts do run on this origin inside the iframe.
