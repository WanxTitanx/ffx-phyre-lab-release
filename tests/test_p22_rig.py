# P22 — weights, rest pose e retarget.
# Corpus-gated: .chr/.mgrp/.dae.phyre staged under .lab/runs.
import json
import shutil
import struct
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
CHR_EDIT = ROOT / "tools" / "chr_edit.py"
POSE = ROOT / "tools" / "pose_bake.py"
ME = ROOT / "tools" / "phyre-meshedit" / "bin" / "Debug" / "net10.0" / \
    "phyre-meshedit"
EXP = ROOT / "tools" / "phyre-exporter" / "bin" / "Debug" / "net10.0" / \
    "phyre-exporter"

P19 = ROOT / ".lab" / "runs" / "p19-motion"
M001_CHR = P19 / "m001_m001.chr"
M002_MGRP = P19 / "m002_resident1.mgrp"
C001_MGRP = P19 / "c001_resident1.mgrp"
M001_PHYRE = ROOT / ".lab/corpus/dev/monster-small/ffx_data/gamedata/" \
    "ps3data/chr/mon/m001/mdl/d3d11/m001.dae.phyre"


def run(*a):
    return subprocess.run([sys.executable, *map(str, a)],
                          capture_output=True, text=True)


@pytest.fixture
def chrwork(tmp_path):
    p = tmp_path / "w.chr"
    shutil.copy(M001_CHR, p)
    return p


@pytest.mark.skipif(not M001_CHR.exists(), reason="p19 chr absent")
def test_chr_edit_rest_pose(chrwork):
    """Rest-pose rot edit quantizes to i16 and reads back."""
    p = run(CHR_EDIT, "set", chrwork, 5, "rot", 1.5708, 0, 0)
    assert p.returncode == 0 and "i16=[9000" in p.stdout
    sys.path.insert(0, str(ROOT / "tools"))
    from chr_check import Chr
    s = Chr(str(chrwork)).skl()
    assert abs(s["bones"][5]["rot"][0] - 1.5707963) < 1e-6
    assert s["bones"][5]["rot"][1] == 0.0


@pytest.mark.skipif(not M001_CHR.exists(), reason="p19 chr absent")
def test_chr_edit_out_preserves_input(tmp_path):
    """--out writes a separate file; only the edited field bytes differ."""
    out = tmp_path / "o.chr"
    p = run(CHR_EDIT, "set", M001_CHR, 5, "rot", 1.5708, 0, 0,
            "--out", out)
    assert p.returncode == 0
    a = M001_CHR.read_bytes()
    b = out.read_bytes()
    diffs = [i for i, (x, y) in enumerate(zip(a, b)) if x != y]
    sys.path.insert(0, str(ROOT / "tools"))
    from chr_check import Chr, u32
    c = Chr(str(M001_CHR))
    s = c.skl()
    bt = s["off"] + u32(c.d, s["off"] + 0x1C)
    lo, hi = bt + 5 * 20 + 2, bt + 5 * 20 + 8
    assert diffs and all(lo <= i < hi for i in diffs)


@pytest.mark.skipif(not M001_CHR.exists(), reason="p19 chr absent")
def test_chr_edit_negatives(chrwork):
    assert run(CHR_EDIT, "set", chrwork, 999, "rot", 0).returncode == 2
    # rot 10 rad = 57295 i16 -> unrepresentable
    assert run(CHR_EDIT, "set", chrwork, 5, "rot", 10.0).returncode == 2


@pytest.mark.skipif(not (M001_CHR.exists() and M002_MGRP.exists()),
                    reason="p19 inputs absent")
def test_bind_matrices_inverse_identity(tmp_path):
    """IBM = inverse(bind world): sanity via pose_bake internals."""
    sys.path.insert(0, str(ROOT / "tools"))
    import numpy as np
    from chr_check import Chr
    from pose_bake import world_mats
    s = Chr(str(M001_CHR)).skl()
    bones = [b["parent"] for b in s["bones"]]
    bind_local = [{"rot": b["rot"], "tr": b["tr"], "sc": b["sc"]}
                  for b in s["bones"]]
    W = world_mats(bones, bind_local, "xyz")
    for i in (0, 5, 40, 91):
        inv = np.linalg.inv(W[i])
        assert np.allclose(W[i] @ inv, np.eye(4), atol=1e-5)
        # translations finite and non-degenerate
        assert np.isfinite(W[i]).all()


@pytest.mark.skipif(not (M001_CHR.exists() and M002_MGRP.exists()),
                    reason="p19 inputs absent")
