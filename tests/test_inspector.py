"""P14: PhyreInspector — headless render + error diagnostics, hermetic tests."""
import base64
import json
import struct
import subprocess
import zlib
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
PROJ = ROOT / "src" / "ui" / "PhyreInspector"


def run(*args):
    return subprocess.run(
        ["dotnet", "run", "--project", str(PROJ), "--", *args],
        capture_output=True, text=True, cwd=ROOT, timeout=300)


def _tiny_gltf(tmp_path, skinned=True):
    """One triangle, optional skin with 1 joint at weight 1.0."""
    pos = struct.pack("<9f", -1, -1, 0, 1, -1, 0, 0, 1, 0)
    uv = struct.pack("<6f", 0, 0, 1, 0, 0.5, 1)
    idx = struct.pack("<3H", 0, 1, 2)
    buf = pos + uv + idx
    views = [{"buffer": 0, "byteOffset": 0, "byteLength": len(pos)},
             {"buffer": 0, "byteOffset": len(pos), "byteLength": len(uv)},
             {"buffer": 0, "byteOffset": len(pos) + len(uv), "byteLength": len(idx)}]
    accs = [{"bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3"},
            {"bufferView": 1, "componentType": 5126, "count": 3, "type": "VEC2"},
            {"bufferView": 2, "componentType": 5123, "count": 3, "type": "SCALAR"}]
    attrs = {"POSITION": 0, "TEXCOORD_0": 1}
    doc = {"asset": {"version": "2.0"}, "scene": 0, "scenes": [{"nodes": [0]}],
           "nodes": [{"name": "n0"}],
           "meshes": [{"primitives": [{"attributes": attrs, "indices": 2, "mode": 4}]}],
           "buffers": [{"uri": "data:application/octet-stream;base64," +
                        base64.b64encode(buf).decode()}],
           "bufferViews": views, "accessors": accs}
    if skinned:
        j = struct.pack("<12H", *([0] * 12))
        w = struct.pack("<12f", 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0)
        ibm = struct.pack("<16f", *([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]))
        off = len(buf); buf += j
        views.append({"buffer": 0, "byteOffset": off, "byteLength": len(j)})
        off += len(j); buf += w
        views.append({"buffer": 0, "byteOffset": off, "byteLength": len(w)})
        off += len(w); buf += ibm
        views.append({"buffer": 0, "byteOffset": off, "byteLength": len(ibm)})
        accs += [{"bufferView": 3, "componentType": 5123, "count": 3, "type": "VEC4"},
                 {"bufferView": 4, "componentType": 5126, "count": 3, "type": "VEC4"},
                 {"bufferView": 5, "componentType": 5126, "count": 1, "type": "MAT4"}]
        attrs["JOINTS_0"] = 3
        attrs["WEIGHTS_0"] = 4
        doc["skins"] = [{"joints": [0], "inverseBindMatrices": 5, "skeleton": 0}]
        doc["buffers"][0]["uri"] = ("data:application/octet-stream;base64," +
                                    base64.b64encode(buf).decode())
    g = tmp_path / "tiny.gltf"
    g.write_text(json.dumps(doc))
    return g


def _png_variance(path):
    d = path.read_bytes()
    assert d[:8] == b"\x89PNG\r\n\x1a\n"
    return len(set(d)) > 8  # real content, not a flat/empty stream


def test_headless_render_produces_real_png(tmp_path):
    g = _tiny_gltf(tmp_path)
    out = tmp_path / "solid.png"
    r = run("--render", str(g), "--out", str(out))
    assert r.returncode == 0, r.stderr
    assert "prims" in r.stdout  # real stats, not canned text
    assert _png_variance(out)


def test_wireframe_differs_from_solid(tmp_path):
    g = _tiny_gltf(tmp_path)
    solid, wire = tmp_path / "s.png", tmp_path / "w.png"
    assert run("--render", str(g), "--out", str(solid)).returncode == 0
    assert run("--render", str(g), "--out", str(wire), "--wire").returncode == 0
    assert solid.read_bytes() != wire.read_bytes()


def test_missing_asset_actionable_error(tmp_path):
    r = run("--render", str(tmp_path / "nope.phyre"), "--out", str(tmp_path / "x.png"))
    assert r.returncode == 1
    assert "asset missing" in r.stderr
    assert "hint" in r.stderr  # actionable, not silent


def test_malformed_gltf_reports_error(tmp_path):
    bad = tmp_path / "bad.gltf"
    bad.write_text("{not json")
    r = run("--render", str(bad), "--out", str(tmp_path / "x.png"))
    assert r.returncode == 1
    assert "error:" in r.stderr


