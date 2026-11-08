#!/usr/bin/env python3
"""
AnimeRoll script plugin for Crunchyroll: subtitles only.

It looks up series and episodes and fetches the soft subtitle files (ASS) for episodes that are already in the
library. It does not download video or audio and does nothing about DRM.

The API flow (token exchange, catalogue and playback endpoints) follows
https://github.com/CuteTenshii/crunchyroll-downloader, MIT License, Copyright 2025-2026 CuteTenshii.
See THIRD-PARTY-NOTICES.md.
"""
import asyncio
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path

# ---------- settings (env / plugin settings) ----------
def setting(key: str, default: str = "") -> str:
    # AnimeRoll injects ANIMEROLL_SETTING_<KEY>
    env_key = f"ANIMEROLL_SETTING_{key.upper()}"
    return os.environ.get(env_key) or default

ETP_RT          = setting("etp_rt")
AUDIO_LANG      = setting("audio_lang", "ja-JP")                     # which dub to list episodes for
SUBS_LANG       = setting("subs_lang", "en-US")
CC_LANG         = setting("cc_lang", "")

DEVICE_ID = str(uuid.uuid4())
UA = "Mozilla/5.0 (X11; Linux x86_64; rv:147.0) Gecko/20100101 Firefox/147.0"
BASIC_AUTH = "bm9haWhkZXZtXzZpeWcwYThsMHE6"   # public web client id, same as in the tool above

_token: str | None = None
_token_lock = asyncio.Lock()


def emit(kind: str, **fields):
    print(json.dumps({"type": kind, **fields}, ensure_ascii=False), flush=True)


# ---------- HTTP helpers ----------
def _request(method: str, url: str, headers: dict | None = None,
             data: bytes | None = None, timeout: int = 25) -> tuple[int, bytes]:
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("User-Agent", UA)
    if headers:
        for k, v in headers.items():
            req.add_header(k, v)
    with urllib.request.urlopen(req, timeout=timeout) as res:
        return res.status, res.read()


async def get_access_token() -> str:
    global _token
    async with _token_lock:
        if _token:
            return _token
        if not ETP_RT:
            raise RuntimeError("Impostazione mancante: etp_rt (cookie Crunchyroll)")

        body = urllib.parse.urlencode({
            "device_id": DEVICE_ID,
            "device_type": "Firefox on Linux",
            "grant_type": "etp_rt_cookie",
        }).encode()

        headers = {
            "Authorization": f"Basic {BASIC_AUTH}",
            "Content-Type": "application/x-www-form-urlencoded",
            "Cookie": f"device_id={DEVICE_ID}; etp_rt={ETP_RT}",
        }
        status, raw = await asyncio.to_thread(
            _request, "POST", "https://www.crunchyroll.com/auth/v1/token",
            headers, body
        )
        if status != 200:
            raise RuntimeError(
                f"Crunchyroll ha rifiutato etp_rt (HTTP {status}). "
                "Aggiorna il cookie etp_rt."
            )
        data = json.loads(raw)
        token = data.get("access_token")
        if not token:
            raise RuntimeError("Risposta token senza access_token")
        _token = token
        return token


async def api_get(path: str, params: dict | None = None) -> dict:
    token = await get_access_token()
    qs = urllib.parse.urlencode(params or {})
    url = f"https://www.crunchyroll.com{path}"
    if qs:
        url += ("&" if "?" in url else "?") + qs

    headers = {"Authorization": f"Bearer {token}"}
    try:
        status, raw = await asyncio.to_thread(_request, "GET", url, headers)
    except Exception as e:
        raise RuntimeError(f"Errore di rete Crunchyroll: {e}") from e

    if status == 401:
        # token scaduto → forza refresh
        global _token
        _token = None
        token = await get_access_token()
        headers["Authorization"] = f"Bearer {token}"
        status, raw = await asyncio.to_thread(_request, "GET", url, headers)

    if status != 200:
        raise RuntimeError(f"Crunchyroll HTTP {status}: {raw[:200]!r}")

    return json.loads(raw)


# ---------- actions ----------
async def init(req: dict, slots):
    if not ETP_RT:
        emit("error", message="Impostazione mancante: etp_rt")
        sys.exit(1)
    # prova un token subito così la fonte risulta “Non disponibile” se il cookie è morto
    await get_access_token()
    emit("log", message="Crunchyroll: login ok")


