# Reverse proxy + DuckDNS (HTTPS e passkey)

Le passkey funzionano solo su un dominio HTTPS. Questo stack mette davanti al NAS un Caddy con certificato
Let's Encrypt, sempre e solo quello (DNS-01 via DuckDNS, quindi anche i sottodomini) e tiene aggiornato l'IP pubblico su DuckDNS.
Serve a tutti i servizi esposti: AnimeRoll è `https://<nome>.duckdns.org`, gli altri sono sottodomini (`ats.<nome>.duckdns.org`) e si aggiungono nel `Caddyfile`.

## 1. Router
Inoltra la porta esterna **TCP 443 → porta 18443 del NAS** (e 80 → 18080, per il redirect): le 80/443 del NAS sono occupate dall'interfaccia del sistema. Niente altro: la 8080 di AnimeRoll resta solo in rete locale.

## 2. Avvio
```bash
cd /volume1/docker/proxy        # copia qui la cartella proxy/
cp .env.example .env            # DUCKDNS_TOKEN, DUCKDNS_NAME, ACME_EMAIL
docker compose up -d --build
docker compose logs -f caddy    # alla prima richiesta ottiene il certificato (30-60 s)
```

## 3. AnimeRoll (.env del server)
```
AnimeRoll__PublicBaseUrl=https://<nome>.duckdns.org
AnimeRoll__Passkey__ServerDomain=<nome>.duckdns.org
AnimeRoll__Passkey__Origins__0=https://<nome>.duckdns.org
AnimeRoll__Apple__AppIds__0=<TEAMID>.com.kurozetsubou.animeroll
AnimeRoll__Apple__AppIds__1=<TEAMID>.com.kurozetsubou.animeroll.tv
AnimeRoll__Android__Sha256CertFingerprints__0=AB:CD:…
```
poi `docker compose up -d` nella cartella del server. Verifica: `https://<nome>.duckdns.org/.well-known/apple-app-site-association`.

## 4. App
- iOS/tvOS: Associated Domains `webcredentials:<nome>.duckdns.org` (già nei file `*.entitlements`); nell'app, "Indirizzo NAS" = `https://<nome>.duckdns.org`.
- Android: stesso indirizzo; `assetlinks.json` lo serve il server con l'impronta del certificato di firma.
- Le passkey esistenti vanno ricreate: sono legate al dominio. Registrale da Profilo dopo il primo accesso con password.

## Sicurezza
Il login è limitato a 10 tentativi al minuto per indirizzo. Tieni `AnimeRoll__AllowRegistration=false`.
