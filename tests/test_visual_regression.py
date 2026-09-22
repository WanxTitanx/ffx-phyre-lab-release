"""P36: regressao visual do viewport (headless render harness).

Cada caso renderiza uma fixture local e compara com o PNG de referencia
(tests/visual/<nome>.png). Diferenca de pixels acima do limite = falha,
o que pega quebras de parser/raster que os testes de contagem nao veem.

As fixtures ficam em .lab/ (nao versionado); o teste e skipado sem elas.
Atualizar a referencia: pytest --update-visual
"""
import os
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
PROJ = ROOT / "src" / "ui" / "FfxLab" / "FfxLab.csproj"
REFDIR = Path(__file__).resolve().parent / "visual"
OUTDIR = ROOT / ".lab" / "out" / "visual"

# limite de pixels diferentes (fracao) por caso
THRESHOLD = 0.02

CASES = [
    # nome, env, argumentos (relativos a ROOT)
    # geom bin primeiro (o --render-test nao auto-detecta a ordem do par)
    ("map_13_0043",
     {"FFX_BRIGHTNESS": "2.4"},
     ["--render-test", ".lab/noclip-data/FinalFantasyX/13/0043.bin",
      ".lab/noclip-data/FinalFantasyX/13/0042.bin", "{out}", "600"]),
    ("actor_npc_1c0000",
     {},
     ["--render-actor", ".lab/fetched/1c/0000.bin", "{out}", "500"]),
    ("encounter_0002",
     {"FFX_BRIGHTNESS": "1.0", "FFX_NO_OVERLAY": "1"},
     ["--render-enc", "x", ".lab/fetched/0e/0002.bin",
      ".lab/fetched/1a/0041.bin", ".lab/fetched/1a/0040.bin", "{out}", "600"]),
]


def _inputs_ready(args):
    for a in args:
        if a.endswith(".bin"):
            p = ROOT / a
            if not p.exists():
                return False
    return True


def _render(env_extra, args, out):
    env = dict(os.environ)
    env.update(env_extra)
    cmd = ["dotnet", "run", "--project", str(PROJ), "--no-build", "--"]
    cmd += [a.replace("{out}", str(out)) for a in args]
    p = subprocess.run(cmd, capture_output=True, text=True, cwd=ROOT,
                       env=env, timeout=600)
    assert p.returncode == 0, p.stderr[-2000:]
    assert out.exists(), f"render nao gerou {out}"


def _png_diff_ratio(a, b):
    """Fraction of pixels whose channel-sum differs by more than 30/765."""
    from PIL import Image
    ia = Image.open(a).convert("RGB")
    ib = Image.open(b).convert("RGB")
    if ia.size != ib.size:
        return 1.0
    ba = ia.tobytes()
    bb = ib.tobytes()
    n = len(ba) // 3
    diff = 0
    for i in range(0, len(ba), 3):
        d = (abs(ba[i] - bb[i]) + abs(ba[i + 1] - bb[i + 1])
             + abs(ba[i + 2] - bb[i + 2]))
        if d > 30:
            diff += 1
    return diff / max(1, n)


@pytest.mark.parametrize("name,env,args", CASES, ids=[c[0] for c in CASES])
def test_visual_regression(name, env, args):
    if not _inputs_ready(args):
        pytest.skip(f"fixtures de {name} ausentes em .lab/")
    if not PROJ.exists():
        pytest.skip("projeto FfxLab ausente")
    pytest.importorskip("PIL")

    OUTDIR.mkdir(parents=True, exist_ok=True)
    out = OUTDIR / f"{name}.png"
    _render(env, args, out)

    ref = REFDIR / f"{name}.png"
    if os.environ.get("UPDATE_VISUAL") == "1" or not ref.exists():
        REFDIR.mkdir(parents=True, exist_ok=True)
        ref.write_bytes(out.read_bytes())
        pytest.skip(f"referencia criada/atualizada: {ref}")

    ratio = _png_diff_ratio(ref, out)
    assert ratio <= THRESHOLD, (
        f"{name}: {ratio:.2%} dos pixels diferentes (limite {THRESHOLD:.0%}); "
        f"ref={ref} out={out}")