async def search(req: dict, slots):
    q = (req.get("query") or "").strip()
    if not q:
        emit("result", data=[])
        return

    data = await api_get(
        "/content/v2/discover/search",
        {
            "q": q,
            "n": 30,
            "type": "series,movie_listing",
            "locale": "en-US",
        },
    )

    # data["data"] = [ { "type": "series", "items": [ ... ] }, ... ]
    buckets = data.get("data") if isinstance(data, dict) else []
    if not isinstance(buckets, list):
        buckets = []

    results = []
    seen = set()

    for bucket in buckets:
        if not isinstance(bucket, dict):
            continue
        entries = bucket.get("items")
        if not isinstance(entries, list):
            # a volte l'item è già il contenuto
            entries = [bucket] if bucket.get("id") else []

        for e in entries:
            if not isinstance(e, dict):
                continue
            ctype = e.get("type") or bucket.get("type")
            if ctype not in ("series", "movie_listing", None):
                continue

            cid = e.get("id") or e.get("guid")
            if not cid or cid in seen:
                continue
            seen.add(cid)

            title = e.get("title") or e.get("name") or "?"
            poster = _pick_poster(e.get("images"))

            year = None
            ep_count = None
            meta = e.get("series_metadata") or e.get("movie_metadata") or {}
            if isinstance(meta, dict):
                year = meta.get("series_launch_year") or meta.get("premiere_year")
                ep_count = meta.get("episode_count")

            subtitle_parts = []
            if year:
                subtitle_parts.append(str(year))
            if ep_count is not None:
                subtitle_parts.append(f"{ep_count} ep")

            results.append({
                "ref": cid,
                "title": title,
                "posterUrl": poster,
                "subtitle": " · ".join(subtitle_parts) if subtitle_parts else None,
                "aniListId": None,
            })
            if len(results) >= 30:
                break
        if len(results) >= 30:
            break

    emit("result", data=results)


def _pick_poster(images) -> str | None:
    """Crunchyroll images sono spesso:
       { "poster_tall": [ [ {width, height, source}, ... ] ] }
       oppure una lista piatta di oggetti.
    """
    if not isinstance(images, dict):
        return None

    for key in ("poster_tall", "poster_wide", "thumbnail"):
        raw = images.get(key)
        if not raw:
            continue
        # normalizza a lista piatta di dict con "source"
        candidates = []
        if isinstance(raw, list):
            for item in raw:
                if isinstance(item, list):
                    candidates.extend(x for x in item if isinstance(x, dict))
                elif isinstance(item, dict):
                    candidates.append(item)
        elif isinstance(raw, dict):
            candidates.append(raw)

        with_src = [c for c in candidates if c.get("source")]
        if not with_src:
            continue
        best = max(with_src, key=lambda x: x.get("width") or 0)
        return best.get("source")
    return None

async def episodes(req: dict, slots):
    series_id = req.get("ref") or ""
    if not series_id:
        emit("result", data=[])
        return

    # 1) stagioni
    seasons_data = await api_get(
        f"/content/v2/cms/series/{urllib.parse.quote(series_id)}/seasons",
        {
            "preferred_audio_language": AUDIO_LANG.split(",")[0].strip() or "ja-JP",
            "locale": "en-US",
        }
    )
    seasons = seasons_data.get("data") or []

    all_eps = []
    for season in seasons:
        sid = season.get("id")
        if not sid:
            continue
        eps_data = await api_get(
            f"/content/v2/cms/seasons/{urllib.parse.quote(sid)}/episodes",
            {
                "preferred_audio_language": AUDIO_LANG.split(",")[0].strip() or "ja-JP",
                "locale": "en-US",
            }
        )
        for e in eps_data.get("data") or []:
            # preferisci la versione con audio richiesto se presente
            versions = e.get("versions") or []
            guid = e.get("id")
            for v in versions:
                if v.get("audio_locale") == (AUDIO_LANG.split(",")[0].strip() or "ja-JP"):
                    guid = v.get("guid") or guid
                    break
            num = e.get("episode_number")
            # alcuni speciali hanno None
            title = e.get("title") or (f"Episodio {num}" if num is not None else "Episodio")
            all_eps.append({
                "ref": guid,
                "number": float(num) if num is not None else None,
                "title": title,
                "aniListId": None,
            })

    # ordina per numero (None alla fine)
    all_eps.sort(key=lambda x: (x["number"] is None, x["number"] or 0))
    emit("result", data=all_eps)


async def download(req: dict, slots):
    # Deliberately not implemented: this plugin only fetches subtitle files for episodes you already have.
    emit("error", message="Questo plugin scarica solo i sottotitoli, non gli episodi.")
    sys.exit(1)


