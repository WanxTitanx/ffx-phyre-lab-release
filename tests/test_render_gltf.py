"""Teste do rasterizador tools/render_gltf.py com glTF sintetico minimo."""
import base64
import importlib.util
import json
import struct
import sys
from pathlib import Path

import numpy as np
import pytest
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("render_gltf", ROOT / "tools" / "render_gltf.py")
render_gltf = importlib.util.module_from_spec(spec)
sys.modules["render_gltf"] = render_gltf
spec.loader.exec_module(render_gltf)


def _tiny_gltf(tmp_path):
    # um triangulo com UV; buffer embutido em base64
    pos = np.array([[-1.0, -1.0, 0.0], [1.0, -1.0, 0.0], [0.0, 1.0, 0.0]], np.float32)
    uv = np.array([[0.0, 0.0], [1.0, 0.0], [0.5, 1.0]], np.float32)
    idx = np.array([0, 1, 2], np.uint16)
    buf = pos.tobytes() + uv.tobytes() + idx.tobytes()
    uri = "data:application/octet-stream;base64," + base64.b64encode(buf).decode()
    tex = Image.new("RGBA", (4, 4), (200, 40, 40, 255))
    bio = __import__("io").BytesIO()
    tex.save(bio, "PNG")
    turi = "data:image/png;base64," + base64.b64encode(bio.getvalue()).decode()
    g = {
        "asset": {"version": "2.0"},
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [{"name": "root"}],
        "meshes": [{"primitives": [{"attributes": {"POSITION": 0, "TEXCOORD_0": 1}, "indices": 2}]}],
        "buffers": [{"uri": uri, "byteLength": len(buf)}],
        "bufferViews": [
            {"buffer": 0, "byteOffset": 0, "byteLength": pos.nbytes},
            {"buffer": 0, "byteOffset": pos.nbytes, "byteLength": uv.nbytes},
            {"buffer": 0, "byteOffset": pos.nbytes + uv.nbytes, "byteLength": idx.nbytes},
        ],
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3"},
            {"bufferView": 1, "componentType": 5126, "count": 3, "type": "VEC2"},
            {"bufferView": 2, "componentType": 5123, "count": 3, "type": "SCALAR"},
        ],
        "images": [{"uri": turi}],
        "textures": [{"source": 0}],
        "samplers": [{}],
        "materials": [{"pbrMetallicRoughness": {"baseColorTexture": {"index": 0}}}],
    }
    p = tmp_path / "tri.gltf"
    p.write_text(json.dumps(g))
    return p


def test_render_triangle_produces_pixels(tmp_path):
    p = _tiny_gltf(tmp_path)
    out = tmp_path / "out"
    argv = sys.argv
    sys.argv = ["render_gltf.py", str(p), str(out), "128"]
    try:
        render_gltf.main()
    finally:
        sys.argv = argv
    img = np.asarray(Image.open(out / "tri_front.png"))
    assert (img != 32).any(), "triangulo deveria rasterizar pixels != fundo"
    report = json.loads((out / "tri_report.json").read_text())
    assert report["vertices"] == 3 and report["primitives"] == 1
    assert report["skinned"] is False and report["animations"] == 0
    assert len(report["renders"]) == 6


def test_render_rejects_missing_file(tmp_path):
    with pytest.raises(FileNotFoundError):
        render_gltf.load_gltf(tmp_path / "nope.gltf")
