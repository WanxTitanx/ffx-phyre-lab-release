#!/usr/bin/env python3
"""map1_check.py — MAP1 (mapout.vpa) structural validator + walkmesh/zone census.

Port of the validated MAP1 codec (491/491 corpus) from the companion FFX
editor worktree (same owner): research_tools/BattleMap/map1_families.py —
FFX-STRUCTURES lane, docs FFX_CODEC_P1_MAP1_2026-09-14.md and
FFX_MAP1_FAMILIES_23_2026-09-15.md. Adapted for the PHYRE-LAB contract work
(P28): single-file check/info plus a corpus census.

Runtime model (IDA, FFX.exe PC):
  - FFX_FieldMap_ProcessMapDataBlob @0x9097C0 consumes magic 'MAP1'; the
    0x80 header is a DWORD slot table: v2[4]=+0x10 scene (YNDT/YNPR/YNSC/YNTM),
    v2[5]=+0x14 GS DMA packet, v2[6]=+0x18 geometry base (dispatch blobOffs
    are relative to it), v2[14]=+0x38 PPP resource (meta/dispatch),
    v2[15]=+0x3C guide map (YNDT -> Yn_GuideMapSetData), v2[16]=+0x40
    Yn_FpSetData.
  - Meta block: u32(meta+0x1C) = dispatchRel; rows of 8B {u16 key, u16 tag,
    u32 blobOff}; LAW: record size = 2*tag; rows sorted by offset tile the
    record region contiguously (98.7% corpus).
  - Zone tags 0x19/0x71/0x0004 mark zone windows over the record stream.

Content shapes of the record stream (validated layouts):
  soup32  32B {u16 id, u16 0, 3x u32 packed (s16 X,s16 Z) verts, 3x u32 RGBA,
               u16 p, u16 q}
  paint20 20B {u32 0x40 | tail 0x84, 3x RGBA, u16 j, u16 i}
  quad40  40B {4x u32 packed verts, 4x u32 RGBA, 4x u16 idx}  (grid or pool)
  edge24  24B {4x u32 RGBA, 4x u16 idx}
  range16 16B {u32 start, u32 end, u32 id, u16 f, u16 k} (PPP program entry)

Other families:
  s16framed (F1): 16B triangle records {3x (s16 X,s16 Z), u16 flags, -2}
    flags in {00,01,30,31,80,81,B0,B1} | 0x8000; scale 1/256 — encounter-zone
    rings (reference decoder: MapoutVpa_EncounterZones_Full.cs).
  float32 (F2): 0x32 transform records (identity scale, -4.0f sentinel);
    a separate sentinel-framed ring stream = navmesh (slot +0x18 region).
  YNDT/YNGM guide (F4) at slot +0x3C: 'YNDT','YNGM'@+0x10, u16 triCount @+0x38,
    u16 vertCount @+0x3A, const 68 @+0x3E, tris @+0x58 (20B {3x u32 sentinel
    colors, u16 iA,iB,iC, u16 0}), vertex pool IMMEDIATELY after (vc x 6B
    s16 X,Y,Z), 'YNED' end marker at variable distance.

Usage:
  map1_check.py check <mapout.vpa>     structural validation (exit 1 on fail)
  map1_check.py info  <mapout.vpa>     detailed dump (slots, runs, zones, yndt)
  map1_check.py census <root>          walk root for mapout.vpa, JSON summary
"""

import json
import os
import struct
import sys
from collections import Counter

HSZ = 0x80
ZONE_TAGS = (0x19, 0x71, 0x0004)
SENT_FLAGS = {0x00, 0x01, 0x30, 0x31, 0x80, 0x81, 0xB0, 0xB1}
S16_SCALE = 1.0 / 256.0
REL_LO, REL_HI = 0x80000000, 0x82000000
MAX_DISPATCH_ROWS = 512
MAX_F3_RECORDS = 8192
MAX_F1_TRIANGLES = 65536
CLASSIFY_SLOTS = 256


def u16(b, o): return struct.unpack_from('<H', b, o)[0]
def i16(b, o):
    v = u16(b, o)
    return v - 0x10000 if v >= 0x8000 else v