# Crunchyroll locale -> ISO 639-2, the codes the library uses for subtitle tracks.
LOCALE3 = {
    "en-US": "eng", "it-IT": "ita", "ja-JP": "jpn", "es-419": "spa", "es-ES": "spa", "fr-FR": "fre", "de-DE": "ger",
    "pt-BR": "por", "pt-PT": "por", "ar-SA": "ara", "ru-RU": "rus", "id-ID": "ind", "ms-MY": "may", "pl-PL": "pol",
    "th-TH": "tha", "vi-VN": "vie", "zh-CN": "chi", "zh-HK": "chi", "zh-TW": "chi", "tr-TR": "tur", "hi-IN": "hin",
    "ta-IN": "tam", "te-IN": "tel", "ca-ES": "cat", "ko-KR": "kor",
}
# v2 lists the soft subtitles (v1 only reports the burned-in ones).
PLAY_URLS = (
    "https://www.crunchyroll.com/playback/v2/{guid}/web/firefox/play",
    "https://cr-play-service.prd.crunchyrollsvc.com/v2/{guid}/web/firefox/play",
)
TOKEN_URLS = (
    "https://www.crunchyroll.com/playback/v1/token/{guid}/{token}",
    "https://cr-play-service.prd.crunchyrollsvc.com/v1/token/{guid}/{token}",
)


async def subtitles(req: dict, slots):
    """Only the subtitle files of one episode (original ASS), no video: used to refresh episodes already in the library."""
    ref = req.get("ref") or ""
    out_dir = Path(req.get("outputDirectory") or ".")
    if not ref:
        emit("error", message="Riferimento episodio mancante")
        sys.exit(1)
    out_dir.mkdir(parents=True, exist_ok=True)

    token = await get_access_token()
    play = None
    last_err = ""
    for tpl in PLAY_URLS:
        url = tpl.format(guid=urllib.parse.quote(ref))
        try:
            status, raw = await asyncio.to_thread(_request, "GET", url, {"Authorization": f"Bearer {token}"})
            if status == 200:
                play = json.loads(raw)
                break
        except urllib.error.HTTPError as e:
            last_err = f"HTTP {e.code} da {urllib.parse.urlparse(url).netloc}"
        except Exception as e:
            last_err = str(e)
    if play is None:
        emit("error", message=f"Crunchyroll non ha restituito i sottotitoli ({last_err or 'nessuna risposta'}).")
        sys.exit(1)

    # Free the stream slot the playback request just opened.
    if play.get("token"):
        for tpl in TOKEN_URLS:
            try:
                await asyncio.to_thread(_request, "DELETE", tpl.format(guid=urllib.parse.quote(ref), token=urllib.parse.quote(play["token"])),
                                        {"Authorization": f"Bearer {token}"})
                break
            except Exception:
                continue

    wanted = [x.strip() for x in SUBS_LANG.split(",") if x.strip()]
    wanted_all = "ALL" in (w.upper() for w in wanted)
    available = {k: v for k, v in (play.get("subtitles") or {}).items() if isinstance(v, dict) and v.get("url")}
    if CC_LANG:
        for k, v in (play.get("captions") or {}).items():
            if isinstance(v, dict) and v.get("url"):
                available.setdefault(k, v)
                wanted.append(k)
    chosen = list(available) if wanted_all else [w for w in wanted if w in available]
    if not chosen:
        emit("log", message=f"Nessun sottotitolo tra {wanted} (disponibili: {list(available)})")

    result = []
    for i, locale in enumerate(chosen):
        sub = available[locale]
        try:
            _, body = await asyncio.to_thread(_request, "GET", sub["url"], None, None, 60)
        except Exception as e:
            emit("log", message=f"Sottotitolo {locale} non scaricato: {e}")
            continue
        fmt = (sub.get("format") or "ass").lower()
        name = f"{i:02d}.{locale}.{fmt}"
        (out_dir / name).write_bytes(body)
        result.append({
            "path": name, "language": LOCALE3.get(locale, locale.split("-")[0]),
            "title": None, "forced": False, "isDefault": i == 0, "format": fmt,
        })
    emit("result", data=result)


ACTIONS = {
    "subtitles": subtitles,
    "init": init,
    "search": search,
    "episodes": episodes,
    "download": download,
}


async def main():
    action = sys.argv[1] if len(sys.argv) > 1 else ""
    request = json.loads(sys.stdin.readline() or "{}")
    handler = ACTIONS.get(action)
    if handler is None:
        return
    await handler(request, asyncio.Semaphore(4))


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except Exception as e:
        emit("error", message=str(e))
        sys.exit(1)