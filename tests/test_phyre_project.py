"""P13: persistent project ops — hermetic tests (synthetic source, no corpus)."""
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROJ = ROOT / "tools" / "phyre-project"


def run(*args, cwd=None):
    return subprocess.run(
        ["dotnet", "run", "--project", str(PROJ), "--no-build", "--", *args],
        capture_output=True, text=True, cwd=cwd or ROOT)


def _new_project(tmp):
    src = tmp / "blob.bin"
    src.write_bytes(bytes(range(256)) * 4)  # 1024 deterministic bytes
    d = tmp / "proj"
    r = run("new", str(src), str(d), "src0")
    assert r.returncode == 0, r.stderr
    return src, d


def test_new_stores_source_by_hash(tmp_path):
    src, d = _new_project(tmp_path)
    doc = json.loads((d / "project.json").read_text())
    assert doc["schema"] == "phyre.ops.v1"
    s = doc["sources"][0]
    stored = d / s["stored"]
    assert stored.read_bytes() == src.read_bytes()  # content-addressed copy
    import hashlib
    assert s["sha256"] == hashlib.sha256(src.read_bytes()).hexdigest()


def test_patch_undo_redo_materialize(tmp_path):
    src, d = _new_project(tmp_path)
    # patch 4 bytes at offset 16 -> 0xAA
    r = run("add-patch", str(d), "src0", "16", "aaaaaaaa")
    assert r.returncode == 0, r.stderr
    out = tmp_path / "out.bin"
    assert run("materialize", str(d), str(out)).returncode == 0
    expected = bytearray(src.read_bytes())
    expected[16:20] = b"\xaa" * 4
    assert out.read_bytes() == bytes(expected)
    # undo -> materialize equals source again
    assert run("undo", str(d)).returncode == 0
    assert run("materialize", str(d), str(out)).returncode == 0
    assert out.read_bytes() == src.read_bytes()
    # redo -> patch back
    assert run("redo", str(d)).returncode == 0
    assert run("materialize", str(d), str(out)).returncode == 0
    assert out.read_bytes() == bytes(expected)


def test_reopen_semantic_equality(tmp_path):
    """new process re-verify: materialized hash equals recorded result."""
    src, d = _new_project(tmp_path)
    run("add-patch", str(d), "src0", "32", "ff00ff00")
    out = tmp_path / "out.bin"
    run("materialize", str(d), str(out))
    r = run("verify", str(d))  # separate process
    assert r.returncode == 0, r.stderr + r.stdout
    rep = json.loads(r.stdout)
    assert rep["semantic_equal"] is True and rep["ops"] == 1


def test_missing_source_diagnostic(tmp_path):
    src, d = _new_project(tmp_path)
    # delete stored copy AND external hint -> verify must name it, exit 5
    doc = json.loads((d / "project.json").read_text())
    os.remove(d / doc["sources"][0]["stored"])
    src.unlink()
    r = run("verify", str(d))
    assert r.returncode == 5
    rep = json.loads(r.stdout)
    assert rep["missing"][0]["sha256"] == doc["sources"][0]["sha256"]


def test_journal_recovery(tmp_path):
    src, d = _new_project(tmp_path)
    run("add-patch", str(d), "src0", "8", "01020304")
    run("add-patch", str(d), "src0", "64", "deadbeef")
    # simulate crash: project.json lost
    os.remove(d / "project.json")
    r = run("recover", str(d))
    assert r.returncode == 0, r.stderr
    rep = json.loads(r.stdout)
    assert rep["recovered"] and rep["ops"] == 2
    # recovered project materializes the same bytes
    out = tmp_path / "out.bin"
    assert run("materialize", str(d), str(out)).returncode == 0
    expected = bytearray(src.read_bytes())
    expected[8:12] = bytes.fromhex("01020304")
    expected[64:68] = bytes.fromhex("deadbeef")
    assert out.read_bytes() == bytes(expected)


def test_out_of_bounds_patch_rejected(tmp_path):
    src, d = _new_project(tmp_path)
    r = run("add-patch", str(d), "src0", "1022", "aabbccdd")  # 1022+4 > 1024
    assert r.returncode == 3


def test_new_op_truncates_redo_tail(tmp_path):
    src, d = _new_project(tmp_path)
    run("add-patch", str(d), "src0", "8", "01020304")
    run("add-patch", str(d), "src0", "12", "05060708")
    run("undo", str(d), "2")
    run("add-patch", str(d), "src0", "40", "cafebabe")
    doc = json.loads((d / "project.json").read_text())
    assert len(doc["ops"]) == 1 and doc["cursor"] == 1
    assert doc["ops"][0]["offset"] == 40