def u32(b, o): return struct.unpack_from('<I', b, o)[0]


def fourcc(b, o):
    if o < 0 or o + 4 > len(b):
        return None
    return b[o:o + 4].decode('ascii', 'replace').replace('\x00', '')


# ── header / dispatch ────────────────────────────────────────────────────────

def parse_header(b):
    """Slot-table view of the 0x80-byte MAP1 header (runtime model of 0x9097C0)."""
    names = {0x10: 'scene', 0x14: 'dma', 0x18: 'geom', 0x1C: 's7',
             0x20: 's8', 0x38: 'meta/ppp', 0x3C: 'guide', 0x40: 'fp'}
    slots = {}
    for off, name in names.items():
        v = u32(b, off) if len(b) >= off + 4 else 0
        slots[name] = {'off': v,
                       'magic': fourcc(b, v) if 0 < v < len(b) - 4 else None}
    return slots


def read_dispatch(b):
    """8B rows {key,tag,blobOff} until terminator/implausible offset."""
    geom = u32(b, 0x18) if len(b) >= 0x1C else 0
    meta = u32(b, 0x38) if len(b) >= 0x3C else 0
    if not (0 < meta < len(b) - 0x20):
        return geom, meta, None, []
    drel = u32(b, meta + 0x1C)
    dabs = meta + drel
    if not (0 < drel and dabs + 8 <= len(b)):
        return geom, meta, None, []
    rows = []
    pos = dabs
    for _ in range(MAX_DISPATCH_ROWS):
        if pos + 8 > len(b):
            break
        k, t, o = u16(b, pos), u16(b, pos + 2), u32(b, pos + 4)
        if k == 0 and t == 0 and o == 0:
            break
        if o < drel or o >= len(b) or geom + o + 0x20 > len(b):
            break
        rows.append({'key': k, 'tag': t, 'off': o, 'blob': geom + o})
        pos += 8
    return geom, meta, dabs, rows


def zone_rows(rows, b):
    return [r for r in rows
            if r['tag'] in ZONE_TAGS and 0 <= r['blob'] and r['blob'] + 0x32 <= len(b)]


# ── family 1: sentinel-framed s16 triangle stream (encounter zones) ──────────

def is_sent_pair(b, o):
    if o + 4 > len(b) or o % 2:
        return False
    return i16(b, o + 2) == -2 and (u16(b, o) & 0x7FFF) in SENT_FLAGS


def decode_s16_stream(b, start, end):
    tris = []
    o = start
    while o + 16 <= min(end, len(b)) and len(tris) < MAX_F1_TRIANGLES:
        if is_sent_pair(b, o + 12):
            verts = [(i16(b, o) * S16_SCALE, i16(b, o + 2) * S16_SCALE),
                     (i16(b, o + 4) * S16_SCALE, i16(b, o + 6) * S16_SCALE),
                     (i16(b, o + 8) * S16_SCALE, i16(b, o + 10) * S16_SCALE)]
            tris.append({'off': o, 'flags': u16(b, o + 12), 'verts': verts})
            o += 16
        else:
            o += 2
    return tris


def scan_ring_stream(b, lo, hi):
    """Biggest sentinel-framed triangle stream in [lo, hi) — the navmesh ring."""
    best = (0, 0, 0)
    o = lo
    while True:
        o = b.find(b'\xfe\xff', o, hi)
        if o < 0 or o + 2 > len(b):
            break
        if o % 2 == 0:
            prev = o - 2
            if prev >= lo and (u16(b, prev) & 0x7FFF) in SENT_FLAGS:
                seg = b[o:o + 0x100]
                if seg.count(b'\xfe\xff') >= 4:
                    start = o
                    tris = decode_s16_stream(
                        b, start - 0x200 if start - 0x200 >= lo else lo,
                        min(start + 0x400, hi))
                    if tris and len(tris) > best[0]:
                        best = (len(tris), tris[0]['off'], tris[-1]['off'] + 16)
                    o += 0x400
                    continue
        o += 2
    return best


# ── family 2: 0x32 transform records (placement xforms) ─────────────────────