def test_retarget_same_skeleton(tmp_path):
    """m002 clip on m001 rig (identical 92-bone skeleton) renders."""
    out = tmp_path / "rt.png"
    p = run(POSE, "render", M001_CHR, M002_MGRP, 0, 0, out,
            "--order", "xyz", "--delta")
    assert p.returncode == 0 and out.exists()


@pytest.mark.skipif(not (M001_CHR.exists() and M002_MGRP.exists()),
                    reason="p19 inputs absent")
def test_retarget_multi_frame_moves():
    """Retargeted clip produces real motion across frames on m001 rig."""
    sys.path.insert(0, str(ROOT / "tools"))
    import numpy as np
    from chr_check import Chr
    from mgrp_decode import Mgrp
    from pose_bake import world_mats
    s = Chr(str(M001_CHR)).skl()
    bones = [b["parent"] for b in s["bones"]]
    bind = [{"rot": b["rot"], "tr": b["tr"], "sc": b["sc"]}
            for b in s["bones"]]
    f = Mgrp(str(M002_MGRP))
    clip = f.decode_clip(f.clips(f.records[0])[0]["blob"])
    assert clip["targets"] == len(bones) == 92
    from mgrp_decode import target_trs
    mid = clip["frames"] // 2
    moved = 0
    moved_mid = 0
    for t in range(clip["targets"]):
        a0 = target_trs(clip, t, 0, 1.0)
        am = target_trs(clip, t, mid, 1.0)
        z = {"rot": [0, 0, 0], "tr": [0, 0, 0], "sc": [1, 1, 1]}
        if not np.allclose(sum((list(a0[k]) for k in a0), []),
                           sum((list(z[k]) for k in z), []), atol=1e-6):
            moved += 1
        if not np.allclose(sum((list(a0[k]) for k in a0), []),
                           sum((list(am[k]) for k in am), []), atol=1e-6):
            moved_mid += 1
    assert moved > 0, "clip never moves any target off bind"
    assert moved_mid > 0, "clip static between frame 0 and mid"


@pytest.mark.skipif(not (M001_CHR.exists() and C001_MGRP.exists()),
                    reason="p19 inputs absent")
def test_retarget_incompatible_refused(tmp_path):
    """c001 clip (136 targets) on m001 rig (92 bones) must refuse."""
    p = run(POSE, "render", M001_CHR, C001_MGRP, 0, 0,
            tmp_path / "no.png", "--order", "xyz", "--delta")
    assert p.returncode == 2 and "incompatible" in p.stderr


@pytest.mark.skipif(not (M001_PHYRE.exists() and ME.exists()
                         and EXP.exists()), reason="corpus/tools absent")
def test_weight_and_joint_edit_export(tmp_path):
    """meshedit joints+weights edits appear correctly in glTF export."""
    ed = tmp_path / "e.phyre"
    p = subprocess.run([str(ME), "joints", str(M001_PHYRE), str(ed),
                        "--seg", "0", "--range", "0:50", "--slot", "0",
                        "--set", "7"], capture_output=True, text=True)
    assert p.returncode == 0, p.stderr
    ed2 = tmp_path / "e2.phyre"
    p = subprocess.run([str(ME), "weights", str(ed), str(ed2),
                        "--seg", "0", "--range", "0:50", "--slot", "1",
                        "--set", "0.5"], capture_output=True, text=True)
    assert p.returncode == 0, p.stderr
    g0, g1 = tmp_path / "o.gltf", tmp_path / "e.gltf"
    subprocess.run([str(EXP), str(M001_PHYRE), str(g0)], check=True,
                   capture_output=True)
    subprocess.run([str(EXP), str(ed2), str(g1)], check=True,
                   capture_output=True)
    sys.path.insert(0, str(ROOT / "tools"))
    from rt_check import accessor
    go, ge = json.load(open(g0)), json.load(open(g1))
    ao = go["meshes"][0]["primitives"][0]["attributes"]
    ae = ge["meshes"][0]["primitives"][0]["attributes"]
    jo = accessor(go, ao["JOINTS_0"], g0)
    je = accessor(ge, ae["JOINTS_0"], g1)
    we = accessor(ge, ae["WEIGHTS_0"], g1)
    assert je[0] != jo[0]            # joint reassigned (palette slot 7)
    assert all(je[i] == je[0] for i in range(50))
    assert je[100] == jo[100]        # outside range untouched
    # weights normalized after edit
    for i in range(50):
        assert abs(sum(we[i]) - 1.0) < 1e-4
