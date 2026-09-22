#!/usr/bin/env python3
# ── PHYRE-LAB .mgrp (motion-group) structural checker ────────────────────────
# Validates the Square PS2-era animation container used by FFX HD PC:
#   header(20B) -> data region -> record table (tail, 20B/record)
#   record -> tableA seqProg bytecode + tableB clip entries -> a2 blob
#   a2: 2-bit channel modes (target-major, 9 comps/bone) + value region
#       (const s16 | keyed {u16 len | delta-RLE 7/14-bit + run})
#
# Derived from ffx-editor research_tools/Ps2/mgrp_census.py +
# Ps2MgrpAnimationReader.cs (same owner, Jarvis lane; RE provenance:
# FFX.exe sha256 78ce3439…, FFX_Mseq_* @0x837040/0x839550/0x839A00,
# docs/reverse/FFX_MODELS_UNLOCK_MGRP_SONDA_2026-09-19.md).
# Corrections folded in: record dummies {subid=0,a=0,b=0,offA=offB=0x10},
# compact emitter modeOff=0x10, clip tag is a bitmask (not const 2),
# SIZE LAW is fileSize == dataLen + motionCount*20.
#
# Usage: mgrp_check.py <file-or-dir>...   (dirs scanned recursively)
# Exit: 0 = all files valid-or-stub; 2 = any anomaly; 1 = usage.
import os
import struct
import sys
from collections import Counter


def u16(d, o):
    return struct.unpack_from("<H", d, o)[0]


def s16(d, o):
    return struct.unpack_from("<h", d, o)[0]


def u32(d, o):
    return struct.unpack_from("<I", d, o)[0]


def disasm_walk(d, code):
    """Linear seqProg walk. ops: 0 END/5 ENDREL (1B, terminal),
    1 PLAY (9B), 2 GATE (1B), 3/4/6 WAIT/GOTO/WAITCNT (3B)."""
    pc, n = code, 0
    while n < 4096:
        if pc >= len(d):
            return -1, []
        op = d[pc]
        n += 1
        if op in (0, 5):
            return n, None
        if op == 1:
            if pc + 9 > len(d):
                return -1, []
            n += 0  # operands parsed by caller if needed
            pc += 9
        elif op == 2:
            pc += 1
        elif op in (3, 4, 6):
            if pc + 3 > len(d):
                return -1, []
            pc += 3
        else:
            return -1, []
    return -1, []


def play_operands(d, code):
    """Yield (segIdx, mode) for each PLAY op until terminator."""
    pc, out = code, []
    for _ in range(4096):
        if pc >= len(d):
            break
        op = d[pc]
        if op in (0, 5):
            break
        if op == 1:
            if pc + 9 > len(d):
                break
            out.append((s16(d, pc + 5), s16(d, pc + 7)))
            pc += 9
        elif op == 2:
            pc += 1
        elif op in (3, 4, 6):
            pc += 3
        else:
            break
    return out


def decode_rle_ok(d, pos, nframes):
    """Delta-RLE bounds check: consume control bytes until nframes produced.
    c<0x80 -> 7-bit delta (1B); c&0xC0==0xC0 -> 14-bit (2B); c&0x80 -> run."""
    for _ in range(nframes):
        if pos >= len(d):
            return False
        c = d[pos]
        if c < 0x80:
            pos += 1
        elif c & 0x40:
            pos += 2
        # run byte: holds current delta, consumes only itself
    return True


