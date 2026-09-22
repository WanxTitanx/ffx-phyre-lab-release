#!/usr/bin/env python3
"""Local noclip.website (dist-ffxstudio) server for the Phyre lab.

Routes (same contract as FFXProjectEditor ViewerHub/StudioWebServer):
  /noclip/*                    -> static dist-ffxstudio bundle
  /data/FinalFantasyX/*        -> local cache, then CDN fetch-through
                                   (https://z.noclip.website/FinalFantasyX)
  /                            -> redirect to /noclip/index.html

Fetch-through rules mirror StudioWebServer.TryBuildNoclipCdnRelativePath:
only FinalFantasyX/<2-hex>/<name>.bin and the 3 critical root bins are
fetchable. Everything else 404s without touching the network.

Usage:
  python3 tools/noclip_server.py [--port 8777]
  open http://127.0.0.1:8777/noclip/index.html#ffx/021/1   (Ruins - Corridor)
"""

import argparse
import os
import posixpath
import re
import sys
import json
import threading
import urllib.request
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote, urlsplit

BUNDLE_ROOT = os.environ.get(
    "NOCLIP_BUNDLE",
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "vendor", "dist-ffxstudio"),
)
DATA_ROOT = os.environ.get(
    "NOCLIP_DATA",
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".lab", "noclip-data"),
)
# Edited bins live here and are served BEFORE the local cache/CDN — the
# viewer sees lab edits without touching the bundle or upstream data.
OVERLAY_ROOT = os.environ.get(
    "NOCLIP_OVERLAY",
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".lab", "overlays"),
)
CDN_BASE = "https://z.noclip.website/FinalFantasyX"
CRITICAL_BINS = {"common_textures.bin", "screen_shatter.bin", "env_map_texture.bin"}
MAX_FETCH = 256 * 1024 * 1024

_hex2 = re.compile(r"^[0-9a-fA-F]{2}$")
_name = re.compile(r"^[0-9a-zA-Z_]{1,64}\.bin$")

_fetch_locks: dict[str, threading.Lock] = {}
_locks_mu = threading.Lock()


def cdn_rel(path: str) -> str | None:
    """Return the CDN-relative path for a /data/FinalFantasyX/... request, else None."""
    rest = path.removeprefix("FinalFantasyX/")
    if rest == path:
        return None
    if rest in CRITICAL_BINS:
        return rest
    parts = rest.split("/")
    if len(parts) == 2 and _hex2.match(parts[0]) and _name.match(parts[1]):
        return rest
    return None


def fetch_through(rel: str) -> bytes | None:
    url = f"{CDN_BASE}/{rel}"
    req = urllib.request.Request(
        url,
        headers={
            "User-Agent": "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 "
                          "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
            "Accept": "application/octet-stream,*/*",
        },
    )
    with urllib.request.urlopen(req, timeout=60) as r:
        data = r.read(MAX_FETCH + 1)
    if len(data) > MAX_FETCH:
        return None
    return data


