# AnimeRoll Server

Self-hosted anime catalog with a Netflix-style experience, built to run on a NAS. It keeps your library of
episodes, serves them to the apps, and syncs your progress with [AniList](https://anilist.co).

AnimeRoll is split in four repositories:

| Repository | What it is |
|---|---|
| **AnimeRoll-Server** (this one) | ASP.NET Core 10 server, plugin API, Docker setup, HTTPS reverse proxy |
| [AnimeRoll-Web](https://github.com/KuroZetsubou/AnimeRoll-Web) | React + Vite web client, built into the server image |
| [AnimeRoll-Apple](https://github.com/KuroZetsubou/AnimeRoll-Apple) | iOS / iPadOS / tvOS apps (SwiftUI) |
| [AnimeRoll-Android](https://github.com/KuroZetsubou/AnimeRoll-Android) | Android and Android TV app (Jetpack Compose) |

The apps are in Italian for now.

## Features

- **Library on your NAS**: every episode ends up in `/media/{Series}/{Series} - E01.mp4`, linked to its AniList id.
  Matroska files are remuxed to MP4 with ffmpeg (stream copy, fast). Only video that Apple devices cannot decode
  (10-bit H.264, 4:2:2 / 4:4:4) is re-encoded to 8-bit.
- **Subtitles** are kept next to the video (ASS with libass on every platform, WebVTT fallback), selectable per
  series, with a ±0.25 s offset. Multi-track audio (dubs) is supported on every client.
- **Sources are plugins**: nothing about where episodes come from is hard-coded. The server ships with a Telegram
  source and local file import, and anything else is a plugin: a .NET assembly or a script in any language.
  See [PLUGINS.md](PLUGINS.md).
- **AniList**: connect your account from the profile page. Past 85 % of an episode the server updates your
  progress (never backwards) and marks the series completed on the last episode. You can also edit progress and
  status by hand, see related seasons, and mark bonus episodes as *extra* (they are never counted).
- **Fix mistakes**: move episodes to another AniList entry (wrong season), renumber an episode, repair A/V sync.
- **Offline**: Apple and Android clients download episodes (with subtitles) to the device.
- **Sign-in** with username + password, then passkeys (Face ID / Touch ID / Android biometrics).

## Quick start (Docker)

You need Docker with Compose v2.17+ and a checkout of **both** this repository and
[AnimeRoll-Web](https://github.com/KuroZetsubou/AnimeRoll-Web) next to it:

```
AnimeRoll/
├── AnimeRoll-Server/
└── AnimeRoll-Web/
```

```bash
cd AnimeRoll-Server
cp .env.example .env            # then fill it in, see below
docker compose up -d --build
```

The web client is built into the image from `../AnimeRoll-Web` (override with `ANIMEROLL_WEB=/path/to/web`).

In `.env`:

- `AnimeRoll__Jwt__Key`: a random secret, e.g. `openssl rand -hex 32`
- `AnimeRoll__PublicBaseUrl` and `AnimeRoll__Passkey__*`: the HTTPS address of the server
- `PUID` / `PGID`: the NAS user that owns your media folders
- the `ANIMEROLL_*` paths of the data, media, downloads and plugin folders (see `docker-compose.yml`)

Open `https://your-domain`, create the account, then set `AnimeRoll__AllowRegistration=false` and recreate the container.

**HTTPS is required for passkeys.** The `proxy/` folder contains a ready-made Caddy + DuckDNS stack with
Let's Encrypt certificates, and a Cloudflare Tunnel option; see [proxy/README.md](proxy/README.md).

### AniList

Create a client at <https://anilist.co/settings/developer> with redirect
`https://your-domain/api/anilist/callback`, then set `AnimeRoll__AniList__ClientId` / `ClientSecret`.

### Telegram

Create `api_id` / `api_hash` at <https://my.telegram.org/apps>, put them in `.env` together with your phone
number, then pick the channels to use as a source from the app (Profile → Telegram channels).

## Plugins

A plugin adds a source (search, episode list, direct streaming, download, subtitles) without touching the server.
Write one in C# or in any scripting language: the contract, an SDK template and examples are in
[PLUGINS.md](PLUGINS.md) and `plugins-sdk/`.

`plugins/crunchyroll` is a reference script plugin that only fetches subtitle files (ASS) for episodes you already
have; it does not download video or audio. Its API flow follows
[crunchyroll-downloader](https://github.com/CuteTenshii/crunchyroll-downloader) (MIT), see
`plugins/crunchyroll/THIRD-PARTY-NOTICES.md`.

Plugins are third-party code that runs with the server's permissions: only install ones you trust, and respect the
terms of the services and the copyright of the content you access.

## Development

```bash
AnimeRoll__DataPath=./.data AnimeRoll__MediaPath=./.media AnimeRoll__Jwt__Key=$(openssl rand -hex 32) \
  dotnet run --project src/AnimeRoll.Server.Api
```

Main API groups: `/api/auth/*`, `/api/anilist/*`, `/api/library/*`, `/api/sources/*`, `/api/downloads`,
`/api/telegram/*`. Video is served with range requests on `/media/episodes/{id}?t=…` (short-lived signed token).

### Local-only sources

Source files placed in `src/AnimeRoll.Server.Api/Providers/Private/` (git-ignored) are compiled into your own
build; a file there can implement `LocalProviders.RegisterLocal` to register extra `ISourceProvider`s. The public
build compiles fine without that folder.

## License

© 2026 KuroZetsubou. Licensed under [Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International](LICENSE)
(CC BY-NC-SA 4.0): you may share and adapt it for non-commercial purposes, with credit, and you must distribute your
changes under the same license. It is provided "as is", without warranty, and no patent rights are granted.
This is a source-available license, not an OSI-approved open source license.
