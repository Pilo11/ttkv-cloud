# ttkv-cloud

ASP.NET Core Razor Pages app for TTKV. No authentication yet.

The home page is a white shell with a Home icon in the top menu and an iframe that loads click-TT admin (`http://ttvn.click-tt.de/admin`) through a same-origin reverse proxy at `/click-tt/...`.

A cross-origin iframe cannot be filled or clicked by page buttons (browser same-origin policy). The proxy exists so later controls above the iframe can use `iframe.contentDocument` on the embedded page. Login traffic therefore passes through this app.

click-TT admin redirects to click-TT ID on `ttde-id.liga.nu`. That OAuth client only allows `redirect_uri=https://ttvn.click-tt.de/.../oAuthLogin`. The proxy keeps that official callback in the authorize request, serves the ID login itself under `/liga-id/...`, then sends the code callback back through `/click-tt/...` so the iframe stays on localhost.

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
