#!/usr/bin/env python3
"""Minimal glTF software renderer for ffx-phyre-lab (P08 evidence).

Renders skinned glTF (JSON + embedded base64 buffer or .bin) with numpy:
  - node hierarchy -> world transforms
  - skinning: v' = sum(w_i * (worldJoint_i @ invBind_i @ v))
  - z-buffered triangle rasterizer, texture sampling, lambert shading
Outputs PNG per camera angle + a JSON hierarchy/skin report.

This is an OFFLINE viewer for lab evidence. It does not prove FFX runtime
acceptance. Usage: render_gltf.py in.gltf out_dir [size]
"""
import base64
import io
import json
import math
import sys
from pathlib import Path

import numpy as np
from PIL import Image

CT = {5120: np.int8, 5121: np.uint8, 5122: np.int16,
      5123: np.uint16, 5125: np.uint32, 5126: np.float32}
NC = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def load_gltf(path: Path):
    g = json.loads(path.read_text())
    if g["buffers"][0].get("uri", "").startswith("data:"):
        buf = base64.b64decode(g["buffers"][0]["uri"].split(",", 1)[1])
    else:
        buf = (path.parent / g["buffers"][0]["uri"]).read_bytes()
    return g, buf


def accessor(g, buf, idx):
    a = g["accessors"][idx]
    bv = g["bufferViews"][a["bufferView"]]
    dt, n = CT[a["componentType"]], NC[a["type"]]
    off = bv.get("byteOffset", 0) + a.get("byteOffset", 0)
    count = a["count"]
    stride = bv.get("byteStride") or n * np.dtype(dt).itemsize
    if stride == n * np.dtype(dt).itemsize:
        arr = np.frombuffer(buf, dt, count * n, off).reshape(count, n)
    else:  # strided
        out = np.empty((count, n), dt)
        for i in range(count):
            out[i] = np.frombuffer(buf, dt, n, off + i * stride)
        arr = out
    if a.get("normalized"):
        f = np.iinfo(dt).max
        arr = np.clip(arr.astype(np.float32) / f, -1.0, 1.0)
    return arr.copy() if arr.dtype != dt else arr


def trs(node):
    if "matrix" in node:
        return np.array(node["matrix"], np.float64).reshape(4, 4).T
    m = np.eye(4)
    r = node.get("rotation", [0, 0, 0, 1])
    x, y, z, w = r
    rot = np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])
    s = node.get("scale", [1, 1, 1])
    m[:3, :3] = rot * np.asarray(s)
    m[:3, 3] = node.get("translation", [0, 0, 0])
    return m


def world_transforms(g):
    n = len(g["nodes"])
    parent = {}
    for i, nd in enumerate(g["nodes"]):
        for c in nd.get("children", []):
            parent[c] = i
    world = [None] * n

    def solve(i):
        if world[i] is None:
            local = trs(g["nodes"][i])
            world[i] = local if i not in parent else world_at(parent[i]) @ local
        return world[i]

    def world_at(i):
        return solve(i)

    for i in range(n):
        solve(i)
    return world, parent


def skin_positions(g, buf, prim, world, skin):
    pos = accessor(g, buf, prim["attributes"]["POSITION"]).astype(np.float64)
    if skin is None or "JOINTS_0" not in prim["attributes"]:
        return pos
    joints = accessor(g, buf, prim["attributes"]["JOINTS_0"]).astype(np.int64)
    weights = accessor(g, buf, prim["attributes"]["WEIGHTS_0"]).astype(np.float64)
    wsum = weights.sum(1, keepdims=True)
    weights = np.divide(weights, wsum, where=wsum > 0, out=np.zeros_like(weights))
    ibm = accessor(g, buf, skin["inverseBindMatrices"]).astype(np.float64)
    ibm = ibm.reshape(-1, 4, 4).transpose(0, 2, 1)  # column-major -> row-major
    sj = np.array(skin["joints"])
    jmats = np.stack([world[sj[i]] @ ibm[i] for i in range(len(sj))])  # (J,4,4)
    hom = np.concatenate([pos, np.ones((len(pos), 1))], 1)
    out = np.zeros_like(hom)
    for k in range(joints.shape[1]):
        m = jmats[joints[:, k]]                      # (V,4,4)
        out += weights[:, k, None] * np.einsum("vij,vj->vi", m, hom)
    return out[:, :3]


