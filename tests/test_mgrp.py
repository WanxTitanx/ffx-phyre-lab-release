"""P18 — .mgrp checker tests (corpus-gated; skips cleanly without .lab)."""
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
TOOL = ROOT / "tools" / "mgrp_check.py"
CORPUS = ROOT / ".lab" / "corpus"

REGMOT = CORPUS / "dev/mgrp-battle/ffx_ps2/ffx/master/jppc/battle/mot/regmot.mgrp"
NPC_DIR = CORPUS / "dev/mgrp-npc"
HOLDOUT = CORPUS / "holdout/mgrp-holdout"
NEG = CORPUS / "negatives" / "truncated.mgrp"


def run(*paths):
    return subprocess.run([sys.executable, str(TOOL), *map(str, paths)],
                          capture_output=True, text=True)


@pytest.mark.skipif(not REGMOT.exists(), reason="local .lab corpus absent")
def test_regmot_multi_record_bank():
    p = run(REGMOT)
    assert p.returncode == 0, p.stdout + p.stderr
    # battle bank: 8 records (groupKeys 1..8), 11 seqProgs, 11 clips
    assert "recs: 8" in p.stdout
    assert "progs: 11" in p.stdout
    assert "clips: 11" in p.stdout
    assert "streams: 1882" in p.stdout


@pytest.mark.skipif(not NPC_DIR.exists(), reason="local .lab corpus absent")
def test_npc_resident_stubs():
    p = run(NPC_DIR)
    assert p.returncode == 0, p.stdout + p.stderr
    assert "stub16" in p.stdout


@pytest.mark.skipif(not HOLDOUT.exists(), reason="local .lab corpus absent")
def test_holdout_npc_stub():
    p = run(HOLDOUT)
    assert p.returncode == 0, p.stdout + p.stderr


@pytest.mark.skipif(not NEG.exists(), reason="local .lab corpus absent")
def test_negative_rejected():
    p = run(NEG)
    assert p.returncode == 2
    assert "tiny" in p.stdout


def test_usage_error():
    p = run()
    assert p.returncode == 1


# ── P19: decode + pose composition ───────────────────────────────────────────
P19 = ROOT / ".lab" / "runs" / "p19-motion"
DEC = ROOT / "tools" / "mgrp_decode.py"
C001_CHR = P19 / "c001_c001.chr"
C001_MGRP = P19 / "c001_resident1.mgrp"
M002_CHR = P19 / "m002_m002.chr"
M002_MGRP = P19 / "m002_resident1.mgrp"


def run_dec(*args):
    return subprocess.run([sys.executable, str(DEC), *map(str, args)],
                          capture_output=True, text=True)


@pytest.mark.skipif(not C001_MGRP.exists(), reason="p19 lab inputs absent")
def test_decode_c001_bank():
    p = run_dec("verify", C001_MGRP)
    assert p.returncode == 0
    assert "clips=77" in p.stdout and "keyedStreams=13803" in p.stdout


@pytest.mark.skipif(not C001_MGRP.exists(), reason="p19 lab inputs absent")
def test_decode_deterministic():
    """Decode twice -> identical track JSON (determinism)."""
    import json as J
    for out in ("/tmp/p19_a.json", "/tmp/p19_b.json"):
        p = run_dec("tracks", C001_MGRP, 0, 5, "--json", out)
        assert p.returncode == 0, p.stderr
    a, b = J.load(open("/tmp/p19_a.json")), J.load(open("/tmp/p19_b.json"))
    assert a == b and a["frames"] == 30 and a["targets"] == 136


@pytest.mark.skipif(not C001_MGRP.exists(), reason="p19 lab inputs absent")
def test_tracks_vary_over_frames():
    """Not a single pose: at least one target's TRS must differ across
    frames in a known-moving clip."""
    import json as J
    p = run_dec("tracks", C001_MGRP, 0, 36, "--json", "/tmp/p19_t.json")
    assert p.returncode == 0
    d = J.load(open("/tmp/p19_t.json"))
    assert d["frames"] == 110
    moved = 0
    for t, frames in d["tracks"].items():
        f0, fl = frames[0], frames[-1]
        if (f0["rot"] != fl["rot"] or f0["tr"] != fl["tr"]):
            moved += 1
    assert moved >= 5, f"only {moved} targets moved"


@pytest.mark.skipif(not (C001_CHR.exists() and M002_CHR.exists()),
                    reason="p19 chr inputs absent")
def test_chr_skeletons():
    p1 = subprocess.run([sys.executable, str(ROOT / "tools" / "chr_check.py"),
                         "info", str(C001_CHR)], capture_output=True, text=True)
    p2 = subprocess.run([sys.executable, str(ROOT / "tools" / "chr_check.py"),
                         "info", str(M002_CHR)], capture_output=True, text=True)
    assert p1.returncode == 0 and "bones=136" in p1.stdout
    assert p2.returncode == 0 and "bones=92" in p2.stdout
    assert "roots=1" in p1.stdout and "roots=1" in p2.stdout


