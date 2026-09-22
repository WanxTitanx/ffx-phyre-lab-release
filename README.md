<p align="center">
  <img src="assets/logo.png" width="140" alt="FFX Mod Studio logo">
</p>

<h1 align="center">FFX Phyre Lab</h1>

<p align="center"><b>Rebuilding PhyreEngine-grade tooling for FINAL FANTASY X HD — one proven byte at a time.</b></p>

<p align="center">Part of the <b>FFX Mod Studio</b> ecosystem · 🇧🇷 Made by a Brazilian developer — WanxTitanx</p>

<p align="center">
  <img src="https://img.shields.io/badge/status-ALPHA-orange" alt="status">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-GPL--3.0-blue" alt="license"></a>
  <img src="https://img.shields.io/badge/.NET-10%20%2B%20Avalonia-purple" alt="dotnet">
  <img src="https://img.shields.io/badge/Python-3.10%2B-blue" alt="python">
  <img src="https://img.shields.io/badge/platform-Windows%20x64%20%C2%B7%20Linux-informational" alt="platform">
  <a href="https://store.steampowered.com/app/359870"><img src="https://img.shields.io/badge/game-FFX%2FFF--X--2%20HD%20Remaster%20(Steam)-green" alt="game"></a>
</p>

<p align="center"><a href="README.pt-BR.md"><b>Versão em Português →</b></a></p>

---

## What is this?

An open-source research lab whose end goal is a **complete, FFX-compatible
PhyreEngine toolchain**: load real game assets, inspect and edit meshes,
skeletons, animations, particles, maps and kernel data, then write results the
actual `FFX.exe` accepts. FFX-2 support is on the horizon — same engine family,
same data layout.

No game assets, no SDK code, no executables are distributed here. You point the
tools at your **own** extracted FFX HD data and your own PhyreEngine knowledge.

## Status — ALPHA

This is research-grade software. Things that are **proven** today:

- **`.phyre` cluster reader** — self-describing class namespace, object blocks,
  shared data, external vertex/index payloads (FFX `RYHP` tags).
- **Preserving writers** — region-exact patching that keeps unknown/opaque data
  intact, with journals, atomic saves and undo.
- **Real in-game proof** — a minimal persistent edit (72 map textures via
  overlay route) was loaded and observed in the actual game.
- **FfxLab desktop app** — scene/map renderer (CPU + GPU raster), actor
  animation playback, EFFECT keyframes, PPP particle simulation, encounter
  editing, EV01 actor swap, kernel table editing, vertex/vertex-color editing.
- **Interchange** — versioned `phyre.project.v1` JSON schema + glTF/Blender
  roundtrip for meshes, rigs and skinning.
- **`.mgrp` motion decode/edit** and **ATEL script disassembly** (events +
  encounters).

**In progress / not yet:** full clip authoring accepted by the game, synthetic
monster injection, playable map authoring end-to-end, FFX-2. A green viewer or
a glTF export is *not* treated as proof of game compatibility — see
`docs/ACCEPTANCE.md` for the gates.

## Components

| Path | What |
|---|---|
| `src/ui/FfxLab` | Avalonia desktop lab — map scene viewer/editor, GL viewport, particle sim |
| `src/ui/PhyreInspector` | Avalonia inspector — meshes, textures, skeleton, animation, glTF export |
| `tools/phyre-reader` | `.phyre` cluster parser CLI |
| `tools/phyre-exporter` | skinned model/texture extraction |
| `tools/phyre-patcher` | region-exact preserving binary writer |
| `tools/phyre-project` | persistent editable project files (schema v1) |
| `tools/phyre-meshedit` | mesh vertex/color/topology editing, scene/worldmat |
| `tools/ffx-map1` | MAP1 scene: walkmesh, lighting, LEVEL_PART, PPP emitters, kernel bins, ATEL, magic VM |
| `tools/*.py` | corpus build, mgrp/chr/map checks, Blender IO, glTF render, noclip bridge |
| `tests/` | pytest suite — corpus-gated tests skip cleanly without local data |
| `schema/` | `phyre.project.v1` JSON schema |
| `docs/` | format notes: interchange, target format, maps, animation, editor census |

## Requirements

- **.NET 10 SDK** (tools + apps) · **Python 3.10+** (`pytest`, `jsonschema`,
  `numpy`, `Pillow` for parts of the suite) · optional **Blender** for the
  roundtrip tests.
- Your own extracted **FFX HD data** — set `FFX_ASSETS` and run
  `scripts/build_corpus.py` to build the local `.lab/corpus` (never committed).
- Optional: a local **noclip** `dist-ffxstudio` bundle (`NOCLIP_BUNDLE` or
  `vendor/dist-ffxstudio`) for the viewer bridge.

## Quick start

```bash
git clone https://github.com/WanxTitanx/ffx-phyre-lab-release.git
cd ffx-phyre-lab-release

dotnet build src/ui/FfxLab          # desktop lab
dotnet build src/ui/PhyreInspector  # inspector

export FFX_ASSETS=/path/to/extracted/ffx
python3 scripts/build_corpus.py     # local corpus (dev + holdout + negatives)
python3 -m pytest tests/            # corpus-gated tests skip without .lab
```

## Legal

FINAL FANTASY, FINAL FANTASY X and FINAL FANTASY X-2 are trademarks of Square
Enix Holdings Co., Ltd. Independent fan-made project — not affiliated with or
endorsed by Square Enix or Sony. **No game assets or proprietary SDK material
are distributed.** PhyreEngine references are local-oracle only.

## License

[GPL-3.0](LICENSE) — see [NOTICE.md](NOTICE.md) and
[THIRD_PARTY.md](THIRD_PARTY.md) for provenance and attribution.
