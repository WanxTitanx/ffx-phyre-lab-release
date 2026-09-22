#!/usr/bin/env python3
# ── PHYRE-LAB .chr SKL rest-pose editor (P22) ────────────────────────────────
# In-place edit of the SKL boneTable rest pose (20B per bone):
#   i16 parent | i16 rotXYZ (rad*18000/pi) | i16 trXYZ (mm/1000)
#   | i16 scXYZ (unit*4096)
# Offsets are relative to the SKL block start (sec[0] offset); boneTable at
# skl+u32(+0x1C). Edits are quantized to the format's own i16 units — the
# written value is the nearest representable value, reported back.
#
# Usage:
#   chr_edit.py set  <file.chr> <bone> <field> <v0> [v1] [v2] [--out dst.chr]
#     field: rot|tr|sc — rot args in radians, tr in units, sc unitless
#     default is in-place on a user-owned copy; --out preserves the input.
#   chr_edit.py info <file.chr> <bone>            (dump one bone)
# Exit: 0 ok; 2 refusal (OOB bone/field/value unrepresentable).
import struct
import sys

sys.path.insert(0, __file__.rsplit("/", 1)[0])
from chr_check import Chr, u32  # noqa: E402

FIELDS = {"rot": (1, lambda x, c: round(x * 18000.0 / 3.141592653589793)),
          "tr": (4, lambda x, c: round(x * 1000.0)),
          "sc": (7, lambda x, c: round(x * 4096.0))}


def main():
    a = sys.argv
    if len(a) < 4:
        print(__doc__)
        return 1
    c = Chr(a[2])
    s = c.skl()
    if not s:
        print("ERR: no SKL", file=sys.stderr)
        return 2
    bt = s["off"] + u32(c.d, s["off"] + 0x1C)
    bone = int(a[3])
    if not (0 <= bone < s["boneCount"]):
        print(f"ERR: bone {bone} >= {s['boneCount']}", file=sys.stderr)
        return 2
    if a[1] == "info":
        print(c.d[bt + 20 * bone:bt + 20 * bone + 20].hex(),
              s["bones"][bone])
        return 0
    field = a[4]
    if field not in FIELDS:
        return 2
    base, q = FIELDS[field]
    vals = []
    out_path = a[2]
    it = iter(a[5:11])
    for x in it:
        if x == "--out":
            out_path = next(it, None)
            if out_path is None:
                return 2
            break
        vals.append(float(x))
    d = bytearray(c.d)
    out = []
    for k, v in enumerate(vals):
        raw = q(v, c)
        if not (-32768 <= raw <= 32767):
            print(f"ERR: {field}[{k}]={v} -> i16 {raw} unrepresentable",
                  file=sys.stderr)
            return 2
        struct.pack_into("<h", d, bt + 20 * bone + 2 * (base + k), raw)
        out.append(raw)
    open(out_path, "wb").write(d)
    # report quantized values actually written
    inv = {"rot": 3.141592653589793 / 18000.0, "tr": 1 / 1000.0,
           "sc": 1 / 4096.0}[field]
    print(f"bone{bone}.{field} wrote i16={out} "
          f"-> {[round(v * inv, 5) for v in out]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
