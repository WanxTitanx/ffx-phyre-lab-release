#!/usr/bin/env python3
# ── PHYRE-LAB .mgrp motion decoder (P19) ─────────────────────────────────────
# Decodes FFX PS2-era body animation to per-frame, per-target TRS tracks.
#
# Format provenance (RE, FFX.exe sha256 78ce3439…, base 0x400000):
#   container/records   FFX_Mgrp_RelocateMseqRecordPointers @0x837040
#   seqProg bytecode    StepScriptVM @0x837980 (op0..6)
#   a2 header + modes   FFX_Mseq_InitChannelTracks @0x839A00
#   keyed delta-RLE     FFX_Mseq_AdvanceKeyedChannelCursors @0x839550
#   per-frame eval      FFX_Mseq_EvaluateKeyedChannelsAtFrame @0x839440
#   dequant -> TRS      FFX_Mseq_WriteSampledTransformChannels @0x838300
#   s16 -> unit float   FFX_Mseq_S16ToUnitFloat @0x839E50 (/4096)
# Derived from docs/reverse/FFX_*MGRP* + Ps2MgrpAnimationReader.cs
# (same owner, lane ffx-editor). Corrections from the 2026-09-19 sonda
# (record dummies, compact emitter, tag bitmask, real SIZE LAW) included.
#
# Usage:
#   mgrp_decode.py dump    <file.mgrp>
#   mgrp_decode.py tracks  <file.mgrp> <record> <clip> [--json out.json]
#   mgrp_decode.py seqprog <file.mgrp> <record> <progIdx>
#   mgrp_decode.py verify  <file.mgrp>          (decode all clips, stats)
# Exit: 0 ok; 2 decode/structural failure; 1 usage.
import json
import math
import struct
import sys

COMP = ["rotX", "rotY", "rotZ", "trX", "trY", "trZ", "scX", "scY", "scZ"]


def u16(d, o): return struct.unpack_from("<H", d, o)[0]
def s16(d, o): return struct.unpack_from("<h", d, o)[0]
def u32(d, o): return struct.unpack_from("<I", d, o)[0]


def sx(v, bits):
    m = 1 << (bits - 1)
    return (v ^ m) - m


def unit(s):
    return sx(s & 0xFFFF, 16) / 4096.0