def decode_f2_records(b, zrows):
    recs = []
    for r in zrows:
        blob = r['blob']
        if blob + 0x32 > len(b):
            continue
        words = [u32(b, blob + 4 * i) for i in range(12)]
        recs.append({'key': r['key'], 'tag': r['tag'], 'blob': blob,
                     'u32': ['%08X' % w for w in words],
                     'floats_1p0': sum(1 for w in words if w == 0x3F800000),
                     'float_neg4': any(w == 0xC0800000 for w in words),
                     'tail_u16': u16(b, blob + 0x30)})
    return recs


# ── family 3: count-prefixed 32B soup records ───────────────────────────────

def decode_f3(b, blob):
    if blob + 4 > len(b):
        return None
    count = u16(b, blob)
    pad = u16(b, blob + 2)
    recs = []
    o = blob + 4
    while o + 32 <= len(b) and len(recs) < MAX_F3_RECORDS:
        cols = (u32(b, o + 0x0C), u32(b, o + 0x10), u32(b, o + 0x14))
        if not all(REL_LO <= r < REL_HI for r in cols):
            break
        recs.append({'off': o,
                     'verts': [(i16(b, o), i16(b, o + 2)),
                               (i16(b, o + 4), i16(b, o + 6)),
                               (i16(b, o + 8), i16(b, o + 10))],
                     'idx': [u16(b, o + 0x18 + 2 * k) for k in range(4)]})
        o += 32
    return {'count_field': count, 'pad_field': pad, 'records': recs,
            'run_len': len(recs), 'extent_end': o}


# ── family 4: YNDT/YNGM guide (walkmesh tris + vertex pool) ─────────────────

def decode_yndt(b):
    y = u32(b, 0x3C) if len(b) >= 0x40 else 0
    if not (0 < y and y + 0x58 < len(b)) or b[y:y + 4] != b'YNDT':
        return None
    out = {'section': y, 'yngm': fourcc(b, y + 0x10)}
    if b[y + 0x10:y + 0x14] != b'YNGM':
        out['error'] = 'no YNGM at +0x10'
        return out
    tc, vc = u16(b, y + 0x38), u16(b, y + 0x3A)
    out.update({'triCount': tc, 'vertCount': vc,
                'const68': u16(b, y + 0x3E) == 68,
                'hdr44': u16(b, y + 0x44)})
    if tc == 0 or vc == 0 or tc > 4096 or vc > 4096:
        out['error'] = 'counts out of range'
        return out
    tris_end = y + 0x58 + tc * 20
    if tris_end + vc * 6 > len(b):
        out['error'] = 'tri/pool OOB'
        return out
    tris, bad_idx, bad_sent = [], 0, 0
    for k in range(tc):
        o = y + 0x58 + k * 20
        sent = [u32(b, o), u32(b, o + 4), u32(b, o + 8)]
        if not all(REL_LO <= s < REL_HI for s in sent):
            bad_sent += 1
        ia, ib, ic = u16(b, o + 12), u16(b, o + 14), u16(b, o + 16)
        if ia >= vc or ib >= vc or ic >= vc:
            bad_idx += 1
        tris.append({'i': (ia, ib, ic)})
    pool = tris_end
    verts, nonzero_y = [], 0
    for k in range(vc):
        o = pool + k * 6
        if i16(b, o + 2) != 0:
            nonzero_y += 1
        verts.append((i16(b, o), i16(b, o + 2), i16(b, o + 4)))
    yned = b.find(b'YNED', pool + vc * 6)
    out.update({'tris': tris, 'bad_sentinel': bad_sent, 'bad_index': bad_idx,
                'pool': pool, 'verts': verts, 'nonzero_y_verts': nonzero_y,
                'yned_at': yned if yned >= 0 else None,
                'yned_gap': (yned - (pool + vc * 6)) if yned >= 0 else None})
    return out


# ── record stream: walk + content shapes ────────────────────────────────────

def rgba_str(v):
    return '%02X%02X%02X%02X' % (v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF, v >> 24)


def is_color(v):
    a = v >> 24
    if a in (0x80, 0x5B, 0xC8, 0x40, 0x20, 0x1C):
        return True
    if a == 0x00:
        r, g, bl = v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF
        return r == g == bl
    return False


