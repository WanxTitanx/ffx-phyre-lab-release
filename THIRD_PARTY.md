# Third-party provenance / Proveniência de terceiros

This repository contains original lab code plus components adapted from
the **FFX Editor** project (GPL-3.0, same author — private upstream).
Files carrying derived code keep a `Provenance:` header naming the source
snapshot hash; per the GPL-3.0 terms those files remain GPL-3.0 and so
does this repository as a whole.

Notably:

- `tools/phyre-reader/` — Phyre descriptor parser adapted from the FFX
  Editor `PhyreModelExportLab` snapshot (GPL-3.0).
- `tools/phyre-exporter/` — link-decoder core extracted verbatim from the
  editor's `animdump` lineage (GPL-3.0).

Project artwork:

- `assets/icons/`, `assets/logo.png`, `assets/social.png`, `*/Assets/app-icon.*`
  — generated with ChatGPT Images for this project (2026-09-22),
  post-processed locally (squircle mask, resize set, .ico packing, card
  composition). No third-party artwork or trademarks embedded.

References used locally (not vendored, not redistributed):

- **noclip.website** Fahrenheit module (MIT) — behavioral reference for the
  viewer bridge in `tools/noclip_server.py` (you supply your own build via
  `NOCLIP_BUNDLE`/`vendor/`).
- **PhyreEngine SDK** (Sony, proprietary) — used only as a local
  layout/format oracle. Nothing from the SDK is committed.
- **FFX HD game data** — local test corpus only (`.lab/corpus`, gitignored).
  Game assets are never committed.

If you import third-party code into this tree, record origin, revision,
license and attribution here first — unknown licenses stay out.
