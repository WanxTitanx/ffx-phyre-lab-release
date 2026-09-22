#!/usr/bin/env python3
# ── PHYRE-LAB .chr (FFXMAP) container + SKL skeleton reader ──────────────────
# Provenance: FFX_CHR_SONDA_2026-09-19 (lane ffx-editor, same owner);
# runtime anchors FFX_Chr_InitFromFfxmapFile @0x825F60,
# FFX_Chr_RelocatePtrsInWorkBuffer @0x825770, deep-reloc @0x827610
# (FFX.exe sha256 78ce3439…, base 0x400000).
#
# Layout (verified on 865-file census upstream):
#   +0x00 u32 anchor=0 | +0x04 u32 sectionCount | +0x08 ? | +0x0C u32=0
#   +0x10 sections × 8B {u32 offset, u32 count}   (slots semânticos)
#   sec[0] SKL:  magic 0x1521@+4, meshParts@+6, boneCount@+0xA,
#                boneTable @ skl+0x1C (offset RELATIVE to skl block)
#   boneTable entry 20B = 10×i16:
#     {parentIdx, rotXYZ (π·x/18000 rad), transXYZ (/1000), scaleXYZ (/4096)}
#   sec[1] SG mesh dir, sec[9] params (u32 nominal size first),
#   sec[10] vertex pool tail.
#
# Usage:
#   chr_check.py info <file.chr>
#   chr_check.py skl  <file.chr>          (bone table dump)
#   chr_check.py census <dir>             (structural census, recursive)
# Exit: 0 ok; 2 anomaly; 1 usage.
import os
import struct
import sys
from collections import Counter


def u16(d, o): return struct.unpack_from("<H", d, o)[0]
def s16(d, o): return struct.unpack_from("<h", d, o)[0]
def u32(d, o): return struct.unpack_from("<I", d, o)[0]


class Chr:
    def __init__(self, path):
        d = open(path, "rb").read()
        self.d, self.path = d, path
        if len(d) < 0x10:
            raise ValueError("tiny")
        self.anchor = u32(d, 0)
        self.nsec = u32(d, 4)
        if self.anchor != 0 or self.nsec not in (10, 11):
            raise ValueError(f"bad header anchor={self.anchor} nsec={self.nsec}")
        if 0x10 + 8 * self.nsec > len(d):
            raise ValueError("section table OOB")
        self.sections = [(u32(d, 0x10 + 8 * i), u32(d, 0x10 + 8 * i + 4))
                         for i in range(self.nsec)]
        for i, (off, cnt) in enumerate(self.sections):
            if cnt and not (0x10 + 8 * self.nsec <= off < len(d)):
                raise ValueError(f"sec{i} offset {off:#x} OOB")

    def skl(self):
        """Returns dict with boneCount + list of bone dicts, or None.
        sec[0] presence is signalled by a nonzero in-bounds OFFSET — the
        count field is 0 on real files (verified m001/c001)."""
        off, cnt = self.sections[0]
        if off == 0 or off >= len(self.d):
            return None
        d = self.d
        magic = u16(d, off + 4)
        # 0x1521 is the common variant; 0x1017/0x1314/0x831 are older SKL
        # versions sharing the same field layout (validated: bone tables
        # still parse, parents in range). Underscore-prefixed sibling files
        # (_c001.chr etc.) are truncated stubs with magic=0 — rejected below
        # by the bone-table bounds check.
        nb = u16(d, off + 0xA)
        bt = off + u32(d, off + 0x1C)
        if nb == 0 or bt + 20 * nb > len(d):
            raise ValueError(f"skl v{magic:#x} boneTable OOB (nb={nb})")
        bones = []
        for i in range(nb):
            e = bt + 20 * i
            if e + 20 > len(d):
                raise ValueError("boneTable OOB")
            v = struct.unpack_from("<10h", d, e)
            bones.append({
                "parent": v[0],
                "rot": [x * 3.141592653589793 / 18000.0 for x in v[1:4]],
                "tr": [x / 1000.0 for x in v[4:7]],
                "sc": [x / 4096.0 for x in v[7:10]]})
        return {"off": off, "magic": magic, "meshParts": u16(d, off + 6),
                "boneCount": nb, "bones": bones,
                "meshDir": u32(d, off + 0x10), "skinDir": u32(d, off + 0x14)}


def cmd_info(c):
    print(f"{c.path}: size={len(c.d)} nsec={c.nsec}")
    for i, (off, cnt) in enumerate(c.sections):
        print(f"  sec{i:2d}: off={off:#08x} count={cnt}")
    s = c.skl()
    if s:
        print(f"  SKL v{s['magic']:#x}: bones={s['boneCount']} "
              f"meshParts={s['meshParts']} "
              f"meshDir={s['meshDir']:#x} skinDir={s['skinDir']:#x}")
        roots = sum(1 for i, b in enumerate(s["bones"]) if b["parent"] == i)
        print(f"  roots={roots} (parent==self)")


def cmd_skl(c):
    s = c.skl()
    if not s:
        print("no SKL")
        return
    for i, b in enumerate(s["bones"]):
        print(f"  bone{i:3d} parent={b['parent']:3d} "
              f"rot=({b['rot'][0]:+.4f},{b['rot'][1]:+.4f},{b['rot'][2]:+.4f}) "
              f"tr=({b['tr'][0]:+.4f},{b['tr'][1]:+.4f},{b['tr'][2]:+.4f}) "
              f"sc=({b['sc'][0]:.4f},{b['sc'][1]:.4f},{b['sc'][2]:.4f})")


def cmd_census(root):
    files = []
    for dp, _, fns in os.walk(root):
        files += [os.path.join(dp, f) for f in fns if f.lower().endswith(".chr")]
    files.sort()
    tot = Counter()
    nsec = Counter()
    bones = Counter()
    bad = []
    for p in files:
        try:
            c = Chr(p)
            s = c.skl()
            tot["ok"] += 1
            nsec[c.nsec] += 1
            if s:
                bones[s["boneCount"]] += 1
        except Exception as e:
            tot["bad"] += 1
            bad.append((str(e), p))
    print(f"=== {len(files)} .chr ===")
    print("classes:", dict(tot), "| nsec:", dict(nsec),
          "| top boneCount:", bones.most_common(8))
    for e, p in bad[:15]:
        print(f"BAD {e}: {p}")
    return 2 if bad else 0


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 1
    mode, path = argv[1], argv[2]
    if mode == "census":
        return cmd_census(path)
    c = Chr(path)
    if mode == "info":
        cmd_info(c)
        return 0
    if mode == "skl":
        cmd_skl(c)
        return 0
    print(__doc__)
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
