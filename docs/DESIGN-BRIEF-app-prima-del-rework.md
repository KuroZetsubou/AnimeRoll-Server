# AnimeRoll: com'era l'app prima del rework UI (brief per Design)

Catalogo anime personale "stile Netflix" ospitato sul NAS dell'utente, con progresso sincronizzato su AniList. Un solo utente (o pochi), un server privato, nessuno store pubblico di contenuti.

## Piattaforme
- iPhone / iPad / iPhone Duo pieghevole (SwiftUI), Apple TV (SwiftUI), Android + launcher Android TV (Compose), Web (React).
- Tema: scuro, sfondo quasi nero (#0F0F14), accento arancione (#FF7333). Lingua: italiano.

## Navigazione (iOS, la più completa)
Tab bar a 5 voci: **Home · Cerca · Libreria · Download · Profilo**.
Android: tab simili (Home, Cerca, Libreria, Download, Profilo/logout). Su iPad e Duo aperto la Home è a due pannelli (catalogo + dettaglio con player).

## Schermate e cosa fanno

### Login
- Logo "AnimeRoll" + "Il tuo catalogo anime sul NAS".
- Campi: indirizzo NAS (https://…), username, password. Bottone Accedi, "Accedi con passkey" (Face ID/Touch ID), "Primo avvio? Crea l'account".

### Home (scaffali orizzontali di poster)
1. Continua a guardare (episodio in corso, con "Episodio N")
2. Stai guardando su AniList
3. Sul tuo NAS (serie recenti, badge "N ep")
4. Questa stagione
5. Di tendenza
- In alto una card in evidenza con "Scopri" (iPad/Duo: pannello dettaglio a fianco).
- Tocco su un poster apre il dettaglio.

### Cerca
- Campo "Cerca": risultati della libreria NAS e di AniList mescolati in griglia.

### Libreria
- Griglia delle serie presenti sul NAS (titolo, anno, info episodi).
- Azione "Importa" (scansiona `/media/_inbox`). Stato vuoto: "Scarica qualcosa sul NAS o metti i file in /media/_inbox e premi Importa".

### Dettaglio titolo
- Banner/copertina, titolo, riga meta (anno, generi, episodi), descrizione.
- Stato AniList ("AniList: guardati X/Y") o invito "Collega AniList dal Profilo".
- Bottone primario: "Riprendi E5" / "Guarda E1". Se non è sul NAS: "Non ancora sul NAS" + **Trova episodi**.
- Lista **Episodi**: numero, titolo, durata, dimensione file, fonte, spunta "Visto", progresso.
- Per episodio: scarica sul dispositivo (freccia), annulla, "Rimuovi dal dispositivo"; menu "Scarica tutti sul dispositivo"; menu stato AniList; "Altri episodi".

### Fonti ("Trova episodi", sheet)
- Selettore fonte (Telegram, plugin installati).
- Campo ricerca → "Risultati" → episodi della fonte con selezione (Tutti/Nessuno).
- Campo "Link o riferimento" manuale, numero episodio facoltativo.
- Azioni: **Scarica sul NAS (N)**, oppure guarda subito in streaming. Messaggio di conferma "Inviati N download al NAS".

### Download
- Controllo a due segmenti: **Sul NAS** / **Su questo dispositivo**.
- Sul NAS: coda lavori (serie, episodio, fonte, stato Queued/Running/PostProcessing/Completed/Failed, progresso). Azioni: Annulla, Riprova, Pulisci (rimuove i finiti). Stato vuoto: "Dalla pagina di un anime tocca Trova episodi…".
- Su questo dispositivo: episodi salvati offline, spazio usato, tocco per guardarli, Elimina.
- Il server in realtà supporta di più (pausa, riprendi, avvia subito, priorità, pausa/riprendi tutto) ma alcuni client non lo mostravano.

### Player
- Nativo (AVPlayer / ExoPlayer): sottotitoli, PiP, AirPlay (iOS), ripresa da dove eri, salto ±10 s, salto 85 s (sigla), "Prossimo episodio" ai titoli di coda.
- iOS: titolo + sottotitolo, tempo trascorso/rimanente, lista episodi a scomparsa. Duo: modalità tavolo e libro.
- Il progresso viene inviato al server; oltre l'85% il server aggiorna AniList (mai all'indietro, "Completato" all'ultimo episodio).

### Profilo / Impostazioni
- Account: utente, server.
- AniList: Collega / Scollega (nota sull'85%).
- Passkey: elenco con ultimo uso, Aggiungi passkey, Elimina.
- Fonti → Telegram: invio codice, codice/2FA, interruttori per i canali da usare, Salva.
- Esci.

## Flussi chiave
1. Scoprire: Home/Cerca → dettaglio → Riprendi/Guarda.
2. Procurarsi un episodio: dettaglio → Trova episodi → scegli fonte → Scarica sul NAS → segui in Download → compare in Libreria/Sul tuo NAS.
3. Guardare offline: dettaglio → freccia download sul dispositivo → Download › Su questo dispositivo.
4. Tenere traccia: guardi oltre l'85% → AniList si aggiorna da solo.

## Dati disponibili (cosa il design può usare davvero)
Serie: titolo, titolo romaji, poster, banner, generi, anno, n. episodi, collegamento AniList. Episodio: numero, titolo, durata, dimensione, fonte, posizione, completato. AniList: copertina, banner, descrizione, generi, stato, progresso, liste (in corso, pianificati…), trending, stagione. **Non ci sono** immagini per singolo episodio né un endpoint per rimuovere un titolo dalla lista AniList.

## Punti deboli da risolvere col rework
- Tab bar a 5 voci: Libreria e Cerca quasi duplicati; Download e Profilo poco scopribili.
- Home senza gerarchia forte: card in evidenza debole, scaffali tutti uguali, niente filtro per genere.
- Dettaglio lungo e piatto: azioni sparse, niente separazione tra Episodi e titoli simili.
- Sheet Fonti funzionale ma da "form di sistema": poco guidata.
- Coda Download poco controllabile su Android (solo Annulla); Impostazioni/Telegram/passkey nascoste su Android.
- Player senza identità visiva propria (controlli di sistema).
- Incoerenze tra iOS, Android, tvOS e Web (stessi dati, interfacce diverse).
