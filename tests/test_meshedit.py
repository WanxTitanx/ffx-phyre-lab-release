"""P16: phyre-meshedit — semantic vertex edits, layout preservation, negatives.

Tests run against corpus files (local-only, .lab/). Skipped when absent.
Every edit must keep file size identical and restrict byte diffs to the
targeted semantic stream (+ PMeshInstanceBounds record for position edits).
"""
import struct
import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
PROJ = ROOT / "tools" / "phyre-meshedit"
MAP = ROOT / ".lab" / "runs" / "p16-meshedit" / "znkd06_orig.dae.phyre"
CHR = ROOT / ".lab" / "runs" / "p16-meshedit" / "c001_orig.dae.phyre"

pytestmark = pytest.mark.skipif(
    not (MAP.exists() and CHR.exists()),
    reason="requires local p16 fixtures (.lab, not committed)")


def run(*args):
    return subprocess.run(
        ["dotnet", "run", "--project", str(PROJ), "--", *args],
        capture_output=True, text=True, cwd=ROOT, timeout=300)


def diff_ranges(a: Path, b: Path):
    A, B = a.read_bytes(), b.read_bytes()
    assert len(A) == len(B), "output size must equal input (layout preserved)"
    xs = [i for i, (x, y) in enumerate(zip(A, B)) if x != y]
    rngs = []
    for i in xs:
        if rngs and i - rngs[-1][1] <= 4:
            rngs[-1][1] = i
        else:
            rngs.append([i, i])
    return xs, rngs


def test_info_reports_streams():
    r = run("info", str(MAP))
    assert r.returncode == 0
    assert "seg 3: verts=20327" in r.stdout
    assert "Vertex" in r.stdout and "Normal" in r.stdout and "Tangent" in r.stdout


def test_position_edit_and_bounds_recompute(tmp_path):
    out = tmp_path / "e.phyre"
    r = run("edit", str(MAP), str(out), "--seg", "3", "--range", "0:100",
            "--translate", "0,50,0")
    assert r.returncode == 0, r.stderr
    assert "bounds recomputed" in r.stdout
    xs, rngs = diff_ranges(MAP, out)
    # 100 verts x 4B (Y float) + bounds record write
    assert len(xs) == 404
    # bounds record lives in PMeshInstanceBounds region (before vertexBase)
    assert rngs[0][0] < 200000 and rngs[0][1] < 200000
    # all vertex diffs inside seg3 position stream [768227, 768227+20327*12)
    assert all(768227 <= a < 768227 + 20327 * 12 for a, _ in rngs[1:])


def test_uv_offset(tmp_path):
    out = tmp_path / "u.phyre"
    r = run("uv", str(MAP), str(out), "--seg", "3", "--range", "0:50",
            "--offset", "0.25,0")
    assert r.returncode == 0, r.stderr
    xs, rngs = diff_ranges(MAP, out)
    assert 0 < len(xs) <= 200  # <= 50 verts x 4B (unchanged mantissa bytes don't diff)
    assert all(1256075 <= a < 1256075 + 20327 * 8 for a, _ in rngs)
    orig = struct.unpack_from("<2f", MAP.read_bytes(), 1256075)
    edit = struct.unpack_from("<2f", out.read_bytes(), 1256075)
    assert abs(edit[0] - orig[0] - 0.25) < 1e-6 and edit[1] == orig[1]


def test_normals_and_tangents(tmp_path):
    for cmd, off in (("normals", 1012151), ("tangents", 1418691)):
        out = tmp_path / f"{cmd}.phyre"
        r = run(cmd, str(MAP), str(out), "--seg", "3", "--range", "0:10",
                "--set", "0,1,0")
        assert r.returncode == 0, r.stderr
        xs, rngs = diff_ranges(MAP, out)
        assert all(off <= a < off + 20327 * 12 for a, _ in rngs)
        # last written vert must equal the set vector
        assert struct.unpack_from("<3f", out.read_bytes(), off + 9 * 12) == (0.0, 1.0, 0.0)


