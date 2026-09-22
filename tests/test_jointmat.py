# P23 — jointmat: PMatrix4 local-bind / absolute-element patcher.
# Corpus-gated: needs c001.dae.phyre (extracted FFX data).
import os
import struct
import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
ME = ROOT / "tools" / "phyre-meshedit" / "bin" / "Debug" / "net10.0" / \
    "phyre-meshedit"
C001 = Path(os.environ.get("FFX_ASSETS", "")) / \
    "ffx_data/gamedata/ps3data/chr/pc/c001/mdl/d3d11/c001.dae.phyre"

# c001.dae.phyre block layout (meshedit `blocks`):
#   PMatrix4 off=36660 elem=64 count=211; skeleton link localmatBase=53, 79 joints
PMAT_OFF, PMAT_ELEM, PMAT_CNT, LBASE, NJ = 36660, 64, 211, 53, 79


def run(*a):
    return subprocess.run([str(ME), *map(str, a)],
                          capture_output=True, text=True)


@pytest.mark.skipif(not (C001.exists() and ME.exists()),
                    reason="corpus/tool absent")
def test_jointmat_scales_3x3_only(tmp_path):
    out = tmp_path / "o.phyre"
    p = run("jointmat", C001, out, "--joint", "40", "--scale", "3")
    assert p.returncode == 0, p.stderr
    a, b = C001.read_bytes(), out.read_bytes()
    assert len(a) == len(b)
    diffs = [i for i, (x, y) in enumerate(zip(a, b)) if x != y]
    mo = PMAT_OFF + PMAT_ELEM * (LBASE + 40)
    # only the 11 floats of the 3x3 part (m[0..10]) may differ — not the
    # translation column m[12..14] nor row m[15]
    lo, hi = mo, mo + 44
    assert diffs and all(lo <= i < hi for i in diffs)
    for i in range(11):
        va = struct.unpack_from("<f", a, mo + i * 4)[0]
        vb = struct.unpack_from("<f", b, mo + i * 4)[0]
        assert vb == pytest.approx(va * 3, rel=1e-6)
    for i in (12, 13, 14, 15):
        assert a[mo + i * 4:mo + i * 4 + 4] == b[mo + i * 4:mo + i * 4 + 4]


@pytest.mark.skipif(not (C001.exists() and ME.exists()),
                    reason="corpus/tool absent")
def test_jointmat_translate(tmp_path):
    out = tmp_path / "o.phyre"
    p = run("jointmat", C001, out, "--joint", "5",
            "--translate", "0.5,-1,2")
    assert p.returncode == 0, p.stderr
    a, b = C001.read_bytes(), out.read_bytes()
    mo = PMAT_OFF + PMAT_ELEM * (LBASE + 5)
    for k, dv in enumerate((0.5, -1.0, 2.0)):
        va = struct.unpack_from("<f", a, mo + (12 + k) * 4)[0]
        vb = struct.unpack_from("<f", b, mo + (12 + k) * 4)[0]
        assert vb == pytest.approx(va + dv, abs=1e-6)


@pytest.mark.skipif(not (C001.exists() and ME.exists()),
                    reason="corpus/tool absent")
def test_jointmat_abs_element(tmp_path):
    out = tmp_path / "o.phyre"
    p = run("jointmat", C001, out, "--abs", "172", "--scale", "2")
    assert p.returncode == 0, p.stderr
    a, b = C001.read_bytes(), out.read_bytes()
    mo = PMAT_OFF + PMAT_ELEM * 172
    diffs = [i for i, (x, y) in enumerate(zip(a, b)) if x != y]
    assert all(mo <= i < mo + 44 for i in diffs)


@pytest.mark.skipif(not (C001.exists() and ME.exists()),
                    reason="corpus/tool absent")
def test_jointmat_negatives(tmp_path):
    # joint index beyond skeleton link range
    assert run("jointmat", C001, tmp_path / "a", "--joint", "999",
               "--scale", "2").returncode == 1
    # absolute element beyond PMatrix4
    assert run("jointmat", C001, tmp_path / "b", "--abs", "999",
               "--scale", "2").returncode == 1
