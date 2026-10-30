# Plugin di AnimeRoll

Un plugin aggiunge una **fonte** ad AnimeRoll senza modificare il server. Nelle app compare accanto ad
StreamSite, Torrent e Telegram: ricerca, lista episodi, streaming diretto e “Scarica sul NAS”, in base a
cosa implementi. Al resto pensa AnimeRoll: coda dei download, avanzamento nelle app, conversione in MP4,
spostamento in libreria e collegamento ad AniList.

Ci sono due tipi di plugin:

| Tipo | Quando usarlo | Esempio nel repo |
|---|---|---|
| **.NET** (C#) | Logica complessa, librerie NuGet, prestazioni | `AnimeRoll.Server/plugins-sdk/Template` (scheletro), `InternetArchive` |
| **Script** (qualsiasi linguaggio) | Python, Node, bash, binari già esistenti | `AnimeRoll.Server/plugins-sdk/direct-url` |

## Installazione sul NAS

```
/volume1/docker/animeroll/plugins/
├── InternetArchive/        ← output di `dotnet publish` (+ plugin.json opzionale)
└── direct-url/             ← plugin.json + main.py
```

1. Copia la cartella del plugin in `plugins/` (montata come `/plugins` nel container).
2. Metti le impostazioni segrete nel `.env`: `AnimeRoll__Plugins__Settings__<id>__<chiave>=valore`.
3. Dalla cartella `AnimeRoll.Server`: `docker compose up -d animeroll`. Ricrea il container e rilegge il `.env`;
   `docker compose restart` non basta se hai cambiato il `.env`.
4. Controlla lo stato: `GET /api/plugins` (oppure `docker logs animeroll | grep -i plugin`).

Le cartelle che iniziano con `_` o `.` vengono ignorate: rinomina `foo` in `_foo` per disattivare un plugin.
Puoi anche mettere `"enabled": false` nel suo `plugin.json`.

## Variabili d'ambiente nel .env

Una riga per impostazione, nel `.env` accanto a `docker-compose.yml`:

```bash
AnimeRoll__Plugins__Settings__<id-plugin>__<chiave>=valore
```

- `__` (due trattini bassi) separa i livelli. `<id-plugin>` è l'`Id` del plugin, non il nome della cartella;
  il trattino nell'id va bene.
- La chiave non distingue maiuscole e minuscole. Nei plugin script arriva anche come
  `ANIMEROLL_SETTING_<CHIAVE>` (maiuscolo, `-` → `_`).
- Il valore va scritto così com'è: niente spazi attorno a `=`, gli spazi interni vanno bene.
- `$` viene interpretato da Docker Compose: scrivi `$$` oppure metti il valore tra apici singoli `'…'`.
- ` #` (spazio + cancelletto) inizia un commento: valori con `#` tra apici singoli.
- Il `.env` vince su `plugin.json` → `settings` per la stessa chiave.
- Dopo una modifica: `docker compose up -d animeroll` (non `restart`).

## Cosa può fare un plugin

| Capacità | Metodo .NET | Azione script | Nelle app |
|---|---|---|---|
| *(sempre)* download | `DownloadAsync` | `download` | “Scarica sul NAS” |
| `Search` | `SearchAsync` | `search` | campo di ricerca |
| `Episodes` | `GetEpisodesAsync` | `episodes` | lista episodi con selezione multipla |
| `Stream` | `ResolveStreamUrlAsync` | `stream` | “Guarda ora” senza scaricare |

Un plugin senza `Search` mostra nelle app un campo **“Link o riferimento”** (con numero episodio opzionale).
`RefPattern`, se impostato, è la regex che il riferimento deve rispettare: le app disattivano il pulsante
e il server rifiuta il download se non corrisponde.

## Plugin .NET

1. Copia `plugins-sdk/Template` e rinomina progetto e classe.
2. Implementa `ISourcePlugin` (vedi i `TODO` in `MyServicePlugin.cs` e i commenti XML in
   `src/AnimeRoll.Plugins.Abstractions/ISourcePlugin.cs`).
3. Compila: `dotnet publish -c Release -o out/MioPlugin` e copia `out/MioPlugin` in `plugins/`.

```csharp
public sealed class MioPlugin : ISourcePlugin
{
    public PluginInfo Info { get; } = new("mio-servizio", "Mio servizio",
        PluginCapabilities.Search | PluginCapabilities.Episodes);

    public Task InitializeAsync(PluginContext ctx, CancellationToken ct) { /* impostazioni, login */ }
    public Task<IReadOnlyList<PluginSearchResult>> SearchAsync(string query, CancellationToken ct) { … }
    public Task<IReadOnlyList<PluginEpisode>> GetEpisodesAsync(string seriesRef, CancellationToken ct) { … }
    public Task<IReadOnlyList<string>> DownloadAsync(PluginDownloadRequest req, IPluginDownloadContext ctx, CancellationToken ct) { … }
}
```

Regole:
- Il progetto referenzia `AnimeRoll.Plugins.Abstractions` con `<Private>false</Private>` e
  `<ExcludeAssets>runtime</ExcludeAssets>` (già impostato nel template): la DLL la fornisce il server.
- Le dipendenze NuGet del plugin vengono caricate in un contesto isolato: possono avere versioni diverse
  da quelle del server.
- La classe deve avere un costruttore senza parametri. Più classi `ISourcePlugin` nella stessa DLL = più fonti.
- `PluginContext` ti dà: `Settings`, `GetSetting`/`RequireSetting`, `Http` (HttpClient condiviso),
  `DataDirectory` (cartella persistente in `/data/plugins/<id>` per token e cache), `Log(...)`.
- Se `InitializeAsync` lancia un'eccezione, la fonte resta visibile come “Non disponibile: <messaggio>”.

## Plugin script

`plugin.json` (obbligatorio):

```jsonc
{
  "type": "script",
  "id": "mio-script",                 // minuscole, cifre e '-'
  "name": "Mio script",
  "version": "1.0.0",
  "command": ["python3", "main.py"],  // eseguito dalla cartella del plugin; python3 è nell'immagine
  "capabilities": ["search", "episodes", "stream"],  // ometti quelle che non implementi
  "refPattern": "^https?://",         // facoltativo
  "hint": "Testo d'aiuto mostrato nelle app",
  "settings": { "quality": "1080p" }  // solo valori non segreti
}
```

AnimeRoll esegue `<command> <azione>` e scrive **una riga JSON** su stdin:

```json
{
  "action": "download",
  "pluginDirectory": "/plugins/mio-script",
  "dataDirectory": "/data/plugins/mio-script",
  "settings": { "quality": "1080p" },
  "query": "…",                 // search
  "ref": "…",                   // episodes, stream, download
  "seriesTitle": "…", "episodeNumber": 3, "aniListId": 154587,   // download
  "outputDirectory": "/data/work/<job>"                          // download
}
```

Le impostazioni arrivano anche come variabili d'ambiente `ANIMEROLL_SETTING_<CHIAVE>` (maiuscolo, `-` → `_`).

Lo script risponde con **righe JSON** su stdout (le righe non JSON finiscono nel log):

| Riga | Quando |
|---|---|
| `{"type":"result","data":[…]}` | `search`, `episodes` (array) e `stream` (stringa URL o `null`) |
| `{"type":"progress","fraction":0.42,"message":"120 MB"}` | durante `download` |
| `{"type":"file","path":"ep03.mkv"}` | per ogni video prodotto (relativo a `outputDirectory`) |
| `{"type":"log","message":"…"}` | messaggi per il log del container |
| `{"type":"error","message":"…"}` | errore mostrato all'utente; poi esci con codice ≠ 0 |

Formati dei risultati:

```json
// search
[{ "ref": "id-serie", "title": "Titolo", "posterUrl": "https://…", "subtitle": "2024 · 12 ep", "aniListId": 154587 }]
// episodes
[{ "ref": "id-episodio", "number": 1, "title": "Episodio 1", "aniListId": 154587 }]
```

Azioni sconosciute: esci con codice 0 senza output. `init` viene chiamata all'avvio (30 s di tempo):
emetti un `error` per segnare la fonte come non disponibile. `search`, `episodes` e `stream` hanno 60 s;
`download` non ha limite e si annulla dalle app.

## Regole valide per tutti

- **Dove scrivere**: i file scaricati vanno solo in `outputDirectory`. Percorsi fuori da quella cartella
  vengono rifiutati, così un plugin non può importare file arbitrari dal NAS.
- **Formati**: va bene qualsiasi formato letto da ffmpeg. MKV e AVI vengono convertiti in MP4 copiando i
  flussi; H.264 a 10 bit viene ricodificato perché i dispositivi Apple non lo riproducono.
- **AniList**: se conosci l'ID, restituiscilo (`aniListId`) e la libreria si collega da sola; altrimenti
  vale quello della pagina da cui l'utente ha avviato il download.
- **Segreti** (password, token, cookie) solo nel `.env`, mai in `plugin.json`.
- **Uso responsabile**: i plugin girano con i permessi del server, sul tuo NAS. Cosa scarichi e da dove
  è sotto la tua responsabilità: rispetta i termini dei servizi e le leggi sul diritto d'autore.
  AnimeRoll non include e non supporta strumenti per aggirare protezioni DRM.

## Provare un plugin in locale

```bash
cd AnimeRoll.Server
dotnet publish plugins-sdk/InternetArchive -c Release -o /tmp/ar-plugins/InternetArchive
cp -R plugins-sdk/direct-url /tmp/ar-plugins/
AnimeRoll__Plugins__Directory=/tmp/ar-plugins AnimeRoll__DataPath=./.data AnimeRoll__MediaPath=./.media \
  AnimeRoll__Jwt__Key=$(openssl rand -hex 32) dotnet run --project src/AnimeRoll.Server.Api
```

Uno script si può provare anche da solo:

```bash
echo '{"query":"felix"}' | python3 main.py search
```