def test_weights_renormalize(tmp_path):
    out = tmp_path / "w.phyre"
    # c001 seg4 vert0 = [0.667,0.184,0.149,0] mixed weights
    r = run("weights", str(CHR), str(out), "--seg", "4", "--range", "0:1",
            "--slot", "0", "--set", "0.9")
    assert r.returncode == 0, r.stderr
    w = struct.unpack_from("<4f", out.read_bytes(), 696344)
    assert abs(sum(w) - 1.0) < 1e-5
    assert w[0] > 0.7  # raised toward dominance, renormalized


def test_alpha_transparency(tmp_path):
    out = tmp_path / "a.phyre"
    r = run("alpha", str(CHR), str(out), "--seg", "0", "--range", "0:20",
            "--set", "0.2")
    assert r.returncode == 0, r.stderr
    c = struct.unpack_from("<4f", out.read_bytes(), 379736)
    assert abs(c[3] - 0.2) < 1e-6


@pytest.mark.parametrize("args,rc", [
    (("edit", "{MAP}", "o", "--seg", "999", "--translate", "0,1,0"), 1),
    (("edit", "{MAP}", "o", "--seg", "0", "--range", "50:10",
      "--translate", "0,1,0"), 1),
    (("weights", "{MAP}", "o", "--seg", "0", "--slot", "0", "--set", "1"), 1),
    (("weights", "{CHR}", "o", "--seg", "0", "--slot", "9", "--set", "1"), 2),
    (("alpha", "{MAP}", "o", "--seg", "0", "--set", "0.5"), 0),  # map has Color
    (("tangents", "{CHR}", "o", "--seg", "0", "--set", "1,0,0"), 0),
])
def test_cases(tmp_path, args, rc):
    a = [x.replace("{MAP}", str(MAP)).replace("{CHR}", str(CHR)) for x in args]
    a[2] = str(tmp_path / "o.phyre")
    r = run(*a)
    assert r.returncode == rc
    if rc != 0:
        assert r.stderr.strip(), "failures must print a diagnostic"


def test_missing_input():
    r = run("info", "/nonexistent/x.phyre")
    assert r.returncode != 0


# ---------------------------------------------------------------------------
# P17: topology growth (grow --tri/--clone)
# ---------------------------------------------------------------------------

SEG3_REC = 109969 + 3 * 108          # PMeshSegment[3] record (108B, object region)
IDX_BASE = 219439                    # ExternalIndexBase for znkd06_orig
SEG3_IDXOFF, SEG3_IDXSIZE = 4320, 42864
SEG3_POS = 768227                    # seg3 Vertex stream start
SEG3_STRIDE_SUM = 104                # 12+12+8+12+12+8+12+12+16 across 9 streams


def grow_file(tmp_path, *gargs, src=MAP):
    out = tmp_path / "g.phyre"
    r = run("grow", str(src), str(out), *gargs)
    assert r.returncode == 0, r.stderr
    return out


def test_grow_clone_counts_and_offsets(tmp_path):
    addV = addI = 30  # --clone 0,17,10 -> 10 tris -> 30 verts + 30 indices
    out = grow_file(tmp_path, "--seg", "3", "--clone", "0,17,10", "--dxyz", "0,45,0")
    d, o = out.read_bytes(), MAP.read_bytes()
    assert len(d) == len(o) + addI * 2 + addV * SEG3_STRIDE_SUM
    # PMeshSegment[3]: maxIndex(+52), indexCount(+56), indexSize(+100)
    f13, f14, f25 = struct.unpack_from("<3I", d, SEG3_REC + 52)[0], \
        struct.unpack_from("<I", d, SEG3_REC + 56)[0], \
        struct.unpack_from("<I", d, SEG3_REC + 100)[0]
    assert (f13, f14, f25) == (20326 + addV, 21432 + addI, SEG3_IDXSIZE + addI * 2)
    # next segment's index offset shifted by exactly the inserted index bytes
    assert struct.unpack_from("<I", d, 109969 + 4 * 108 + 92)[0] == 47184 + addI * 2
    # header sizes
    assert struct.unpack_from("<I", d, 72)[0] == 423220 + addI * 2
    assert struct.unpack_from("<I", d, 76)[0] == 12451840 + addV * SEG3_STRIDE_SUM