def _animated_gltf(tmp_path):
    """Skinned tri with 1 joint; animation rotates joint 0 -> 90deg around Z."""
    import math
    g = _tiny_gltf(tmp_path, skinned=True)
    doc = json.loads(g.read_text())
    buf = base64.b64decode(doc["buffers"][0]["uri"].split(",", 1)[1])
    # times [0,1], quat values [identity, rot90z]
    t = struct.pack("<2f", 0.0, 1.0)
    q = struct.pack("<8f", 0, 0, 0, 1, 0, 0, math.sin(math.pi / 4), math.cos(math.pi / 4))
    off = len(buf); buf += t
    doc["bufferViews"].append({"buffer": 0, "byteOffset": off, "byteLength": len(t)})
    off += len(t); buf += q
    doc["bufferViews"].append({"buffer": 0, "byteOffset": off, "byteLength": len(q)})
    doc["accessors"] += [
        {"bufferView": 6, "componentType": 5126, "count": 2, "type": "SCALAR",
         "min": [0.0], "max": [1.0]},
        {"bufferView": 7, "componentType": 5126, "count": 2, "type": "VEC4"}]
    doc["animations"] = [{"name": "spin", "channels": [
        {"sampler": 0, "target": {"node": 0, "path": "rotation"}}],
        "samplers": [{"input": 6, "output": 7, "interpolation": "LINEAR"}]}]
    doc["buffers"][0]["uri"] = ("data:application/octet-stream;base64," +
                                base64.b64encode(buf).decode())
    g.write_text(json.dumps(doc))
    return g


def test_pose_edit_changes_render(tmp_path):
    g = _tiny_gltf(tmp_path)
    a, b = tmp_path / "a.png", tmp_path / "b.png"
    assert run("--render", str(g), "--out", str(a)).returncode == 0
    r = run("--render", str(g), "--out", str(b),
            "--set-node", "0,0,0,0,0,0,45,1,1,1")
    assert r.returncode == 0, r.stderr
    assert "journal 1/1 ops" in r.stdout
    assert a.read_bytes() != b.read_bytes()


def test_undo_restores_bind_pose(tmp_path):
    g = _tiny_gltf(tmp_path)
    a, b = tmp_path / "a.png", tmp_path / "b.png"
    run("--render", str(g), "--out", str(a))
    r = run("--render", str(g), "--out", str(b),
            "--set-node", "0,0,0,0,0,0,45,1,1,1", "--undo", "1")
    assert r.returncode == 0, r.stderr
    assert "journal 0/1 ops" in r.stdout
    assert a.read_bytes() == b.read_bytes()  # undone == bind pose


def test_session_save_and_reopen(tmp_path):
    g = _tiny_gltf(tmp_path)
    ses = tmp_path / "s.session.json"
    out = tmp_path / "edited.png"
    r = run("--render", str(g), "--out", str(out),
            "--set-node", "0,1,0,0,0,0,0,1,1,1", "--session-out", str(ses))
    assert r.returncode == 0, r.stderr
    doc = json.loads(ses.read_text())
    assert doc["schema"] == "phyre-inspector-pose.v1" and len(doc["ops"]) == 1
    out2 = tmp_path / "reopened.png"
    r = run("--render", str(g), "--out", str(out2), "--session-in", str(ses))
    assert r.returncode == 0, r.stderr
    assert out.read_bytes() == out2.read_bytes()  # reopen reproduces edit


def test_animation_sample_changes_pose(tmp_path):
    g = _animated_gltf(tmp_path)
    t0, t1 = tmp_path / "t0.png", tmp_path / "t1.png"
    r0 = run("--render", str(g), "--out", str(t0), "--clip", "0", "--time", "0")
    r1 = run("--render", str(g), "--out", str(t1), "--clip", "0", "--time", "1")
    assert r0.returncode == 0 and r1.returncode == 0
    assert "1 anim(s)" in r0.stdout
    assert t0.read_bytes() != t1.read_bytes()  # scrub changes the pose


def test_clip_flag_rejected_when_no_animation(tmp_path):
    g = _tiny_gltf(tmp_path)  # no animations
    r = run("--render", str(g), "--out", str(tmp_path / "x.png"), "--clip", "0")
    assert r.returncode == 1
    assert "no animation" in r.stderr


def test_joint_weight_heatmap_paints_red(tmp_path):
    g = _tiny_gltf(tmp_path, skinned=True)
    out = tmp_path / "j.png"
    r = run("--render", str(g), "--out", str(out), "--joint", "0")
    assert r.returncode == 0, r.stderr
    # decode PNG and check for red-dominant pixels (heatmap ramp)
    d = out.read_bytes()
    pos, idat = 8, b""
    while pos + 8 <= len(d):
        ln = struct.unpack(">I", d[pos:pos + 4])[0]
        if d[pos + 4:pos + 8] == b"IDAT":
            idat += d[pos + 8:pos + 8 + ln]
        pos += 12 + ln
    raw = zlib.decompress(idat)
    red = sum(1 for i, b in enumerate(raw)
              if i % 4 == 1 and b > 200)  # R channel samples > 200
    assert red > 0, "joint weight heatmap produced no red pixels"
