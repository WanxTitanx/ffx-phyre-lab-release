#!/usr/bin/env python3
"""build_corpus.py — monta corpus representativo + negativos em .lab/corpus/.

Nada aqui vai para o Git: .lab/ é ignorado. O manifesto (só hashes e metadados)
vai em evidence/. Split: dev (uso em desenvolvimento) vs holdout (nunca usado
para desenvolver parsers — controle final). Negativos sintéticos cobrem
truncamento, overflow de comprimento, ciclos de offset e versão/magic inválidos.
"""
import hashlib
import json
import os
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
_ffx = os.environ.get("FFX_ASSETS")
if not _ffx:
    raise SystemExit("set FFX_ASSETS to your extracted FFX HD data root")
FFX = Path(_ffx)
OUT = ROOT / ".lab" / "corpus"
EVID = ROOT / "evidence" / "p05-corpus.json"

# (categoria, caminho relativo, split)
SELECTION = [
    # monstros: pequeno / médio / grande (mesh + textura + ahwin32)
    ("monster-small", "ffx_data/gamedata/ps3data/chr/mon/m001", "dev"),
    ("monster-medium", "ffx_data/gamedata/ps3data/chr/mon/m100", "dev"),
    ("monster-large", "ffx_data/gamedata/ps3data/chr/mon/m300", "holdout"),
    # npc
    ("npc", "ffx_data/gamedata/ps3data/chr/npc/n008", "dev"),
    # mapa de batalha
    ("map", "ffx_data/gamedata/ps3data/btlmap/bika/bika00_a", "dev"),
    ("map-holdout", "ffx_data/gamedata/ps3data/btlmap/dome/dome00_a", "holdout"),
    # MGRP (PS2 master) — vanilla e backfill
    ("mgrp-battle", "ffx_ps2/ffx/master/jppc/battle/mot/regmot.mgrp", "dev"),
    ("mgrp-npc", "ffx_ps2/ffx/master/jppc/chr/npc/n008/mot/resident0.mgrp", "dev"),
    ("mgrp-holdout", "ffx_ps2/ffx/master/jppc/chr/npc/n183/mot/resident0.mgrp", "holdout"),
    # vanilla-marked (baseline PS2) vs não-marcado (backfill HD)
    ("vanilla-mon", "ffx_ps2/ffx/master/jppc/battle/mon/_m065", "dev"),
]


def sha256(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()


def collect():
    entries = []
    for category, rel, split in SELECTION:
        src = FFX / rel
        if not src.exists():
            raise SystemExit(f"missing source: {src}")
        files = sorted(src.rglob("*")) if src.is_dir() else [src]
        for f in files:
            if not f.is_file():
                continue
            dst = OUT / split / category / f.relative_to(FFX)
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(f, dst)
            entries.append({
                "category": category, "split": split,
                "source": str(f.relative_to(FFX)),
                "corpus": str(dst.relative_to(ROOT)),
                "size": f.stat().st_size, "sha256": sha256(f),
                "vanilla_marker": f.suffix == ".vanilla" or (f.parent / (f.name + ".vanilla")).exists(),
            })
    return entries


def negatives():
    """Negativos sintéticos gerados localmente (não derivados de assets reais)."""
    neg = OUT / "negatives"
    neg.mkdir(parents=True, exist_ok=True)
    cases = {}

    # magic real dos .phyre FFX (little-endian 'RYHP'), observado no corpus
    MAGIC = b"RYHP"

    # truncamento: header válido mas payload cortado
    cases["truncated.phyre"] = MAGIC + (0).to_bytes(4, "little") + b"\x10\x00"

    # overflow: campo de comprimento 0xFFFFFFFF seguido de poucos bytes
    cases["overflow_len.phyre"] = (MAGIC + (0).to_bytes(4, "little")
                                   + b"\xff\xff\xff\xff" + b"A" * 16)

    # ciclo: offset de bloco apontando para si mesmo
    cases["cycle_offset.phyre"] = (MAGIC + (1).to_bytes(4, "little")
                                   + (8).to_bytes(4, "little")
                                   + (8).to_bytes(4, "little"))

    # versão/magic errado
    cases["bad_magic.phyre"] = b"NOPE" + (0).to_bytes(4, "little") + b"\x00" * 32
    cases["bad_version.phyre"] = MAGIC + (0xDEADBEEF).to_bytes(4, "little") + b"\x00" * 32

    # vazio e aleatório
    cases["empty.phyre"] = b""
    cases["random.bin"] = os.urandom(256)

    # mgrp truncado
    cases["truncated.mgrp"] = b"MGRP" + (0).to_bytes(4, "little")

    out = []
    for name, data in cases.items():
        p = neg / name
        p.write_bytes(data)
        out.append({"negative": name, "corpus": str(p.relative_to(ROOT)),
                    "size": len(data), "sha256": sha256(p)})
    return out


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    entries = collect()
    negs = negatives()
    manifest = {
        "task_id": "P05",
        "source_root": str(FFX),
        "entries": entries,
        "negatives": negs,
        "counts": {
            "dev": sum(1 for e in entries if e["split"] == "dev"),
            "holdout": sum(1 for e in entries if e["split"] == "holdout"),
            "negatives": len(negs),
        },
    }
    EVID.write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n")
    print(json.dumps(manifest["counts"]))
    print(f"manifest -> {EVID}")


if __name__ == "__main__":
    sys.exit(main())
