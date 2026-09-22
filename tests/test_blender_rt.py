# P21 — Blender round-trip with identity preservation.
# Corpus-gated: needs the exported m001 glTF (P08) + local Blender install.
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
TAG = ROOT / "tools" / "gltf_tag.py"
BIO = ROOT / "tools" / "blender_io.py"
RT = ROOT / "tools" / "rt_check.py"
GLTF = ROOT / ".lab" / "runs" / "p08-m001" / "m001_skinned.gltf"
SRC = Path(os.environ.get("FFX_ASSETS", "")) / \
    "ffx_data/gamedata/ps3data/chr/mon/m001/mdl/d3d11/m001.dae.phyre"
BLENDER = shutil.which("blender") or str(
    Path.home() / ".local/opt/blender-5.0.1/blender")

P21 = ROOT / ".lab" / "runs" / "p21-blender"


def run(*a):
    return subprocess.run([sys.executable, *map(str, a)],
                          capture_output=True, text=True)


@pytest.fixture
def tagged(tmp_path):
    out = tmp_path / "tagged.gltf"
    p = run(TAG, GLTF, out, SRC, "--profile", "ffx-hd")
    assert p.returncode == 0, p.stderr
    return out


pytestmark = pytest.mark.skipif(not GLTF.exists() or not SRC.exists(),
                                reason="p08 gltf/corpus absent")


def test_tag_injects_provenance(tagged):
    g = json.load(open(tagged))
    assert g["asset"]["extras"]["phyre_source"]["sha256"]
    ids = [n.get("extras", {}).get("phyre_id") for n in g["nodes"]]
    assert all(ids) and len(set(ids)) == len(ids)


def test_rt_check_identical_self(tagged):
    """A file checked against itself must pass with zero loss."""
    p = run(RT, tagged, tagged)
    assert p.returncode == 0
    rep = json.loads(p.stdout)
    assert rep["result"] == "pass" and rep["dropped"] == 0


def test_rt_check_detects_damage(tagged, tmp_path):
    """Negative: drop a node name -> identity check must fail."""
    g = json.load(open(tagged))
    del g["nodes"][3]["name"]
    bad = tmp_path / "bad.gltf"
    json.dump(g, open(bad, "w"))
    p = run(RT, tagged, bad)
    assert p.returncode == 2


@pytest.mark.skipif(not Path(BLENDER).exists(), reason="blender absent")
def test_blender_roundtrip(tmp_path):
    out = tmp_path / "back.gltf"
    p = subprocess.run([BLENDER, "-b", "-P", str(BIO), "--", "import",
                        str(P21 / "m001_tagged.gltf"), str(out)],
                       capture_output=True, text=True, timeout=300)
    assert "BLENDER_RT_OK" in p.stdout, p.stdout[-500:]
    chk = run(RT, P21 / "m001_tagged.gltf", out,
              "--report", tmp_path / "rt.json")
    assert chk.returncode == 0, chk.stdout
    rep = json.load(open(tmp_path / "rt.json"))
    # additive deltas are reported but not identity loss
    assert rep["result"] == "pass" and rep["loss"]["dropped"] == []
    assert any(c["check"] == "vert_count" for c in rep["loss"]["changed"])