class Mgrp:
    def __init__(self, path):
        d = open(path, "rb").read()
        self.d = d
        self.path = path
        if len(d) == 16 and d == b"\x00" * 12 + b"\x10\x00\x00\x00":
            self.stub = True
            self.mc = 0
            self.records = []
            return
        self.stub = False
        if len(d) < 0x14:
            raise ValueError("tiny")
        if u32(d, 0) != 0 or u32(d, 8) != 0:
            raise ValueError("header flag/reserved nonzero")
        self.mc, self.dl = u32(d, 4), u32(d, 0xC)
        if self.dl + self.mc * 20 != len(d):
            raise ValueError("SIZE LAW")
        self.records = []
        for r in range(self.mc):
            ro = self.dl + r * 20
            self.records.append({
                "f0": u32(d, ro), "subid": u32(d, ro + 4),
                "a": u16(d, ro + 8), "b": u16(d, ro + 10),
                "offA": u32(d, ro + 12), "offB": u32(d, ro + 16)})

    def seqprogs(self, rec):
        """tableA entries -> disassembled programs."""
        d, o = self.d, rec["offA"]
        out = []
        for i in range(rec["a"]):
            e = {"clipId": u16(d, o + 16 * i), "key": u16(d, o + 16 * i + 2),
                 "flags": u16(d, o + 16 * i + 4), "u6": u16(d, o + 16 * i + 6),
                 "lbl": u32(d, o + 16 * i + 8), "code": u32(d, o + 16 * i + 12)}
            e["ops"] = disasm(d, e["code"])
            out.append(e)
        return out

    def clips(self, rec):
        """tableB entries -> {f0, tag, segFrameTbl, blob}."""
        d, o = self.d, rec["offB"]
        out = []
        for i in range(rec["b"]):
            e = o + 16 * i
            out.append({"f0": u32(d, e), "tag": u32(d, e + 4),
                        "sft": u32(d, e + 8), "blob": u32(d, e + 12)})
        return out

    def decode_clip(self, blob):
        """a2 blob -> {frames, targets, rate, extra, channels:[{t,c,mode,
        const|samples}]}. Raises ValueError on structural failure."""
        d = self.d
        fc, tc = u16(d, blob), u16(d, blob + 2)
        rate, xc = u16(d, blob + 4), u16(d, blob + 6)
        mo, ko, xo = u32(d, blob + 8), u32(d, blob + 12), u32(d, blob + 16)
        if fc == 0 or tc == 0 or rate == 0:
            return {"frames": fc, "targets": tc, "rate": rate, "extra": xc,
                    "channels": [], "null": True}
        mode_base, val_base = blob + mo, blob + ko
        nch = 9 * tc
        if mode_base + (nch * 2 + 7) // 8 > len(d) or val_base >= len(d):
            raise ValueError("mode/value region OOB")
        vp = val_base
        channels = []
        for c in range(nch):
            mode = (d[mode_base + (c * 2) // 8] >> ((c * 2) % 8)) & 3
            ch = {"t": c // 9, "c": c % 9, "mode": mode}
            if mode == 0:
                ch["const"] = 0.0
            elif mode == 1:
                ch["const"] = 1.0
            elif mode == 2:
                ch["const"] = unit(s16(d, vp))
                vp += 2
            else:
                L = u16(d, vp)
                if L < 2 or vp + L > len(d):
                    raise ValueError(f"keyed stream OOB ch{c}")
                ch["samples"] = decode_rle(d, vp + 2, fc)
                vp += L
            channels.append(ch)
        return {"frames": fc, "targets": tc, "rate": rate, "extra": xc,
                "extraOff": xo, "channels": channels,
                "valConsumed": vp - val_base}


def decode_rle(d, pos, n):
    """FFX_Mseq_AdvanceKeyedChannelCursors: n samples; delta persists across
    run bytes. Returns list[int] raw s16 samples."""
    delta = sample = run = 0
    out = []
    for _ in range(n):
        if run:
            run -= 1
        elif pos < len(d):
            c = d[pos]
            pos += 1
            if c < 0x80:
                delta = sx(c & 0x7F, 7)
            elif c & 0x40:
                if pos < len(d):
                    delta = sx((c & 0x3F) | (d[pos] << 6), 14)
                    pos += 1
            else:
                run = c & 0x3F
        sample = sx((sample + delta) & 0xFFFF, 16)
        out.append(sample)
    return out


def disasm(d, code):
    """seqProg bytecode -> list of (name, operands). PLAY operands:
    clipTblIdx i16, repeat i16, segIdx i16, mode i16."""
    ops = []
    pc = code
    for _ in range(4096):
        if pc >= len(d):
            ops.append(("TRUNC", ()))
            return ops
        op = d[pc]
        if op in (0, 5):
            ops.append(("END" if op == 0 else "ENDREL", ()))
            return ops
        if op == 1:
            ops.append(("PLAY", (s16(d, pc + 1), s16(d, pc + 3),
                                 s16(d, pc + 5), s16(d, pc + 7))))
            pc += 9
        elif op == 2:
            ops.append(("GATE", ()))
            pc += 1
        elif op in (3, 4, 6):
            ops.append(({3: "WAIT", 4: "GOTO", 6: "WAITCNT"}[op],
                        (s16(d, pc + 1),)))
            pc += 3
        else:
            ops.append((f"BADOP_{op:#x}", ()))
            return ops
    ops.append(("OVERFLOW", ()))
    return ops


def wrap_pi(a):
    while a > math.pi:
        a -= 2 * math.pi
    while a < -math.pi:
        a += 2 * math.pi
    return a


def target_trs(clip, target, frame, inst_scale=0.001):
    """Dequantized local TRS of one target at integer frame.
    rot = unit*2pi (wrapped); trans = unit*instScale*4096; scale = unit."""
    tr = {"rot": [0.0, 0.0, 0.0], "tr": [0.0, 0.0, 0.0],
          "sc": [1.0, 1.0, 1.0]}
    for ch in clip["channels"]:
        if ch["t"] != target:
            continue
        v = ch["const"] if ch["mode"] != 3 else unit(
            ch["samples"][min(frame, len(ch["samples"]) - 1)])
        c = ch["c"]
        if c < 3:
            tr["rot"][c] = wrap_pi(2 * math.pi * v)
        elif c < 6:
            tr["tr"][c - 3] = v * inst_scale * 4096.0
        else:
            tr["sc"][c - 6] = v
    return tr


def target_trs_lerp(clip, target, frame_fx, inst_scale=0.001):
    """Sub-frame sample: fixed .8 frame cursor, LERP adjacent frames;
    rotation channels wrap (shortest arc)."""
    f0 = int(frame_fx) >> 8
    frac = (frame_fx & 0xFF) / 256.0
    a = target_trs(clip, target, f0, inst_scale)
    if frac == 0 or f0 + 1 >= clip["frames"]:
        return a
    b = target_trs(clip, target, f0 + 1, inst_scale)
    out = {"rot": [0.0] * 3, "tr": [0.0] * 3, "sc": [1.0] * 3}
    for i in range(3):
        dr = b["rot"][i] - a["rot"][i]
        if dr > math.pi:
            dr -= 2 * math.pi
        elif dr < -math.pi:
            dr += 2 * math.pi
        out["rot"][i] = a["rot"][i] + dr * frac
        out["tr"][i] = a["tr"][i] + (b["tr"][i] - a["tr"][i]) * frac
        out["sc"][i] = a["sc"][i] + (b["sc"][i] - a["sc"][i]) * frac
    return out


def cmd_dump(f):
    print(f"file={f.path} size={len(f.d)} stub={f.stub} mc={f.mc}")
    for i, r in enumerate(f.records):
        kind = "dummy" if (r["a"] == 0 and r["b"] == 0) else ""
        print(f" rec{i}: subid={r['subid']:#06x} a={r['a']} b={r['b']} "
              f"offA={r['offA']:#x} offB={r['offB']:#x} f0={r['f0']:#x} {kind}")
        for j, p in enumerate(f.seqprogs(r)):
            print(f"   prog{j}: clipId={p['clipId']:#x} key={p['key']:#x} "
                  f"flags={p['flags']} ops={len(p['ops'])}")
        for j, c in enumerate(f.clips(r)):
            try:
                cl = f.decode_clip(c["blob"])
                n_key = sum(1 for x in cl["channels"] if x["mode"] == 3)
                print(f"   clip{j}: tag={c['tag']:#x} frames={cl['frames']} "
                      f"targets={cl['targets']} rate={cl['rate']} "
                      f"extra={cl['extra']} keyed={n_key} "
                      f"null={cl.get('null', False)}")
            except ValueError as e:
                print(f"   clip{j}: DECODE FAIL {e}")


def cmd_tracks(f, ri, ci, json_path=None):
    rec = f.records[ri]
    clip = f.decode_clip(f.clips(rec)[ci]["blob"])
    tracks = {}
    for t in range(clip["targets"]):
        tracks[t] = [target_trs(clip, t, fr) for fr in range(clip["frames"])]
    if json_path:
        json.dump({"file": f.path, "record": ri, "clip": ci,
                   "frames": clip["frames"], "rate": clip["rate"],
                   "targets": clip["targets"], "tracks": tracks},
                  open(json_path, "w"))
        print(f"wrote {json_path}: {clip['frames']}f x {clip['targets']}t")
        return
    # human summary: per-target min/max range of each TRS comp
    for t in range(clip["targets"]):
        tr = tracks[t]
        rng = {}
        for grp in ("rot", "tr", "sc"):
            for k in range(3):
                vals = [x[grp][k] for x in tr]
                rng[f"{grp}{k}"] = (min(vals), max(vals))
        moved = [k for k, (a, b) in rng.items() if abs(b - a) > 1e-6]
        print(f" target{t:3d}: moved={len(moved)} " +
              " ".join(f"{k}:[{rng[k][0]:.3f}..{rng[k][1]:.3f}]"
                       for k in moved[:6]))


def cmd_verify(f):
    tot = {"clips": 0, "null": 0, "frames": 0, "keyed": 0, "bad": 0}
    for r in f.records:
        for c in f.clips(r):
            try:
                cl = f.decode_clip(c["blob"])
            except ValueError:
                tot["bad"] += 1
                continue
            if cl.get("null"):
                tot["null"] += 1
                continue
            tot["clips"] += 1
            tot["frames"] += cl["frames"]
            tot["keyed"] += sum(1 for x in cl["channels"] if x["mode"] == 3)
    print(f"{f.path}: clips={tot['clips']} null={tot['null']} "
          f"frames={tot['frames']} keyedStreams={tot['keyed']} "
          f"bad={tot['bad']}")
    return 2 if tot["bad"] else 0


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 1
    mode, path = argv[1], argv[2]
    f = Mgrp(path)
    if mode == "dump":
        cmd_dump(f)
        return 0
    if mode == "verify":
        return cmd_verify(f)
    if mode == "tracks":
        jp = argv[argv.index("--json") + 1] if "--json" in argv else None
        cmd_tracks(f, int(argv[3]), int(argv[4]), jp)
        return 0
    if mode == "seqprog":
        for i, op in enumerate(f.seqprogs(f.records[int(argv[3])])
                               [int(argv[4])]["ops"]):
            print(f"  {i:3d}: {op[0]} {op[1]}")
        return 0
    print(__doc__)
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