def packed_vert_ok(v):
    x, z = v & 0xFFFF, v >> 16
    return 0 <= x < 0x8000 and 0 <= z < 0x8000 and (x | z) != 0


def soup32_ok(b, o):
    if o + 32 > len(b) or u16(b, o + 2) != 0:
        return False
    if not all(packed_vert_ok(u32(b, o + 4 * k)) for k in (1, 2, 3)):
        return False
    return all(is_color(u32(b, o + 0x10 + 4 * k)) for k in range(3))


def paint20_ok(b, o):
    if o + 20 > len(b):
        return False
    if u32(b, o) == 0x40:
        return all(is_color(u32(b, o + 4 + 4 * k)) for k in range(3))
    if u32(b, o + 0x10) == 0x84:
        return all(is_color(u32(b, o + 4 * k)) for k in range(3))
    return False


def quad40_ok(b, o):
    if o + 40 > len(b):
        return False
    if not all(packed_vert_ok(u32(b, o + 4 * k)) for k in range(4)):
        return False
    if not all(is_color(u32(b, o + 0x10 + 4 * k)) for k in range(4)):
        return False
    return all(u16(b, o + 0x20 + 2 * k) < 0x4000 for k in range(4))


def edge24_ok(b, o):
    if o + 24 > len(b):
        return False
    cols = [u32(b, o + 4 * k) for k in range(4)]
    if not all(is_color(c) for c in cols):
        return False
    if not any((c >> 24) == 0x80 for c in cols):
        return False
    idx = [u16(b, o + 0x10 + 2 * k) for k in range(4)]
    return all(i < 0x4000 for i in idx) and len(set(idx)) >= 2


def range16_ok(b, o):
    if o + 16 > len(b):
        return False
    a, c, d = u32(b, o), u32(b, o + 4), u32(b, o + 8)
    return 0 < a < c < 0x200000 and d < 0x10000 and (c - a) < 0x10000


SHAPES = (
    ('paint20', 20, paint20_ok),
    ('range16', 16, range16_ok),
    ('quad40', 40, quad40_ok),
    ('soup32', 32, soup32_ok),
    ('edge24', 24, edge24_ok),
)


def walk_dispatch(b, rows, geom):
    srows = sorted(rows, key=lambda r: r['off'])
    walk = [{'row': r, 'start': geom + r['off'], 'end': geom + r['off'] + 2 * r['tag']}
            for r in srows]
    n_pairs, n_contig, gaps = 0, 0, []
    for a, c in zip(walk, walk[1:]):
        n_pairs += 1
        if c['start'] == a['end']:
            n_contig += 1
        else:
            gaps.append((a['end'], c['start']))
    return walk, n_contig, n_pairs, gaps


def classify_run(b, start, end):
    best = (None, 0, 0, 0, 0.0)
    n = end - start
    for name, size, ok in SHAPES:
        if n < size:
            continue
        for anchor in range(0, size):
            slots = hits = 0
            o = start + anchor
            while o + size <= end and slots < CLASSIFY_SLOTS:
                slots += 1
                if ok(b, o):
                    hits += 1
                o += size
            rate = (hits / slots) if slots else 0.0
            if slots >= 2 and hits * 2 > slots and rate > best[4]:
                best = (name, anchor, hits, slots, rate)
    return best[:4]


def analyze_stream(b, rows, geom):
    walk, n_contig, n_pairs, gaps = walk_dispatch(b, rows, geom)
    runs = []
    for w in walk:
        if runs and runs[-1][1] == w['start']:
            runs[-1][1] = w['end']
            runs[-1][2].append(w['row'])
        else:
            runs.append([w['start'], w['end'], [w['row']]])
    out_runs = []
    for s, e, rrows in runs:
        shape, anchor, hits, slots = classify_run(b, s, e)
        entry = {'start': s, 'end': e, 'size': e - s, 'rows': len(rrows),
                 'keys': sorted({r['key'] for r in rrows}),
                 'tags': sorted({r['tag'] for r in rrows})}
        if shape:
            entry.update({'shape': shape, 'anchor': anchor,
                          'records': hits, 'slots': slots})
        else:
            row_shapes = []
            for w in walk:
                if not (s <= w['start'] < e) or w['end'] - w['start'] < 20:
                    continue
                rshape, ranchor, rhits, rslots = classify_run(b, w['start'], w['end'])
                if rshape:
                    row_shapes.append({'start': w['start'], 'end': w['end'],
                                       'key': w['row']['key'], 'tag': w['row']['tag'],
                                       'shape': rshape, 'anchor': ranchor,
                                       'records': rhits, 'slots': rslots})
            if row_shapes:
                entry['row_shapes'] = row_shapes
        out_runs.append(entry)
    return {'walk_pairs': n_pairs, 'walk_contig': n_contig,
            'walk_gaps': len(gaps),
            'first_blob': walk[0]['start'] if walk else None,
            'walk_end': walk[-1]['end'] if walk else None,
            'runs': out_runs}


