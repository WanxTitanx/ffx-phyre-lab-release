<h1 align="center">FFX Phyre Lab</h1>

<p align="center"><b>Reconstruindo tooling nível PhyreEngine para FINAL FANTASY X HD — um byte comprovado por vez.</b></p>

<p align="center">Parte do ecossistema <b>FFX Mod Studio</b> · 🇧🇷 Feito por desenvolvedor brasileiro — WanxTitanx</p>

<p align="center">
  <img src="https://img.shields.io/badge/status-ALPHA-orange" alt="status">
  <a href="LICENSE"><img src="https://img.shields.io/badge/licen%C3%A7a-GPL--3.0-blue" alt="licença"></a>
  <img src="https://img.shields.io/badge/.NET-10%20%2B%20Avalonia-purple" alt="dotnet">
  <img src="https://img.shields.io/badge/Python-3.10%2B-blue" alt="python">
  <img src="https://img.shields.io/badge/plataforma-Windows%20x64%20%C2%B7%20Linux-informational" alt="plataforma">
  <a href="https://store.steampowered.com/app/359870"><img src="https://img.shields.io/badge/jogo-FFX%2FFF--X--2%20HD%20Remaster%20(Steam)-green" alt="jogo"></a>
</p>

<p align="center"><a href="README.md"><b>English version →</b></a></p>

---

## O que é isso?

Laboratório open-source de pesquisa cujo objetivo final é uma **toolchain
PhyreEngine completa e compatível com o FFX**: abrir assets reais do jogo,
inspecionar e editar malhas, esqueletos, animações, partículas, mapas e dados do
kernel — e gravar resultados que o `FFX.exe` real aceita. Suporte a FFX-2 está
no horizonte: mesma família de engine, mesmo layout de dados.

Nenhum asset do jogo, código de SDK ou executável é distribuído aqui. Você
aponta as ferramentas para a **sua** extração local do FFX HD.

## Status — ALPHA

Software de nível pesquisa. O que está **comprovado** hoje:

- **Reader de cluster `.phyre`** — namespace de classes autodescritivo, blocos
  de objetos, shared data, payloads externos de vértice/índice (tags `RYHP`).
- **Writers com preservação** — patch exato por região mantendo dados
  desconhecidos/opacos intactos, com journal, save atômico e undo.
- **Prova real no jogo** — uma alteração mínima persistente (72 texturas de
  mapa via rota de overlay) foi carregada e observada no jogo de verdade.
- **App desktop FfxLab** — renderer de cena/mapa (raster CPU + GPU), playback
  de animação de ator, keyframes EFFECT, simulação de partículas PPP, edição
  de encontros, swap de ator EV01, edição de tabelas kernel, vértices/cores.
- **Intercâmbio** — schema JSON versionado `phyre.project.v1` + roundtrip
  glTF/Blender para malhas, rigs e skinning.
- **Decode/edição de `.mgrp`** e **disassembler ATEL** (eventos + encontros).

**Em andamento / ainda não:** autoria de clip completa aceita pelo jogo,
injeção de monstro sintético, autoria de mapa jogável ponta a ponta, FFX-2.
Um viewer bonito ou um glTF exportado **não** contam como prova de
compatibilidade — veja os gates em `docs/ACCEPTANCE.md`.

## Componentes

| Caminho | O que é |
|---|---|
| `src/ui/FfxLab` | Lab desktop Avalonia — viewer/editor de cena de mapa, viewport GL, sim de partículas |
| `src/ui/PhyreInspector` | Inspector Avalonia — malhas, texturas, esqueleto, animação, export glTF |
| `tools/phyre-reader` | CLI parser de cluster `.phyre` |
| `tools/phyre-exporter` | extração de modelo skinned/texturas |
| `tools/phyre-patcher` | writer binário exato por região, com preservação |
| `tools/phyre-project` | arquivos de projeto editável persistente (schema v1) |
| `tools/phyre-meshedit` | edição de vértice/cor/topologia, scene/worldmat |
| `tools/ffx-map1` | cena MAP1: walkmesh, lighting, LEVEL_PART, emissores PPP, bins kernel, ATEL, magic VM |
| `tools/*.py` | corpus, checks mgrp/chr/mapa, IO Blender, render glTF, ponte noclip |
| `tests/` | suíte pytest — testes de corpus pulam limpo sem dados locais |
| `schema/` | schema JSON `phyre.project.v1` |
| `docs/` | notas de formato: intercâmbio, formato alvo, mapas, animação, censo do editor |

## Requisitos

- **.NET 10 SDK** (tools + apps) · **Python 3.10+** (`pytest`, `jsonschema`,
  `numpy`, `Pillow` para partes da suíte) · **Blender** opcional para os
  testes de roundtrip.
- Sua própria extração do **FFX HD** — defina `FFX_ASSETS` e rode
  `scripts/build_corpus.py` para montar o `.lab/corpus` local (nunca commitado).
- Opcional: bundle local **noclip** `dist-ffxstudio` (`NOCLIP_BUNDLE` ou
  `vendor/dist-ffxstudio`) para a ponte do viewer.

## Começo rápido

```bash
git clone https://github.com/WanxTitanx/ffx-phyre-lab-release.git
cd ffx-phyre-lab-release

dotnet build src/ui/FfxLab          # lab desktop
dotnet build src/ui/PhyreInspector  # inspector

export FFX_ASSETS=/caminho/da/extracao/ffx
python3 scripts/build_corpus.py     # corpus local (dev + holdout + negativos)
python3 -m pytest tests/            # testes de corpus pulam sem .lab
```

## Legal

FINAL FANTASY, FINAL FANTASY X e FINAL FANTASY X-2 são marcas da Square Enix
Holdings Co., Ltd. Projeto independente de fã — sem afiliação ou endosso da
Square Enix ou da Sony. **Nenhum asset do jogo nem material proprietário de SDK
é distribuído.** Referências a PhyreEngine são apenas de oráculo local.

## Licença

[GPL-3.0](LICENSE) — veja [NOTICE.md](NOTICE.md) e
[THIRD_PARTY.md](THIRD_PARTY.md) para proveniência e atribuição.