class Handler(SimpleHTTPRequestHandler):
    def log_message(self, fmt, *args):
        sys.stderr.write("[noclip] " + fmt % args + "\n")

    def _safe_join(self, root: str, rel: str) -> str | None:
        rel = posixpath.normpath(unquote(rel)).lstrip("/")
        full = os.path.realpath(os.path.join(root, rel))
        return full if full.startswith(os.path.realpath(root) + os.sep) else None

    def _serve_file(self, full: str) -> bool:
        if not os.path.isfile(full):
            return False
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(os.path.getsize(full)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        if self.command != "HEAD":
            with open(full, "rb") as f:
                while chunk := f.read(1 << 20):
                    self.wfile.write(chunk)
        return True

    def do_GET(self):
        path = urlsplit(self.path).path

        # The bundle uses relative asset URLs; if index.html ends up treated
        # as a directory (e.g. trailing-slash or iframe URL quirks), requests
        # arrive as /noclip/index.html/noclip/static/... — collapse to the
        # last /noclip/ or /data/ segment.
        if path.count("/noclip/") > 1:
            path = "/noclip/" + path.rsplit("/noclip/", 1)[1]
        elif path.count("/data/") > 1:
            path = "/data/" + path.rsplit("/data/", 1)[1]

        if path in ("/", "/index.html"):
            self.send_response(302)
            self.send_header("Location", "/noclip/index.html")
            self.end_headers()
            return

        if path.startswith("/noclip/"):
            full = self._safe_join(BUNDLE_ROOT, path[len("/noclip/"):])
            if full and os.path.isfile(full):
                return self._serve_bundle(full)
            self.send_error(404, "not found")
            return

        if path.startswith("/data/"):
            rel = path[len("/data/"):]
            # Lab overlay wins over cache and CDN.
            ov = self._safe_join(OVERLAY_ROOT, rel)
            if ov and os.path.isfile(ov):
                self.log_message("overlay %s", rel)
                if self._serve_file(ov):
                    return
            full = self._safe_join(DATA_ROOT, rel)
            if full and os.path.isfile(full):
                if self._serve_file(full):
                    return
            # CDN fetch-through under the same segment rules as StudioWebServer
            cdn = cdn_rel(posixpath.normpath(unquote(rel)).lstrip("/"))
            if cdn and full:
                with _locks_mu:
                    lock = _fetch_locks.setdefault(cdn, threading.Lock())
                with lock:
                    if os.path.isfile(full) and self._serve_file(full):
                        return
                    try:
                        data = fetch_through(cdn)
                    except Exception as e:  # noqa: BLE001 - report and 404
                        self.log_message("cdn fetch failed %s: %s", cdn, e)
                        data = None
                    if data is not None:
                        os.makedirs(os.path.dirname(full), exist_ok=True)
                        tmp = full + ".part"
                        with open(tmp, "wb") as f:
                            f.write(data)
                        os.replace(tmp, full)
                        self.log_message("cdn->cache %s (%d bytes)", cdn, len(data))
                        if self._serve_file(full):
                            return
            self.send_error(404, "not found")
            return

        self.send_error(404, "not found")

    do_HEAD = do_GET

    # ── Mutable edit route ─────────────────────────────────────────────
    # POST /api/edits/<encId>  body {"slot":N,"position":[dx,dy,dz],
    # "heading":rad,"scale":mult} -> merges into
    # .lab/overlays/FinalFantasyX/edits/<encId>.json which the viewer
    # re-fetches via /data. Same contract as StudioWebServer.HandleEditsPost
    # + SidecarEditsWriter.ApplySlot.
    def do_POST(self):
        path = urlsplit(self.path).path
        if not path.startswith("/api/edits/"):
            self.send_error(404, "not found")
            return
        enc = path[len("/api/edits/"):]
        if not enc.isdigit() or len(enc) > 10 or not (0 <= int(enc) <= 0xFFFF):
            return self._json(400, ok=False, error="invalid encounter id")
        enc = str(int(enc))
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            length = 0
        if length <= 0:
            return self._json(400, ok=False, error="body required")
        if length > 65536:
            return self._json(413, ok=False, error="body too large")
        body = self.rfile.read(length)
        if len(body) != length:
            return self._json(400, ok=False, error="truncated body")
        try:
            payload = json.loads(body)
            slot = int(payload["slot"])
            if not (0 <= slot <= 7):
                raise ValueError
        except Exception:
            return self._json(400, ok=False, error="invalid edit payload")

        edits_root = os.path.join(OVERLAY_ROOT, "FinalFantasyX", "edits")
        os.makedirs(edits_root, exist_ok=True)
        dest = os.path.join(edits_root, enc + ".json")
        with _locks_mu:
            lock = _fetch_locks.setdefault("edit:" + enc, threading.Lock())
        with lock:
            root = {}
            if os.path.isfile(dest):
                try:
                    root = json.loads(open(dest, encoding="utf8").read())
                except Exception:
                    root = {}
            actors = root.setdefault("actors", {})
            s = actors.setdefault(str(slot), {})
            pos = payload.get("position")
            if isinstance(pos, list) and len(pos) == 3:
                s["position"] = [float(x) for x in pos]
            if "heading" in payload:
                s["heading"] = float(payload["heading"])
            if "scale" in payload:
                s["scale"] = float(payload["scale"])
            if "monster" in payload:
                m = int(payload["monster"])
                if not (0 <= m <= 0xFFFF):
                    return self._json(400, ok=False, error="invalid monster id")
                s["monster"] = m
            tmp = dest + ".part"
            with open(tmp, "w", encoding="utf8") as fo:
                json.dump(root, fo, indent=2)
            os.replace(tmp, dest)
        self.log_message("edit saved %s slot=%d", enc, slot)
        return self._json(200, ok=True, file=enc + ".json", slot=slot)

    def _json(self, code: int, **obj):
        data = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(data)

    def _serve_bundle(self, full: str):
        import mimetypes
        ctype = mimetypes.guess_type(full)[0] or "application/octet-stream"
        if full.endswith(".js"):
            ctype = "text/javascript"
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(os.path.getsize(full)))
        self.end_headers()
        if self.command != "HEAD":
            with open(full, "rb") as f:
                while chunk := f.read(1 << 20):
                    self.wfile.write(chunk)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8777)
    ap.add_argument("--host", default="127.0.0.1")
    args = ap.parse_args()

    os.makedirs(DATA_ROOT, exist_ok=True)
    os.makedirs(OVERLAY_ROOT, exist_ok=True)
    if not os.path.isdir(BUNDLE_ROOT):
        sys.exit(f"bundle not found: {BUNDLE_ROOT}")

    srv = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"[noclip] bundle = {BUNDLE_ROOT}")
    print(f"[noclip] data   = {DATA_ROOT}")
    print(f"[noclip] overlay= {OVERLAY_ROOT}")
    print(f"[noclip] http://{args.host}:{args.port}/noclip/index.html#ffx/021/1")
    srv.serve_forever()


if __name__ == "__main__":
    main()
