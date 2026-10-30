#!/usr/bin/env python3
"""AnimeRoll script plugin – example and template.

Downloads a video from a direct http(s) link pasted in the apps. Copy this folder to
start a plugin in Python: every action is a function, AnimeRoll calls

    python3 main.py <action>        (cwd = plugin folder, JSON request on stdin)

and reads JSON lines from stdout. Contract: PLUGINS.md. Standard library only.
"""
import json
import os
import sys
import urllib.parse
import urllib.request


def emit(kind, **fields):
    print(json.dumps({"type": kind, **fields}), flush=True)


def init(req):
    # Validate settings here; emit("error", ...) + exit code 1 disables the source with that message.
    pass


def search(req):
    # Only called if "search" is in plugin.json capabilities.
    # emit("result", data=[{"ref": "...", "title": "...", "posterUrl": None, "subtitle": None, "aniListId": None}])
    emit("result", data=[])


def episodes(req):
    # Only called if "episodes" is in capabilities. req["ref"] = a search result ref.
    # emit("result", data=[{"ref": "...", "number": 1, "title": "Episodio 1"}])
    emit("result", data=[])


def stream(req):
    # Only called if "stream" is in capabilities: a URL the apps can play directly.
    emit("result", data=None)


def download(req):
    url = req["ref"]
    out_dir = req["outputDirectory"]
    name = os.path.basename(urllib.parse.urlparse(url).path) or "video.mp4"
    path = os.path.join(out_dir, name)

    request = urllib.request.Request(url, headers={"User-Agent": "AnimeRoll-Plugin/1.0"})
    with urllib.request.urlopen(request, timeout=60) as res, open(path, "wb") as f:
        total = int(res.headers.get("Content-Length") or 0)
        done, last_pct = 0, -1
        while chunk := res.read(1 << 16):
            f.write(chunk)
            done += len(chunk)
            pct = int(done * 100 / total) if total else 0
            if pct != last_pct:
                last_pct = pct
                emit("progress", fraction=done / total if total else 0, message=f"{done // 1048576} MB")

    emit("file", path=name)  # relative to outputDirectory


ACTIONS = {"init": init, "search": search, "episodes": episodes, "stream": stream, "download": download}

if __name__ == "__main__":
    action = sys.argv[1] if len(sys.argv) > 1 else ""
    request = json.loads(sys.stdin.readline() or "{}")
    try:
        ACTIONS.get(action, lambda _: None)(request)
    except Exception as e:  # shown to the user in the download list
        emit("error", message=str(e))
        sys.exit(1)
