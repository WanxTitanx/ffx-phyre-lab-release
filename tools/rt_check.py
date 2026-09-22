#!/usr/bin/env python3
# ── PHYRE-LAB glTF round-trip checker (P21) ──────────────────────────────────
# Compares the tagged glTF that LEFT the lab with the glTF that CAME BACK
# from the DCC. Emits a loss report — glTF is an exchange view; identity is
# proven by stable ids/names, rig hierarchy, vertex counts and skin weights,
# NOT by byte equality (re-exports legitimately reorder accessors).
#
# Usage: rt_check.py <outbound.gltf> <returned.gltf> [--report out.json]
# Exit 0 if identity preserved (all required checks); 2 on loss.
import json
import math
import struct
import sys
import base64
import os


def buf(g, i, path):
    b = g["buffers"][i]
    uri = b["uri"]
    if uri.startswith("data:"):
        return base64.b64decode(uri.split(",", 1)[1])
    return open(os.path.join(os.path.dirname(path), uri), "rb").read()


def accessor(g, i, path):
    a = g["accessors"][i]
    bv = g["bufferViews"][a["bufferView"]]
    raw = buf(g, bv["buffer"], path)
    off = bv.get("byteOffset", 0) + a.get("byteOffset", 0)
    ncomp = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4,
             "MAT4": 16}[a["type"]]
    fmt, sz = {5126: ("f", 4), 5123: ("H", 2), 5125: ("I", 4),
               5121: ("B", 1)}[a["componentType"]]
    n = a["count"] * ncomp
    st = bv.get("byteStride", sz * ncomp)
    out = []
    for k in range(a["count"]):
        out.append(struct.unpack_from("<" + fmt * ncomp,
                                      raw, off + k * st))
    return out


def snapshot(g, path):
    nodes = g["nodes"]
    par = {}
    for i, n in enumerate(nodes):
        for c in n.get("children", []):
            par[c] = i
    names = [nodes[i].get("name", f"node{i}") for i in range(len(nodes))]
    sk = g.get("skins", [{}])
    joints = [names[j] for j in sk[0].get("joints", [])]
    mesh0 = g["meshes"][0]["primitives"][0]
    pos = accessor(g, mesh0["attributes"]["POSITION"], path)
    wts = (accessor(g, mesh0["attributes"]["WEIGHTS_0"], path)
           if "WEIGHTS_0" in mesh0["attributes"] else [])
    jts = (accessor(g, mesh0["attributes"]["JOINTS_0"], path)
           if "JOINTS_0" in mesh0["attributes"] else [])
    # per-position skinning map keyed by JOINT NAME (DCCs reorder the
    # skin.joints array, so raw JOINTS_0 indices are not comparable)
    skin_map = {}
    for i, p in enumerate(pos):
        key = tuple(round(v, 4) for v in p)
        w = wts[i] if i < len(wts) else ()
        j = jts[i] if i < len(jts) else ()
        # set of distinct influence tuples per position — split verts are
        # exact duplicates, so multiplicity is not semantic
        skin_map.setdefault(key, set()).add(
            tuple(sorted((joints[x], round(wv, 4))
                         for x, wv in zip(j, w) if wv > 0)))
    return {
        "nNodes": len(nodes), "nodeNames": names, "parentOf": par,
        "nMeshes": len(g["meshes"]), "nJoints": len(joints),
        "jointNames": joints,
        "nVerts": len(pos),
        "verts": set(tuple(round(v, 4) for v in p) for p in pos),
        "skinMap": skin_map,
        "nodeExtras": {names[i]: nodes[i].get("extras", {})
                       for i in range(len(nodes))},
    }


def main():
    a = sys.argv
    if len(a) < 3:
        print("usage: rt_check.py <out.gltf> <back.gltf> [--report r.json]")
        return 1
    src, back = a[1], a[2]
    s = snapshot(json.load(open(src)), src)
    r = snapshot(json.load(open(back)), back)
    loss = {"dropped": [], "changed": [], "identical": []}

    def chk(key, cond, note):
        (loss["identical"] if cond else loss["dropped"]).append(
            {"check": key, "note": note})

    # DCCs legitimately add wrapper nodes and split seam verts — additive
    # deltas go to "changed", real identity losses to "dropped".
    added = sorted(set(r["nodeNames"]) - set(s["nodeNames"]))
    missing = sorted(set(s["nodeNames"]) - set(r["nodeNames"]))
    chk("node_names", not missing,
        f"all {len(set(s['nodeNames']))} names present; "
        f"added={added or 'none'}")
    if added:
        loss["changed"].append({"check": "nodes_added",
                                "note": f"{added} (DCC wrapper nodes)"})
    # hierarchy over shared names; reparents only onto ADDED wrapper nodes
    # are DCC artifacts -> "changed", shared-node reparents -> "dropped"
    def parents_by_name(sn):
        return {sn["nodeNames"][c]: sn["nodeNames"][p]
                for c, p in sn["parentOf"].items()}
    ps, pr = parents_by_name(s), parents_by_name(r)
    bad, wrap = [], []
    for k in set(ps) | set(pr):
        if ps.get(k) == pr.get(k):
            continue
        (wrap if pr.get(k) in added else bad).append(k)
    chk("hierarchy", not bad,
        "parent map preserved over shared nodes" if not bad else
        f"reparented: {bad[:5]}")
    if wrap:
        loss["changed"].append({"check": "hierarchy_wrapper",
                                "note": f"{wrap} now under {added}"})
    chk("joint_count", s["nJoints"] == r["nJoints"],
        f"{s['nJoints']} vs {r['nJoints']}")
    chk("joint_names", set(s["jointNames"]) == set(r["jointNames"]),
        "skin joint name set (order may differ)")
    chk("vert_positions", s["verts"] == r["verts"],
        f"distinct position set equal ({len(s['verts'])})" if
        s["verts"] == r["verts"] else "positions differ")
    if s["nVerts"] != r["nVerts"]:
        loss["changed"].append({"check": "vert_count",
                                "note": f"{s['nVerts']} vs {r['nVerts']} — "
                                        f"DCC splits seam verts"})
    chk("skin_map", s["skinMap"] == r["skinMap"],
        "per-position (jointName,weight) map equal" if
        s["skinMap"] == r["skinMap"] else "skinning differs")
    # provenance: node-level extras
    miss_id = [n for n in s["nodeNames"]
               if s["nodeExtras"].get(n, {}).get("phyre_id") !=
               r["nodeExtras"].get(n, {}).get("phyre_id")]
    chk("provenance", not miss_id,
        "node.extras.phyre_id round-tripped" if not miss_id
        else f"lost on {miss_id[:5]}")

    ok = not loss["dropped"]
    loss["result"] = "pass" if ok else "loss"
    rep = {"result": loss["result"], "loss": loss}
    out = a[a.index("--report") + 1] if "--report" in a else None
    if out:
        json.dump(rep, open(out, "w"), indent=1, ensure_ascii=False)
    print(json.dumps({k: len(v) if isinstance(v, list) else v
                      for k, v in loss.items()}, ensure_ascii=False))
    return 0 if ok else 2


if __name__ == "__main__":
    sys.exit(main())