# ── blob family classification (zone shapes) ────────────────────────────────

def blob_shape(b, blob):
    if blob + 0x32 > len(b):
        return 'oob'
    for o in range(blob, blob + 0x32, 2):
        if u32(b, o) in (0x3F800000, 0xC0800000):
            return 'float32'
    sents = sum(1 for o in range(blob, blob + 0x40, 2) if is_sent_pair(b, o))
    if sents >= 2:
        return 's16framed'
    first = blob + ((4 - blob % 4) % 4)
    has_band = False
    for o in range(first, blob + 0x20, 4):
        if all(REL_LO <= u32(b, o + 4 * k) < REL_HI for k in range(3)):
            has_band = True
            break
    if has_band:
        d = decode_f3(b, blob)
        if (d and d['pad_field'] == 0 and 1 <= d['count_field'] <= 4096
                and d['run_len'] >= d['count_field']):
            return 'reloc32'
        return 'reloc_frag'
    if all(c == 0 for c in b[blob:blob + 0x20]):
        return 'zeros'
    return 'unframed'


# ── per-file pipeline ───────────────────────────────────────────────────────

def analyze_file(path, root=None):
    with open(path, 'rb') as f:
        b = f.read()
    rel = os.path.relpath(path, root) if root else path
    res = {'file': rel, 'size': len(b), 'status': 'ok'}
    if len(b) < 4 or b[:4] != b'MAP1':
        res['status'] = 'bad-magic'
        return res
    if len(b) <= HSZ + 4:
        res['status'] = 'stub'
        return res
    res['slots'] = parse_header(b)
    geom, meta, dabs, rows = read_dispatch(b)
    res['geom'], res['meta'], res['dispatch'] = geom, meta, dabs
    if meta == 0 or dabs is None:
        res['status'] = 'nometa' if (meta == 0 or not (0 < meta < len(b))) else 'nodispatch'
        res['yndt'] = decode_yndt(b)
        return res
    res['rows'] = len(rows)
    res['stream'] = analyze_stream(b, rows, geom)
    zr = zone_rows(rows, b)
    res['zone_rows'] = len(zr)
    shapes = [blob_shape(b, r['blob']) for r in zr]
    res['zone_shapes'] = dict(Counter(shapes))
    fam_shape = 'none'
    for want in ('reloc32', 'float32', 's16framed'):
        if want in shapes:
            fam_shape = want
            break
    if fam_shape == 'none' and zr:
        fam_shape = next((s for s in shapes if s != 'zeros'), 'zeros')
    res['blob_family'] = fam_shape
    if fam_shape == 's16framed':
        lo = min(r['blob'] for r in zr)
        hi = max(r['blob'] for r in zr) + 0x400
        tris = decode_s16_stream(b, lo, min(hi, len(b)))
        res['f1'] = {'triangles': len(tris),
                     'first_off': tris[0]['off'] if tris else None,
                     'last_off': (tris[-1]['off'] + 16) if tris else None,
                     'flags_seen': sorted({t['flags'] for t in tris})}
    elif fam_shape == 'float32':
        res['f2'] = {'records': decode_f2_records(b, zr)}
        cnt, s, e = scan_ring_stream(b, geom, meta if meta > geom else len(b))
        res['f2']['ring_stream'] = {'triangles': cnt, 'start': s, 'end': e}
    elif fam_shape == 'reloc32':
        anchor = next(r['blob'] for r, s in zip(zr, shapes) if s == 'reloc32')
        res['f3'] = decode_f3(b, anchor)
        res['f3']['records_sample'] = res['f3']['records'][:6]
        del res['f3']['records']
    res['yndt'] = decode_yndt(b)
    if res['yndt'] and 'tris' in res['yndt']:
        res['yndt']['verts'] = res['yndt']['verts'][:4]
        res['yndt']['tris'] = res['yndt']['tris'][:4]
    return res


