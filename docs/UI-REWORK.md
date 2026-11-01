# AnimeRoll – UI rework (iOS + Android)

One design for both apps. iOS stays SwiftUI (`AnimeRoll.Client/iOS`), Android stays
Kotlin + Jetpack Compose (`AnimeRoll.Client/Android`). tvOS comes later.

Inspiration: the layout and motion ideas of github.com/saulsharma/netflix-ui (React Native).
That repo has **no license**: take ideas only, copy no code, assets, fonts, sounds or data.
No Netflix names, logos or images anywhere in the apps.

No profiles / "who is watching" screen: one account per login, as today.
No server changes are needed: every screen uses the existing API.

## Look

| Token | Value | Use |
|---|---|---|
| `accent` | `#FF7333` (today's orange) | primary buttons, progress, selected tab, badges |
| `background` | `#07070C` | every screen |
| `heroTop` | `#202036` → `#11111D` → `#07070C` (stops 0, 0.4, 0.8) | vertical gradient behind the top of Home, ~80% of the screen height |
| `surface` | white 8% | cards, placeholders, chips |
| `surfaceStrong` | white 16% | pressed state, secondary buttons |
| `textSecondary` | white 60% | metadata, captions |

- Type: system font. Screen titles 28 bold; shelf titles 18 bold; card captions 12.
- Corners: posters 6, hero card 12, buttons 6, chips fully rounded.
- Spacing: 16 side margins, 8 between posters, 24 between shelves.
- Haptic tick (light impact) on: tab change, Play, add to device, chip selection.
- Motion: 0.3–0.4 s ease-out; nothing bouncy except the hero press (scale 0.97).

## Navigation

Bottom tabs (3): **Home**, **Novità**, **Il mio AnimeRoll**.
Search and Download are icons in the Home header, not tabs.

- The tab bar slides away when scrolling down past ~90 pt and comes back when scrolling up.
- iOS keeps today's two-pane layout on iPad / unfolded iPhone Duo (sidebar + detail next to the
  list); the three tabs become the sidebar items, plus Search and Download as extra items.

## Screens

### Home
1. **Header** (overlay, pinned): left "Per <username>"; right icons Search and Download.
   Transparent at the top, blurred material from 0 to 90 pt of scroll.
   Under it a row of chips: `Serie` (opens Libreria = series on the NAS), `In corso` (AniList
   "watching" list), `Categorie ▾` (full-screen list of genres, see below).
2. **Hero card**: the first AniList "watching" entry, else the first trending title.
   Not full-bleed: a rounded card 16 pt from the edges, poster/banner image, dark gradient at the
   bottom with title, 3 genres separated by "•", two buttons: `▶ Riproduci` (white, black text)
   and `+ Lista` / `✓ Lista` (surfaceStrong). Plays the next episode if the series is on the NAS,
   otherwise opens the detail.
   - iOS: the image moves a few points with the device tilt (CoreMotion, max ±8 pt), text and
     buttons move slightly the other way: parallax. Off with Reduce Motion.
   - Android: same with the rotation-vector sensor, or skip if it costs too much battery.
3. **Shelves**, in this order, hidden when empty:
   - Continua a guardare (16:9 thumbnails with progress line, ⓘ button opens the detail)
   - Stai guardando su AniList (badge `3/12`)
   - Sul tuo NAS
   - **Top 10 di tendenza**: trending, first 10, each poster with a big outlined rank number
     on its left (number ~100 pt tall, stroked, overlapping the poster by 20%).
   - Questa stagione
   - one shelf per genre found in the NAS library (max 3)

### Categorie (full-screen overlay)
Dark translucent background, centred vertical list of genres in large text, the selected one
bold; a round ✕ button at the bottom. Picking one shows Home filtered to that genre.

### Detail (title page)
Opened as a **sheet over Home**, not a push:
- Home behind it scales to 0.92 and dims.
- Drag down (or right, from the left edge) to close; it follows the finger and closes past ~120 pt.
- Top: banner image 16:9 (slow zoom 1.0 → 1.08 over 20 s), close ✕ top right.
- Title, meta row (`2026 · TV · 12 ep · ★ 85%`, "Sul NAS" badge when downloaded).
- Full-width `▶ Riproduci E3` (white) and `⬇ Scarica E3` (device download, shows progress).
- Description, 3 lines, "altro" expands.
- Action row with icons + small labels: `Lista` (AniList status PLANNING via the existing
  `PUT /api/anilist/entry/{id}/status`), `Trova episodi` (opens the sources sheet),
  `Ripara` (repair on the NAS).
- Tabs: **Episodi** (list: 16:9 thumbnail with progress, `1. Title`, duration, device-download
  button; context menu keeps "Ripara sul NAS", "Apri con…") and **Simili** (poster grid of
  titles sharing a genre, taken from the NAS library and the home's trending/seasonal lists;
  there is no recommendations endpoint).

### Novità
Vertical feed, newest first, built from existing data:
- "Aggiunti sul NAS": series grouped by `updatedAt` day; each item has a date column on the left
  (`OTT` / `31`) and a 16:9 card on the right with title, "Episodio N disponibile", genres, and
  `Riproduci` / `Lista` buttons.
- "Questa stagione": AniList seasonal titles, same card style without the date column.
A segmented header switches between the two.

### Cerca
- Search field at the top, focused on open, results as you type (debounce 300 ms).
- Empty field: "Di tendenza" list: wide thumbnail, title, ▶ icon on the right.
- Results: 3-column poster grid; NAS series first, then AniList.

### Download
Today's screen with the new look: segmented `Sul NAS` / `Su questo dispositivo`. Rows with 16:9
thumbnails. Keep every existing function (queue controls, priority, pause, offline playback,
"Apri con…", the offline banner).

### Il mio AnimeRoll
Avatar circle with the initial, username, AniList status. Then cards:
- `Download` (device count + space used) → Download screen
- `La mia lista` (AniList planning) shelf
- `Visti di recente` shelf (continue watching incl. completed)
- `Impostazioni` (today's Settings: account, AniList, passkeys, NAS queue, Telegram, logout)

### Login
Full-screen dark gradient, app name large, the three fields as today, `Accedi` white button,
passkey button below. No profile picker after login.

## Player
Unchanged in behaviour (subtitles, sync, startup timing). Restyle only the controls: centred
⏪10 ▶ ⏩10, bottom seek bar in `accent`, title top-left, "Episodio successivo" button near the end.
iOS keeps the system player controls for now (Picture in Picture, AirPlay and accessibility come
with them); only the iPhone Duo tabletop controls are custom.

## Done means
- Every screen above exists and uses real data from the server; nothing hard-coded.
- Everything that works today still works (playback, subtitles, NAS downloads and queue,
  device downloads, offline mode, repair, AniList, passkeys, Telegram settings).
- iOS: builds with Xcode 27.2, checked on the iPhone Duo simulator folded and unfolded and on iPad.
- Android: builds with `./gradlew assembleDebug`, checked on an emulator.
