#!/usr/bin/env python3
# ── PHYRE-LAB .mgrp clip editor (P20) ────────────────────────────────────────
# Conservative in-place edits on existing clips over the original rig:
#   freeze  – rewrite keyed RLE streams to hold frame0 (track edit)
#   const   – overwrite a mode-2 channel's i16 constant
#   timing  – set frameCount (shrink keeps streams valid; grow extrapolates
#             with persistent delta — documented, not silently "held")
#   synth   – replace a clip blob with a generated all-const clip when it
#             fits the original footprint (clipTable blob offsets bound it)
# Stream layout (rel. to blob): +0 fc u16, +2 tc u16, +4 rate u16,
#   +6 extraCount u16, +8 modeOff, +12 keyedOff, +16 extraOff.
# Mode stream = nch*2 bits at modeOff; keyed region walks channels in
# order, u16 length L + RLE payload per mode-3 channel, s16 per mode-2.
# Negatives: refuse oversized synth, OOB streams, non-mode2 const writes.
import struct
import sys

sys.path.insert(0, __file__.rsplit("/", 1)[0])
from mgrp_decode import Mgrp, u16, s16, u32  # noqa: E402

COMP = ["rotX", "rotY", "rotZ", "trX", "trY", "trZ", "scX", "scY", "scZ"]


def clip_hdr(d, blob):
    return {"fc": u16(d, blob), "tc": u16(d, blob + 2),
            "rate": u16(d, blob + 4), "xc": u16(d, blob + 6),
            "mo": u32(d, blob + 8), "ko": u32(d, blob + 12),
            "xo": u32(d, blob + 16)}


def channel_offsets(d, blob):
    """byte offset of each channel's payload in the keyed region."""
    h = clip_hdr(d, blob)
    nch = 9 * h["tc"]
    vp = blob + h["ko"]
    offs = []
    for c in range(nch):
        mode = (d[blob + h["mo"] + (c * 2) // 8] >> ((c * 2) % 8)) & 3
        offs.append((mode, vp))
        if mode == 2:
            vp += 2
        elif mode == 3:
            vp += u16(d, vp)
    return h, offs


def blob_footprint(mg, rec, clip_idx):
    """Space available for the blob = distance to next blob (or to the
    record table / EOF). Blobs are emitted consecutively in the corpus."""
    cl = mg.clips(rec)[clip_idx]
    start = cl["blob"]
    nxt = min([c["blob"] for r in mg.records
               for c in mg.clips(r) if c["blob"] > start] + [mg.dl])
    return start, nxt - start


def rle_hold(fc):
    """RLE tail that holds a value: delta=0 marker (0x00) then run bytes
    covering fc-1 samples — the RLE delta persists across run bytes, so
    holding REQUIRES zeroing it first."""
    out = bytearray([0x00])
    rem = fc - 1
    while rem > 0:
        n = min(rem, 0x3F)
        out.append(0x80 | n)
        rem -= n
    return bytes(out)


def do_freeze(path, rec_i, clip_i, targets):
    d = bytearray(open(path, "rb").read())
    mg = Mgrp(path)
    rec = mg.records[rec_i]
    h, offs = channel_offsets(d, mg.clips(rec)[clip_i]["blob"])
    n = 0
    for c, (mode, vp) in enumerate(offs):
        if mode != 3:
            continue
        t, comp = c // 9, c % 9
        if targets != "all" and t not in targets:
            continue
        L = u16(d, vp)
        tok = d[vp + 2:vp + 4] if d[vp + 2] >= 0xC0 else d[vp + 2:vp + 3]
        body = bytes(tok) + rle_hold(h["fc"])
        if len(body) > L - 2:
            raise ValueError(f"ch{c}: hold stream {len(body)} > L-2={L - 2}")
        d[vp + 2:vp + L] = body + bytes(L - 2 - len(body))
        n += 1
    open(path, "wb").write(d)
    return n


def do_const(path, rec_i, clip_i, ch_i, raw):
    d = bytearray(open(path, "rb").read())
    mg = Mgrp(path)
    _, offs = channel_offsets(d, mg.clips(mg.records[rec_i])[clip_i]["blob"])
    mode, vp = offs[ch_i]
    if mode != 2:
        raise ValueError(f"ch{ch_i} mode={mode}, not const-i16")
    struct.pack_into("<h", d, vp, raw)
    open(path, "wb").write(d)


def do_timing(path, rec_i, clip_i, frames):
    d = bytearray(open(path, "rb").read())
    mg = Mgrp(path)
    blob = mg.clips(mg.records[rec_i])[clip_i]["blob"]
    struct.pack_into("<H", d, blob, frames)
    open(path, "wb").write(d)


def do_synth(path, rec_i, clip_i, fc, tc):
    """All-mode2 const clip over an existing blob. Layout: header 0x18,
    mode bits at mo=0x18, i16 consts at ko, extra at end."""
    d = bytearray(open(path, "rb").read())
    mg = Mgrp(path)
    rec = mg.records[rec_i]
    start, foot = blob_footprint(mg, rec, clip_i)
    nch = 9 * tc
    mo = 0x18
    ko = mo + (nch * 2 + 7) // 8
    need = ko + nch * 2
    if need > foot:
        raise ValueError(f"synth {need}B > footprint {foot}B")
    blob = bytearray(foot)
    struct.pack_into("<HHHHIII", blob, 0, fc, tc, 7680, 0, mo, ko, ko + nch * 2)
    for c in range(nch):
        bi = mo + (c * 2) // 8
        blob[bi] |= 2 << ((c * 2) % 8)          # mode 2 = const i16
        struct.pack_into("<h", blob, ko + c * 2, 0)
    d[start:start + foot] = blob
    open(path, "wb").write(d)
    return foot


def main():
    a = sys.argv
    if len(a) < 4:
        print("usage: mgrp_edit.py <cmd> <file.mgrp> ...\n"
              "  info   <f> <rec> <clip>\n"
              "  freeze <f> <rec> <clip> [all|t1,t2,...]  (in-place)\n"
              "  const  <f> <rec> <clip> <ch> <i16>     (in-place)\n"
              "  timing <f> <rec> <clip> <frames>       (in-place)\n"
              "  synth  <f> <rec> <clip> <fc> <tc>    (in-place, fits check)")
        return 1
    cmd, path = a[1], a[2]
    try:
        if cmd == "info":
            mg = Mgrp(path)
            for i, c in enumerate(mg.clips(mg.records[int(a[3])])):
                h = clip_hdr(mg.d, c["blob"])
                print(f"clip{i} blob=0x{c['blob']:x} fc={h['fc']} "
                      f"tc={h['tc']} rate={h['rate']} xc={h['xc']} "
                      f"mo=0x{h['mo']:x} ko=0x{h['ko']:x} xo=0x{h['xo']:x}")
        elif cmd == "freeze":
            tg = a[5] if len(a) > 5 else "all"
            ts = "all" if tg == "all" else {int(x) for x in tg.split(",")}
            print(f"froze {do_freeze(path, int(a[3]), int(a[4]), ts)} streams")
        elif cmd == "const":
            do_const(path, int(a[3]), int(a[4]), int(a[5]), int(a[6]))
        elif cmd == "timing":
            do_timing(path, int(a[3]), int(a[4]), int(a[5]))
        elif cmd == "synth":
            foot = do_synth(path, int(a[3]), int(a[4]), int(a[5]), int(a[6]))
            print(f"synth clip written into {foot}B footprint")
        else:
            return 1
    except (ValueError, IndexError) as e:
        print(f"ERR: {e}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
