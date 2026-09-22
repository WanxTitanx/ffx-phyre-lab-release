"""P26: map scene graph — PNode/PWorldMatrix reading, worldmat edits,
battle-vs-field structure, project persistence (phyre.ops.v1).

Tests run against the local znkd06 fixture (.lab/, not committed).
Every worldmat edit must keep file size identical and restrict byte
diffs to the targeted matrix record.
"""
import struct
import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
PROJ = ROOT / "tools" / "phyre-meshedit"
PROJX = ROOT / "tools" / "phyre-project"
MAP = ROOT / ".lab" / "runs" / "p26-map" / "znkd06_src.dae.phyre"

pytestmark = pytest.mark.skipif(
    not MAP.exists(), reason="requires local p26 fixture (.lab, not committed)")

# znkd06 layout facts (measured 2026-09-21)
WM_OFF, WM_ELEM = 184921, 48          # PWorldMatrix block
NODE_OFF, NODE_ELEM = 122389, 84      # PNode block


def run(*args, proj=PROJ):
    return subprocess.run(
        ["dotnet", "run", "--project", str(proj), "--", *args],
        capture_output=True, text=True, cwd=ROOT, timeout=300)


def test_scene_summary():
    p = run("scene", MAP)
    assert p.returncode == 0, p.stderr
    assert "nodes=64" in p.stdout and "worldMats=49" in p.stdout
    assert "instances=44" in p.stdout and "materials=59" in p.stdout
    assert "cameras=5" in p.stdout


def test_scene_instance_matrix_map():
    """Every mesh instance is used by exactly one world matrix (1:1)."""
    p = run("scene", MAP)
    assert p.returncode == 0
    lines = [l for l in p.stdout.splitlines() if l.startswith("wm[")]
    inst_users = [l for l in lines if "inst" in l]
    assert len(inst_users) == 44
    assert "camP0" in p.stdout and "camO0" in p.stdout  # cameras resolved


def test_scene_materials():
    p = run("scene", MAP)
    assert p.returncode == 0
    assert "importStrings=44" in p.stdout
    assert "PhyreDefaultLitShader" in p.stdout
    assert ".dds" in p.stdout
    # material->paramBuffer resolution: per-material block ids (13..71),
    # never the degenerate element index 0
    assert "paramBufs=[43]" in p.stdout
    assert "paramBufs=[0]" not in p.stdout


def test_worldmat_translate_locality(tmp_path):
    out = tmp_path / "m.phyre"
    p = run("worldmat", MAP, out, "--matrix", "1", "--translate", "0,300,0")
    assert p.returncode == 0, p.stderr
    a, b = MAP.read_bytes(), out.read_bytes()
    assert len(a) == len(b)
    diffs = [i for i, (x, y) in enumerate(zip(a, b)) if x != y]
    # exactly the ty float of wm[1]: off + 1*48 + f[9]*4
    lo, hi = WM_OFF + WM_ELEM + 36, WM_OFF + WM_ELEM + 40
    assert diffs == list(range(lo, hi))
    ty = struct.unpack_from("<f", b, lo)[0]
    assert abs(ty - 303.376) < 0.01


def test_worldmat_node_local(tmp_path):
    out = tmp_path / "n.phyre"
    p = run("worldmat", MAP, out, "--node", "1", "--translate", "0,300,0")
    assert p.returncode == 0, p.stderr
    a, b = MAP.read_bytes(), out.read_bytes()
    diffs = [i for i, (x, y) in enumerate(zip(a, b)) if x != y]
    # PNode[1] local matrix row3.y: off + 1*84 + 16 (matrix) + 52 (row3 col1)
    lo = NODE_OFF + NODE_ELEM + 16 + 52
    assert diffs == list(range(lo, lo + 4))


def test_worldmat_scale(tmp_path):
    out = tmp_path / "s.phyre"
    p = run("worldmat", MAP, out, "--matrix", "1", "--scale", "2.0")
    assert p.returncode == 0, p.stderr
    a, b = MAP.read_bytes(), out.read_bytes()
    diffs = [i for i, (x, y) in enumerate(zip(a, b)) if x != y]
    # 9 basis floats of wm[1] scaled: f0..f7 + f11 (f8..f10 = translation untouched)
    base = WM_OFF + WM_ELEM
    for fi in range(8):
        v = struct.unpack_from("<f", b, base + fi * 4)[0]
        o = struct.unpack_from("<f", a, base + fi * 4)[0]
        assert abs(v - o * 2.0) < 1e-4, fi
    for fi in (8, 9, 10):
        assert struct.unpack_from("<f", b, base + fi * 4)[0] == \
               struct.unpack_from("<f", a, base + fi * 4)[0]


def test_worldmat_negatives(tmp_path):
    out = tmp_path / "x.phyre"
    assert run("worldmat", MAP, out, "--matrix", "999").returncode == 1
    assert run("worldmat", MAP, out, "--node", "999").returncode == 1
    p = run("worldmat", MAP, out, "--matrix", "1", "--translate", "1,2")
    assert p.returncode == 2
    assert run("worldmat", MAP, out).returncode == 2  # nothing to patch


def test_project_persistence(tmp_path):
    """worldmat edit journaled as phyre.ops.v1 patch replays byte-identical."""
    edited = tmp_path / "edited.phyre"
    assert run("worldmat", MAP, edited, "--matrix", "1",
               "--translate", "0,300,0").returncode == 0
    proj = tmp_path / "proj"
    p = subprocess.run(["dotnet", "run", "--project", str(PROJX), "--",
                        "new", str(MAP), str(proj)],
                       capture_output=True, text=True, timeout=300)
    assert p.returncode == 0, p.stderr
    # diff -> patch ops
    a, b = MAP.read_bytes(), edited.read_bytes()
    xs = [i for i, (x, y) in enumerate(zip(a, b)) if x != y]
    runs = []
    for i in xs:
        if runs and i == runs[-1][1] + 1:
            runs[-1][1] = i
        else:
            runs.append([i, i])
    for lo, hi in runs:
        p = subprocess.run(["dotnet", "run", "--project", str(PROJX), "--",
                            "add-patch", str(proj), "src0", str(lo),
                            b[lo:hi + 1].hex()],
                           capture_output=True, text=True, timeout=300)
        assert p.returncode == 0, p.stderr
    out = tmp_path / "mat.phyre"
    p = subprocess.run(["dotnet", "run", "--project", str(PROJX), "--",
                        "materialize", str(proj), str(out)],
                       capture_output=True, text=True, timeout=300)
    assert p.returncode == 0, p.stderr
    assert out.read_bytes() == b