def check_file(path):
    """Returns (cls, stats dict). cls in {ok, stub16, tiny, SIZELAW_FAIL,
    RECPTR_OOB, PROG_ERR, CLIP_ERR, FLAG_NONZERO}."""
    r = {"recs": 0, "progs": 0, "clips": 0, "streams": 0, "dummies": 0,
         "plays": 0, "segidx>0": 0, "tags": Counter(), "modeoffs": Counter(),
         "modes": Counter(), "f0nz": 0, "subids": [], "targets": Counter(),
         "extras": 0, "nullclips": 0, "errors": []}
    d = open(path, "rb").read()
    r["size"] = len(d)
    if len(d) == 16 and d == b"\x00" * 12 + b"\x10\x00\x00\x00":
        return "stub16", r
    if len(d) < 0x14:
        return "tiny", r
    if u32(d, 0) != 0 or u32(d, 8) != 0:
        return "FLAG_NONZERO", r
    mc, dl = u32(d, 4), u32(d, 0xC)
    r["mc"] = mc
    if dl + mc * 20 != len(d):
        return "SIZELAW_FAIL", r
    for ri in range(mc):
        ro = dl + ri * 20
        if u32(d, ro) != 0:
            r["f0nz"] += 1
        subid = u32(d, ro + 4)
        ca, cb = u16(d, ro + 8), u16(d, ro + 10)
        oa, ob = u32(d, ro + 12), u32(d, ro + 16)
        r["subids"].append(subid)
        if ca == 0 and cb == 0:
            r["dummies"] += 1
            continue  # record dummy sentinel
        if not (0x14 <= oa < len(d) and 0x14 <= ob < len(d)):
            return "RECPTR_OOB", r
        r["recs"] += 1
        for i in range(ca):
            eo = oa + i * 16
            if eo + 16 > len(d):
                return "PROG_ERR", r
            code = u32(d, eo + 12)
            if not (0x14 <= code < len(d)):
                return "PROG_ERR", r
            n, _ = disasm_walk(d, code)
            if n < 0:
                return "PROG_ERR", r
            r["progs"] += 1
            for seg, md in play_operands(d, code):
                r["plays"] += 1
                if seg > 0:
                    r["segidx>0"] += 1
                r["modes"][md] += 1
        for i in range(cb):
            eo = ob + i * 16
            if eo + 16 > len(d):
                return "CLIP_ERR", r
            r["tags"][u32(d, eo + 4)] += 1
            sft, blob = u32(d, eo + 8), u32(d, eo + 12)
            if not (0x14 <= blob < len(d) and 0x10 <= sft < len(d)):
                return "CLIP_ERR", r
            fc, tc = u16(d, blob), u16(d, blob + 2)
            rate, xc = u16(d, blob + 4), u16(d, blob + 6)
            mo, ko = u32(d, blob + 8), u32(d, blob + 12)
            if fc == 0 or tc == 0 or rate == 0:
                # Degenerate null clip: chr/wep/w001/mot/resident1.mgrp is the
                # single corpus placeholder (rate=0, garbage tail fields).
                # Tolerated by the runtime; FFX_MODELS_UNLOCK_MGRP_SONDA (c).
                r["nullclips"] += 1
                continue
            if rate != 7680:
                r["errors"].append(f"clip{i}: rate={rate}")
            r["clips"] += 1
            r["targets"][tc] += 1
            r["modeoffs"][mo] += 1
            if xc:
                r["extras"] += 1
            mode_base, val_base = blob + mo, blob + ko
            nchan = 9 * tc
            if mode_base + (nchan * 2 + 7) // 8 > len(d) or val_base >= len(d):
                return "CLIP_ERR", r
            vp = val_base
            for c in range(nchan):
                mode = (d[mode_base + (c * 2) // 8] >> ((c * 2) % 8)) & 3
                if mode == 2:
                    vp += 2
                elif mode == 3:
                    if vp + 2 > len(d):
                        return "CLIP_ERR", r
                    L = u16(d, vp)
                    if L < 2 or vp + L > len(d):
                        return "CLIP_ERR", r
                    r["streams"] += 1
                    if not decode_rle_ok(d, vp + 2, fc):
                        r["errors"].append(f"clip{i}/ch{c}: rle truncated")
                    vp += L
            if vp > len(d):
                return "CLIP_ERR", r
    return "ok", r


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 1
    files = []
    for a in argv[1:]:
        if os.path.isdir(a):
            for dp, _, fns in os.walk(a):
                files += [os.path.join(dp, f) for f in fns
                          if f.lower().endswith(".mgrp")]
        else:
            files.append(a)
    files.sort()
    tot = Counter()
    agg = Counter()
    tags, modeoffs, modes, targets = Counter(), Counter(), Counter(), Counter()
    worst = []
    for p in files:
        cls, r = check_file(p)
        tot[cls] += 1
        for k in ("recs", "progs", "clips", "streams", "dummies", "plays",
                  "segidx>0", "f0nz", "extras", "nullclips"):
            agg[k] += r[k]
        tags += r["tags"]
        modeoffs += r["modeoffs"]
        modes += r["modes"]
        targets += r["targets"]
        if cls not in ("ok", "stub16"):
            worst.append((cls, p))
        elif r["errors"]:
            worst.append(("soft:" + ";".join(r["errors"][:3]), p))
        elif len(files) == 1 or "-v" in argv:
            print(f"{cls} {p}: recs={r['recs']} progs={r['progs']} "
                  f"clips={r['clips']} streams={r['streams']} "
                  f"plays={r['plays']} subids={[hex(s) for s in r['subids']]}")
    print(f"=== {len(files)} files ===")
    print("classes:", dict(tot))
    for k in ("recs", "dummies", "progs", "plays", "segidx>0", "clips",
              "streams", "extras", "f0nz", "nullclips"):
        print(f"  {k}: {agg[k]}")
    if tags:
        print("  clip tag bitmask top:", tags.most_common(6))
        print("  a2 modeOff top:", modeoffs.most_common(4))
        print("  PLAY mode operand top:", modes.most_common(8))
        print("  targetCount top:", targets.most_common(6))
    for cls, p in worst[:20]:
        print(f"ANOMALY {cls}: {p}")
    return 2 if worst else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