def render(pos, faces, uv, tex, size, cam_dir, up_hint=np.array([0, 1, 0])):
    """Z-buffered textured rasterizer. cam_dir: unit vector from model to camera."""
    mn, mx = pos.min(0), pos.max(0)
    center = (mn + mx) / 2
    radius = max((mx - mn).max() / 2, 1e-6)
    fwd = -np.asarray(cam_dir, np.float64)
    fwd /= np.linalg.norm(fwd)
    right = np.cross(fwd, up_hint)
    right /= np.linalg.norm(right)
    up = np.cross(right, fwd)
    eye = center - fwd * radius * 3.0
    rel = pos - eye
    cx, cy, cz = rel @ right, rel @ up, rel @ fwd
    f = 1.0 / math.tan(math.radians(45) / 2)
    sx = cx * f / np.clip(cz, 1e-6, None) * size / 2 + size / 2
    sy = -cy * f / np.clip(cz, 1e-6, None) * size / 2 + size / 2
    img = np.full((size, size, 3), 32, np.uint8)
    zbuf = np.full((size, size), np.inf)
    texa = np.asarray(tex, np.float32) / 255.0 if tex is not None else None
    th, tw = texa.shape[:2] if texa is not None else (0, 0)
    light = fwd * -1.0

    tris = faces.reshape(-1, 3)
    for t in tris:
        xs, ys, zs = sx[t], sy[t], cz[t]
        x0, x1 = int(np.floor(xs.min())), int(np.ceil(xs.max()))
        y0, y1 = int(np.floor(ys.min())), int(np.ceil(ys.max()))
        if x1 < 0 or y1 < 0 or x0 >= size or y0 >= size:
            continue
        x0, x1 = max(0, x0), min(size - 1, x1)
        y0, y1 = max(0, y0), min(size - 1, y1)
        d = (xs[1] - xs[0]) * (ys[2] - ys[0]) - (xs[2] - xs[0]) * (ys[1] - ys[0])
        if abs(d) < 1e-9:
            continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        w0 = ((xs[1] - gx) * (ys[2] - gy) - (xs[2] - gx) * (ys[1] - gy)) / d
        w1 = ((xs[2] - gx) * (ys[0] - gy) - (xs[0] - gx) * (ys[2] - gy)) / d
        w2 = 1 - w0 - w1
        m = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
        if not m.any():
            continue
        z = w0 * zs[0] + w1 * zs[1] + w2 * zs[2]
        sub = zbuf[y0:y1 + 1, x0:x1 + 1]
        m &= z < sub
        if not m.any():
            continue
        sub[m] = z[m]
        # flat normal lighting
        e1, e2 = pos[t[1]] - pos[t[0]], pos[t[2]] - pos[t[0]]
        nrm = np.cross(e1, e2)
        nl = np.linalg.norm(nrm)
        shade = 0.35 + 0.65 * abs(np.dot(nrm / nl, light)) if nl > 0 else 0.35
        if texa is not None and uv is not None:
            u = w0 * uv[t[0], 0] + w1 * uv[t[1], 0] + w2 * uv[t[2], 0]
            v = w0 * uv[t[0], 1] + w1 * uv[t[1], 1] + w2 * uv[t[2], 1]
            ti = np.clip((u % 1) * tw, 0, tw - 1).astype(int)
            tj = np.clip((v % 1) * th, 0, th - 1).astype(int)
            col = texa[tj[m], ti[m], :3] * shade
        else:
            col = np.full((m.sum(), 3), 0.7 * shade)
        img[y0:y1 + 1, x0:x1 + 1][m] = (np.clip(col, 0, 1) * 255).astype(np.uint8)
    return img


def hierarchy_report(g, parent, world):
    lines = []
    children = {i: n.get("children", []) for i, n in enumerate(g["nodes"])}
    roots = [i for i in range(len(g["nodes"])) if i not in parent]

    def walk(i, depth):
        nd = g["nodes"][i]
        t = world[i][:3, 3]
        lines.append({"node": i, "name": nd.get("name", f"node{i}"),
                      "depth": depth, "parent": parent.get(i),
                      "worldPos": [round(float(v), 4) for v in t]})
        for c in children[i]:
            walk(c, depth + 1)

    for r in roots:
        walk(r, 0)
    return lines