@pytest.mark.skipif(not (C001_CHR.exists() and C001_MGRP.exists()),
                    reason="p19 lab inputs absent")
def test_target_count_equals_bone_count():
    """Channel target i == bone i: every clip's targetCount == skeleton
    boneCount (identity map evidence)."""
    sys.path.insert(0, str(ROOT / "tools"))
    from mgrp_decode import Mgrp
    from chr_check import Chr
    f = Mgrp(str(C001_MGRP))
    nb = Chr(str(C001_CHR)).skl()["boneCount"]
    rec = f.records[0]
    for c in f.clips(rec):
        dec = f.decode_clip(c["blob"])
        assert dec["targets"] == nb


# ── P20: clip editing over the original rig ─────────────────────────────────
EDIT = ROOT / "tools" / "mgrp_edit.py"
C001_R0 = P19 / "c001_resident0.mgrp"


def run_edit(*args):
    return subprocess.run([sys.executable, str(EDIT), *map(str, args)],
                          capture_output=True, text=True)


@pytest.fixture
def work(tmp_path):
    import shutil
    p = tmp_path / "w.mgrp"
    shutil.copy(C001_R0, p)
    return p


@pytest.mark.skipif(not C001_R0.exists(), reason="p19 lab inputs absent")
def test_freeze_holds_all_keyed(work):
    p = run_edit("freeze", work, 0, 0, "all")
    assert p.returncode == 0, p.stderr
    p = run_dec("tracks", work, 0, 0, "--json", "/tmp/p20_w.json")
    assert p.returncode == 0
    import json as J
    d = J.load(open("/tmp/p20_w.json"))
    assert all(f[0] == f[-1] for f in d["tracks"].values())


@pytest.mark.skipif(not C001_R0.exists(), reason="p19 lab inputs absent")
def test_timing_shrink(work):
    assert run_edit("timing", work, 0, 0, 20).returncode == 0
    p = run_dec("tracks", work, 0, 0, "--json", "/tmp/p20_t2.json")
    assert p.returncode == 0
    import json as J
    assert J.load(open("/tmp/p20_t2.json"))["frames"] == 20


@pytest.mark.skipif(not C001_R0.exists(), reason="p19 lab inputs absent")
def test_synth_clip_decodes(work):
    p = run_edit("synth", work, 0, 1, 16, 118)
    assert p.returncode == 0, p.stderr
    p = run_dec("verify", work)
    assert p.returncode == 0, p.stdout
    import json as J
    run_dec("tracks", work, 0, 1, "--json", "/tmp/p20_s.json")
    d = J.load(open("/tmp/p20_s.json"))
    assert d["frames"] == 16 and d["targets"] == 118


@pytest.mark.skipif(not C001_R0.exists(), reason="p19 lab inputs absent")
def test_synth_oversize_refused(work):
    """Negative: synth bigger than the blob footprint must refuse."""
    p = run_edit("synth", work, 0, 1, 16, 9999)
    assert p.returncode == 2 and "footprint" in p.stderr


@pytest.mark.skipif(not C001_R0.exists(), reason="p19 lab inputs absent")
def test_corrupt_stream_rejected(work):
    """Negative: clobber a keyed stream's length -> decode must fail."""
    import struct
    sys.path.insert(0, str(ROOT / "tools"))
    from mgrp_decode import Mgrp
    d = bytearray(work.read_bytes())
    mg = Mgrp(str(work))
    blob = mg.clips(mg.records[0])[0]["blob"]
    ko = struct.unpack_from("<I", d, blob + 12)[0]
    nch = 9 * struct.unpack_from("<H", d, blob + 2)[0]
    mo = struct.unpack_from("<I", d, blob + 8)[0]
    vp = blob + ko
    for c in range(nch):
        mode = (d[blob + mo + (c * 2) // 8] >> ((c * 2) % 8)) & 3
        if mode == 3:
            struct.pack_into("<H", d, vp, 0xFFFF)   # absurd length
            break
        if mode == 2:
            vp += 2
    work.write_bytes(d)
    p = run_dec("verify", work)
    assert p.returncode == 2 and "bad=1" in p.stdout or "bad" in p.stdout


@pytest.mark.skipif(not C001_R0.exists(), reason="p19 lab inputs absent")
def test_const_rejects_keyed_channel(work):
    """Negative: const edit on a mode-3 channel must refuse."""
    sys.path.insert(0, str(ROOT / "tools"))
    from mgrp_decode import Mgrp
    mg = Mgrp(str(work))
    dec = mg.decode_clip(mg.clips(mg.records[0])[0]["blob"])
    k = next(i for i, c in enumerate(dec["channels"]) if c["mode"] == 3)
    p = run_edit("const", work, 0, 0, k, 100)
    assert p.returncode == 2 and "not const-i16" in p.stderr
