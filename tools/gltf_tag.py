#!/usr/bin/env python3
# ── PHYRE-LAB glTF provenance tagger (P21) ───────────────────────────────────
# Injects round-trip identity + provenance into a lab-exported glTF before
# it enters an external DCC (Blender). glTF is an exchange VIEW, not the
# authority on the format (docs/INTERCHANGE.md) — opaque regions, material
# params and FFX routes stay outside and must not be "re-imported" as truth.
#
# Tags written:
#   asset.extras.phyre_source = {path, sha256, profile, exported}
#   asset.extras.phyre_ids    = node-name -> stable id map (name is the id)
#   asset.generator           = appended "phyre-lab/gltf_tag"
# Usage: gltf_tag.py <in.gltf> <out.gltf> <source.phyre> [--profile ffx-hd]
import hashlib
import json
import sys


def sha256(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


def main():
    a = sys.argv
    if len(a) < 4:
        print("usage: gltf_tag.py <in.gltf> <out.gltf> <source.phyre> "
              "[--profile ffx-hd]")
        return 1
    g = json.load(open(a[1]))
    src = a[3]
    profile = "ffx-hd"
    if "--profile" in a:
        profile = a[a.index("--profile") + 1]

    names = [n.get("name", f"node{i}") for i, n in enumerate(g["nodes"])]
    dup = len(names) != len(set(names))
    g.setdefault("asset", {})
    g["asset"]["generator"] = (g["asset"].get("generator", "") +
                               " + phyre-lab/gltf_tag")
    prov = {"path": src, "sha256": sha256(src), "profile": profile}
    g["asset"]["extras"] = {
        "phyre_source": prov,
        "phyre_id_unique": not dup,
    }
    # asset.extras does not survive Blender's exporter (Khronos writes its
    # own asset block); node-level extras DO survive as object custom props.
    # Identity + provenance therefore live per node, not per file.
    for i, n in enumerate(g["nodes"]):
        n.setdefault("extras", {})
        n["extras"]["phyre_id"] = names[i]
        n["extras"]["phyre_src"] = prov["sha256"][:16]
    json.dump(g, open(a[2], "w"), ensure_ascii=False, indent=1,
              sort_keys=False)
    print(f"tagged {len(names)} nodes (unique={not dup}) -> {a[2]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