def main():
    src, outd = Path(sys.argv[1]), Path(sys.argv[2])
    size = int(sys.argv[3]) if len(sys.argv) > 3 else 512
    outd.mkdir(parents=True, exist_ok=True)
    g, buf = load_gltf(src)
    world, parent = world_transforms(g)
    skin = g["skins"][0] if g.get("skins") else None

    tex = None
    if g.get("images"):
        uri = g["images"][0]["uri"]
        tex = Image.open(io.BytesIO(base64.b64decode(uri.split(",", 1)[1]))
                         if uri.startswith("data:") else src.parent / uri).convert("RGBA")

    mesh_world = {}
    for i, nd in enumerate(g["nodes"]):
        if "mesh" in nd and nd["mesh"] not in mesh_world:
            mesh_world[nd["mesh"]] = world[i]
    allpos, prims = [], []
    for mi, mesh in enumerate(g["meshes"]):
        for prim in mesh["primitives"]:
            pos = skin_positions(g, buf, prim, world, skin)
            # skinned prims already carry world via joint matrices; non-skinned
            # need the node's world transform applied explicitly
            if skin is None or "JOINTS_0" not in prim["attributes"]:
                mw = mesh_world.get(mi)
                if mw is not None:
                    pos = (pos @ mw[:3, :3].T) + mw[:3, 3]
            if "indices" in prim:
                faces = accessor(g, buf, prim["indices"]).astype(np.int64).reshape(-1, 3)
            else:
                faces = np.arange(len(pos), dtype=np.int64).reshape(-1, 3)
            uv = accessor(g, buf, prim["attributes"]["TEXCOORD_0"]).astype(np.float64) \
                if "TEXCOORD_0" in prim["attributes"] else None
            allpos.append(pos)
            prims.append((faces, uv))
    pos = np.concatenate(allpos)

    cams = {"front": [0, 0, 1], "back": [0, 0, -1], "left": [-1, 0, 0],
            "right": [1, 0, 0], "top": [0, 1, 0.15], "persp": [0.7, 0.4, 0.7]}
    outs = {}
    for name, d in cams.items():
        img = np.zeros((size, size, 3), np.uint8)
        img[:] = 32
        # single z-buffer across prims: render each prim into shared buffers
        # (simplest correct: render prims with shared zbuf -> merge in one pass)
        zbuf = np.full((size, size), np.inf)
        for faces, uv in prims:
            # reuse render() internals per-prim against shared buffers
            mn, mx = pos.min(0), pos.max(0)
            center = (mn + mx) / 2
            radius = max((mx - mn).max() / 2, 1e-6)
            fwd = -np.asarray(d, np.float64); fwd /= np.linalg.norm(fwd)
            right = np.cross(fwd, [0, 1, 0]); right /= np.linalg.norm(right)
            up = np.cross(right, fwd)
            eye = center - fwd * radius * 3.0
            rel = pos - eye
            cx, cy, cz = rel @ right, rel @ up, rel @ fwd
            f = 1.0 / math.tan(math.radians(45) / 2)
            sx = cx * f / np.clip(cz, 1e-6, None) * size / 2 + size / 2
            sy = -cy * f / np.clip(cz, 1e-6, None) * size / 2 + size / 2
            texa = np.asarray(tex, np.float32) / 255.0 if tex is not None else None
            th, tw = texa.shape[:2] if texa is not None else (0, 0)
            light = -fwd
            for t in faces:
                xs, ys, zs = sx[t], sy[t], cz[t]
                x0, x1 = int(np.floor(xs.min())), int(np.ceil(xs.max()))
                y0, y1 = int(np.floor(ys.min())), int(np.ceil(ys.max()))
                if x1 < 0 or y1 < 0 or x0 >= size or y0 >= size:
                    continue
                x0, x1 = max(0, x0), min(size - 1, x1)
                y0, y1 = max(0, y0), min(size - 1, y1)
                dd = (xs[1] - xs[0]) * (ys[2] - ys[0]) - (xs[2] - xs[0]) * (ys[1] - ys[0])
                if abs(dd) < 1e-9:
                    continue
                gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + .5, np.arange(y0, y1 + 1) + .5)
                w0 = ((xs[1] - gx) * (ys[2] - gy) - (xs[2] - gx) * (ys[1] - gy)) / dd
                w1 = ((xs[2] - gx) * (ys[0] - gy) - (xs[0] - gx) * (ys[2] - gy)) / dd
                w2 = 1 - w0 - w1
                m = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
                if not m.any():
                    continue
                z = w0 * zs[0] + w1 * zs[1] + w2 * zs[2]
                sub = zbuf[y0:y1 + 1, x0:x1 + 1]
                m &= z < sub
                if not m.any():
                    continue
                sub[m] = z[m]
                e1, e2 = pos[t[1]] - pos[t[0]], pos[t[2]] - pos[t[0]]
                nrm = np.cross(e1, e2); nl = np.linalg.norm(nrm)
                shade = 0.35 + 0.65 * abs(np.dot(nrm / nl, light)) if nl > 0 else 0.35
                if texa is not None and uv is not None:
                    u = w0 * uv[t[0], 0] + w1 * uv[t[1], 0] + w2 * uv[t[2], 0]
                    v = w0 * uv[t[0], 1] + w1 * uv[t[1], 1] + w2 * uv[t[2], 1]
                    ti = np.clip((u % 1) * tw, 0, tw - 1).astype(int)
                    tj = np.clip((v % 1) * th, 0, th - 1).astype(int)
                    col = texa[tj[m], ti[m], :3] * shade
                else:
                    col = np.full((m.sum(), 3), 0.7 * shade)
                img[y0:y1 + 1, x0:x1 + 1][m] = (np.clip(col, 0, 1) * 255).astype(np.uint8)
        op = outd / f"{src.stem}_{name}.png"
        Image.fromarray(img).save(op)
        outs[name] = op.name

    hier = hierarchy_report(g, parent, world)
    report = {
        "input": src.name,
        "nodes": len(g["nodes"]),
        "joints": len(skin["joints"]) if skin else 0,
        "skinned": skin is not None,
        "animations": len(g.get("animations", [])),
        "primitives": len(prims),
        "vertices": int(len(pos)),
        "bounds": {"min": pos.min(0).tolist(), "max": pos.max(0).tolist()},
        "texture": g["images"][0]["uri"] if g.get("images") else None,
        "renders": outs,
        "hierarchy": hier,
    }
    (outd / f"{src.stem}_report.json").write_text(json.dumps(report, indent=1))
    print(json.dumps({k: v for k, v in report.items() if k != "hierarchy"}, indent=1))
    print(f"hierarchy: {len(hier)} nodes, roots={[h['node'] for h in hier if h['depth'] == 0]}")


if __name__ == "__main__":
    main()