def test_grow_clone_data_and_locality(tmp_path):
    out = grow_file(tmp_path, "--seg", "3", "--clone", "0,17,3", "--dxyz", "0,45,0")
    d, o = out.read_bytes(), MAP.read_bytes()
    posNew = SEG3_POS + 3 * 3 * 2  # index insert shifts whole vertex region
    for j in range(3):             # cloned tris 0,17,34
        tri = struct.unpack_from("<3H", o, IDX_BASE + SEG3_IDXOFF + j * 17 * 6)
        for e, src in enumerate(tri):
            srcPos = struct.unpack_from("<3f", o, SEG3_POS + src * 12)
            k = j * 3 + e
            got = struct.unpack_from("<3f", d, posNew + (20327 + k) * 12)
            assert got == pytest.approx(
                (srcPos[0], srcPos[1] + 45.0, srcPos[2]), abs=1e-5)
            # appended index points at the new vertex (segment-local indexing)
            ni = struct.unpack_from("<H", d, IDX_BASE + SEG3_IDXOFF + SEG3_IDXSIZE + k * 2)[0]
            assert ni == 20327 + k
    # seg4 index data preserved at its shifted offset
    assert struct.unpack_from("<6H", d, IDX_BASE + 47184 + 18) == \
        struct.unpack_from("<6H", o, IDX_BASE + 47184)


def test_grow_skinned_clones_all_streams(tmp_path):
    # c001 seg5: 8 streams incl. SkinWeights(16B)@881316, SkinIndices(4B)@920212
    out = grow_file(tmp_path, "--seg", "5", "--clone", "0,1,3", "--dxyz", "0,8,0", src=CHR)
    d, o = out.read_bytes(), CHR.read_bytes()
    vtxShift = 3 * 3 * 2  # 9 new indices * 2B
    sw = 881316 + vtxShift + 9 * (12 + 12 + 8 + 12 + 12 + 16)  # inserts before SkinWeights
    si = 920212 + vtxShift + 9 * (12 + 12 + 8 + 12 + 12 + 16 + 16)
    # src verts = first 3 tris of seg5 (clone 0,1,3): idxbase for c001
    hdr = struct.unpack_from("<22I", o, 0)
    ib = (len(o) - hdr[19]) - hdr[18]
    seg5_idxoff = struct.unpack_from("<I", o, 51188 + 5 * 108 + 92)[0]
    srcs = struct.unpack_from("<9H", o, ib + seg5_idxoff)
    for k, src in enumerate(srcs):
        assert d[sw + (2431 + k) * 16: sw + (2432 + k) * 16] == \
            o[881316 + src * 16: 881316 + (src + 1) * 16]      # weights cloned
        assert d[si + (2431 + k) * 4: si + (2432 + k) * 4] == \
            o[920212 + src * 4: 920212 + (src + 1) * 4]        # joint indices cloned
    r = run("info", str(out))
    assert "seg 5: verts=2440" in r.stdout


def test_grow_tri_updates_bounds(tmp_path):
    out = grow_file(tmp_path, "--seg", "3",
                    "--tri", "0,400,0;200,400,0;0,400,200")
    d = out.read_bytes()
    ymax = max(struct.unpack_from("<3f", d, 104069 + i * 36 + 16)[1]
               + struct.unpack_from("<3f", d, 104069 + i * 36)[1]
               for i in range(44))
    assert ymax >= 399.9


@pytest.mark.parametrize("gargs,rc", [
    (("--seg", "999", "--clone", "0,1,1"), 1),
    (("--seg", "3", "--clone", "7100,1,100"), 1),  # 7100+99 >= tri count 7144
    (("--seg", "3"), 2),                            # no growth args
    (("--seg", "3", "--clone", "0,0,5"), 1),        # zero stride
])
def test_grow_negatives(tmp_path, gargs, rc):
    r = run("grow", str(MAP), str(tmp_path / "o.phyre"), *gargs)
    assert r.returncode == rc
    assert r.stderr.strip()
