#!/usr/bin/env python3
# ── PHYRE-LAB pose baker (P19) ───────────────────────────────────────────────
# Composes .chr skeleton + .mgrp clip into per-frame bone WORLD transforms and
# renders stick-figure frames (PIL) as visual proof of decoded motion.
#
# Hypothesis under test (validated by targetCount == boneCount on c001/m002):
#   channel target i == skeleton bone i (identity map).
# Euler order flag exists because the on-disk order is unverified:
#   --order xyz|rzy   (bind-pose render is the ground truth check)
#
# Usage:
#   pose_bake.py render <file.chr> <file.mgrp> <rec> <clip> <outprefix.png>
#               [--order xyz] [--frames 0,N,2N...] [--instscale 0.001]
#   pose_bake.py bind <file.chr> <out.png>   (rest pose only)
import math
import os
import struct
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mgrp_decode import Mgrp, target_trs  # noqa: E402
from chr_check import Chr  # noqa: E402


def euler(rot, order):
    rx, ry, rz = rot
    cx, sx = math.cos(rx), math.sin(rx)
    cy, sy = math.cos(ry), math.sin(ry)
    cz, sz = math.cos(rz), math.sin(rz)
    Rx = np.array([[1, 0, 0, 0], [0, cx, -sx, 0], [0, sx, cx, 0],
                   [0, 0, 0, 1]], dtype=np.float64)
    Ry = np.array([[cy, 0, sy, 0], [0, 1, 0, 0], [-sy, 0, cy, 0],
                   [0, 0, 0, 1]], dtype=np.float64)
    Rz = np.array([[cz, -sz, 0, 0], [sz, cz, 0, 0], [0, 0, 1, 0],
                   [0, 0, 0, 1]], dtype=np.float64)
    m = {"x": Rx, "y": Ry, "z": Rz}
    R = m[order[0]] @ m[order[1]] @ m[order[2]]
    return R


def local_mat(trs, order):
    M = euler(trs["rot"], order)
    for i in range(3):
        M[i, i] *= trs["sc"][i]
        M[i, 3] = trs["tr"][i]
    return M


def world_mats(bones, locals_, order):
    """bones[i] = parent idx; locals_[i] = local TRS dict or ('M', mat4)."""
    n = len(bones)
    W = [None] * n
    for i in range(n):
        li = locals_[i]
        L = li[1] if isinstance(li, tuple) else local_mat(li, order)
        p = bones[i]
        W[i] = L if p == i else W[p] @ L
    return W


def draw_frame(W, bones, size, scale, cx, cy):
    img = Image.new("RGB", (size, size), (16, 16, 24))
    dr = ImageDraw.Draw(img)
    for i, p in enumerate(bones):
        if p == i:
            continue
        a, b = W[p], W[i]
        x0 = cx + a[0, 3] * scale
        y0 = cy - a[1, 3] * scale  # Y up
        x1 = cx + b[0, 3] * scale
        y1 = cy - b[1, 3] * scale
        dr.line([x0, y0, x1, y1], fill=(220, 200, 120), width=2)
        dr.ellipse([x1 - 2, y1 - 2, x1 + 2, y1 + 2], fill=(120, 220, 160))
    return img


def cmd_render(chr_path, mgrp_path, ri, ci, outprefix, order, frames_sel,
               inst_scale, delta_mode):
    c = Chr(chr_path)
    s = c.skl()
    bones = [b["parent"] for b in s["bones"]]
    bind_local = [{"rot": b["rot"], "tr": b["tr"], "sc": b["sc"]}
                  for b in s["bones"]]
    f = Mgrp(mgrp_path)
    rec = f.records[ri]
    clip = f.decode_clip(f.clips(rec)[ci]["blob"])
    nfr = clip["frames"]
    if frames_sel:
        frames = [min(int(x), nfr - 1) for x in frames_sel.split(",")]
    else:
        frames = sorted(set([0, nfr // 4, nfr // 2, 3 * nfr // 4, nfr - 1]))

    # retarget guard: channel targets map 1:1 onto rig bones — a clip with
    # more targets than the rig would silently drop bones' motion; fewer is
    # a partial-body clip (valid). Equality is the clean retarget contract.
    if clip["targets"] > len(bones):
        print(f"REFUSE: clip targets {clip['targets']} > rig bones "
              f"{len(bones)} — incompatible retarget", file=sys.stderr)
        return 2

    # first pass: bind pose to calibrate extent
    Wb = world_mats(bones, bind_local, order)
    ys = [Wb[i][1, 3] for i in range(len(bones))]
    xs = [Wb[i][0, 3] for i in range(len(bones))]
    span = max(max(xs) - min(xs), max(ys) - min(ys), 1e-6)
    size = 384
    scale = (size * 0.8) / span
    cx = size / 2 - (min(xs) + max(xs)) / 2 * scale
    cy = size / 2 + (min(ys) + max(ys)) / 2 * scale

    tiles = []
    for fr in frames:
        locs = []
        for t in range(len(bones)):
            if t < clip["targets"]:
                trs = target_trs(clip, t, fr, inst_scale)
            else:
                trs = bind_local[t]
            if delta_mode:
                # matrix delta: local = bind ∘ clip  (R_b·R_c, t_b+R_b·t_c·s_b,
                # s_b·s_c) — euler component addition is wrong under ±90° binds
                B = local_mat(bind_local[t], order)
                C = local_mat(trs, order)
                locs.append(("M", B @ C))
            else:
                locs.append(trs)
        W = world_mats(bones, locs, order)
        img = draw_frame(W, bones, size, scale, cx, cy)
        d = ImageDraw.Draw(img)
        d.text((8, 8), f"{os.path.basename(mgrp_path)} r{ri}c{ci} f{fr}",
               fill=(255, 255, 255))
        tiles.append(img)
    strip = Image.new("RGB", (size * len(tiles), size))
    for i, t in enumerate(tiles):
        strip.paste(t, (i * size, 0))
    strip.save(outprefix)
    print(f"wrote {outprefix} frames={frames} bones={len(bones)} "
          f"targets={clip['targets']} order={order}")
    return 0


def cmd_bind(chr_path, outpng, order):
    c = Chr(chr_path)
    s = c.skl()
    bones = [b["parent"] for b in s["bones"]]
    locs = [{"rot": b["rot"], "tr": b["tr"], "sc": b["sc"]}
            for b in s["bones"]]
    W = world_mats(bones, locs, order)
    xs = [W[i][0, 3] for i in range(len(bones))]
    ys = [W[i][1, 3] for i in range(len(bones))]
    span = max(max(xs) - min(xs), max(ys) - min(ys), 1e-6)
    size = 512
    scale = size * 0.8 / span
    img = draw_frame(W, bones, size, scale,
                     size / 2 - (min(xs) + max(xs)) / 2 * scale,
                     size / 2 + (min(ys) + max(ys)) / 2 * scale)
    img.save(outpng)
    print(f"wrote {outpng}: bind pose {len(bones)} bones order={order}")
    return 0


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 1
    mode = argv[1]
    order = "xyz"
    if "--order" in argv:
        order = argv[argv.index("--order") + 1]
    if mode == "bind":
        return cmd_bind(argv[2], argv[3], order)
    if mode == "render":
        frames = None
        if "--frames" in argv:
            frames = argv[argv.index("--frames") + 1]
        ins = 0.001
        if "--instscale" in argv:
            ins = float(argv[argv.index("--instscale") + 1])
        return cmd_render(argv[2], argv[3], int(argv[4]), int(argv[5]),
                          argv[6], order, frames, ins,
                          "--delta" in argv)
    print(__doc__)
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