def iter_corpus(root):
    hits = []
    for dirpath, _dirs, files in os.walk(root):
        for fn in files:
            if fn.lower() == 'mapout.vpa':
                hits.append(os.path.join(dirpath, fn))
    return sorted(hits)


def cmd_check(path):
    try:
        r = analyze_file(path)
    except (struct.error, IndexError) as e:
        print('%s: status=parse-error (%s)' % (os.path.basename(path), e))
        return 1
    st = r['status']
    # stub/nometa/nodispatch are legitimate container states the runtime
    # accepts (header-only maps, meta-less files); only magic mismatch fails.
    ok = st != 'bad-magic'
    print('%s: status=%s size=%d rows=%s zones=%s fam=%s' %
          (os.path.basename(path), st, r['size'],
           r.get('rows', '-'), r.get('zone_rows', '-'),
           r.get('blob_family', '-')))
    if 'yndt' in r and r['yndt'] and 'triCount' in r['yndt']:
        y = r['yndt']
        print('  yndt: tris=%d verts=%d bad_idx=%d nonzero_y=%d' %
              (y['triCount'], y['vertCount'], y['bad_index'], y['nonzero_y_verts']))
    return 0 if ok else 1


def cmd_info(path):
    r = analyze_file(path)
    print(json.dumps(r, indent=1, default=str))
    return 0


def cmd_census(root):
    paths = iter_corpus(root)
    results = [analyze_file(p, root) for p in paths]
    agg = Counter(r['status'] for r in results)
    fam = Counter(r.get('blob_family', '-') for r in results)
    yndt_ok = sum(1 for r in results
                  if r.get('yndt') and 'tris' in r.get('yndt', {}))
    with_stream = [r for r in results if 'stream' in r]
    pairs = sum(r['stream']['walk_pairs'] for r in with_stream)
    contig = sum(r['stream']['walk_contig'] for r in with_stream)
    ngaps = sum(r['stream']['walk_gaps'] for r in with_stream)
    shape_files = Counter()
    shape_records = Counter()
    for r in with_stream:
        shapes = {run.get('shape') for run in r['stream']['runs'] if run.get('shape')}
        shapes |= {rs['shape'] for run in r['stream']['runs']
                   for rs in run.get('row_shapes', [])}
        for s in shapes:
            shape_files[s] += 1
        for run in r['stream']['runs']:
            if run.get('shape'):
                shape_records[run['shape']] += run['records']
            for rs in run.get('row_shapes', []):
                shape_records[rs['shape']] += rs['records']
    f1_tris = sum(r['f1']['triangles'] for r in results if 'f1' in r)
    rings = sum(r['f2']['ring_stream']['triangles']
                for r in results if 'f2' in r)
    summary = {'files': len(results), 'status_counts': dict(agg),
               'blob_family_counts': dict(fam),
               'yndt_decoded_ok': yndt_ok,
               'stream_walk_pairs': pairs, 'stream_walk_contig': contig,
               'stream_walk_gaps': ngaps,
               'stream_shape_files': dict(shape_files),
               'stream_shape_records': dict(shape_records),
               'f1_zone_triangles_total': f1_tris,
               'f2_ring_navmesh_triangles_total': rings}
    print(json.dumps(summary, indent=1))
    return 0


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    cmd, arg = sys.argv[1], sys.argv[2]
    if cmd == 'check':
        return cmd_check(arg)
    if cmd == 'info':
        return cmd_info(arg)
    if cmd == 'census':
        if not os.path.isdir(arg):
            print('missing corpus root: %s' % arg, file=sys.stderr)
            return 2
        return cmd_census(arg)
    print('unknown command %s' % cmd, file=sys.stderr)
    return 2


if __name__ == '__main__':
    sys.exit(main())
