"""P28 — MAP1 (mapout.vpa) validator port tests.

Fixtures staged under .lab/runs/p28-map/ (same pattern as test_mapscene):
  znkd06_mapout.vpa  — field map, ok, YNDT 46 tris/44 verts
  kami03_a_mapout.vpa — battle arena, nometa (valid container state)
  azit08_stub.vpa    — 64B header-only stub (valid)
"""
import json
import os
import subprocess
import sys

import pytest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOOL = os.path.join(ROOT, "tools", "map1_check.py")
FIX = os.path.join(ROOT, ".lab", "runs", "p28-map")
ZNKD06 = os.path.join(FIX, "znkd06_mapout.vpa")
KAMI03A = os.path.join(FIX, "kami03_a_mapout.vpa")
AZIT08 = os.path.join(FIX, "azit08_stub.vpa")
EBP = os.path.join(FIX, "znkd0600.ebp")

sys.path.insert(0, os.path.join(ROOT, "tools"))
import map1_check  # noqa: E402

pytestmark = pytest.mark.skipif(
    not os.path.isfile(ZNKD06), reason="P28 fixture not staged")


def run(*args):
    return subprocess.run([sys.executable, TOOL, *args],
                          capture_output=True, text=True)


def test_check_znkd06_ok():
    r = run("check", ZNKD06)
    assert r.returncode == 0
    assert "status=ok" in r.stdout
    assert "tris=46" in r.stdout and "verts=44" in r.stdout


def test_check_valid_container_states_pass():
    for f in (KAMI03A, AZIT08):
        r = run("check", f)
        assert r.returncode == 0, f
    assert "status=nometa" in run("check", KAMI03A).stdout
    assert "status=stub" in run("check", AZIT08).stdout


def test_check_bad_magic_fails(tmp_path):
    bad = tmp_path / "bad.vpa"
    bad.write_bytes(b"XXXX" + b"\0" * 200)
    r = run("check", str(bad))
    assert r.returncode == 1
    assert "bad-magic" in r.stdout


def test_truncated_map1_is_stub_not_crash(tmp_path):
    trunc = tmp_path / "trunc.vpa"
    trunc.write_bytes(open(ZNKD06, "rb").read(100))
    r = run("check", str(trunc))
    assert r.returncode == 0
    assert "status=stub" in r.stdout


def test_slots_resolve_named_sections():
    b = open(ZNKD06, "rb").read()
    slots = map1_check.parse_header(b)
    assert slots["geom"]["off"] > 0
    assert slots["meta/ppp"]["off"] > 0
    assert slots["guide"]["magic"] == "YNDT"


def test_dispatch_law_size_2x_tag():
    """Record size = 2*tag; sorted rows tile the region (P28 core law)."""
    b = open(ZNKD06, "rb").read()
    geom, meta, dabs, rows = map1_check.read_dispatch(b)
    assert len(rows) == 14
    walk, contig, pairs, gaps = map1_check.walk_dispatch(b, rows, geom)
    assert pairs == len(rows) - 1
    assert contig == pairs  # znkd06 tiles exactly


def test_yndt_walkmesh_decode():
    b = open(ZNKD06, "rb").read()
    y = map1_check.decode_yndt(b)
    assert y["triCount"] == 46 and y["vertCount"] == 44
    assert y["bad_index"] == 0 and y["const68"]
    assert len(y["tris"]) == 46 and len(y["verts"]) == 44


def test_ebp_event_package_magic():
    """EV01 = FFX field event script package (contract §6b)."""
    with open(EBP, "rb") as f:
        hdr = f.read(0x20)
    assert hdr[:4] == b"EV01"


def test_census_numbers_replicate_reference():
    """Reference-validated census values (map1_families 2026-09-15 report)
    replicated on the same corpus — run only if corpus root is mounted."""
    corpus = os.path.join(os.environ.get("FFX_ASSETS", ""),
                          "ffx_ps2/ffx/master/jppc")
    if not os.path.isdir(corpus):
        pytest.skip("jppc corpus not mounted")
    import io
    import contextlib
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        map1_check.cmd_census(corpus)
    s = json.loads(buf.getvalue())
    assert s["files"] == 491
    assert s["status_counts"]["ok"] == 286
    assert s["yndt_decoded_ok"] == 262
    assert s["stream_walk_pairs"] == 1525
    assert s["stream_walk_contig"] == 1505
    assert s["stream_shape_records"]["quad40"] == 533
    assert s["f1_zone_triangles_total"] == 465
