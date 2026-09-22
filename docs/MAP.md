# Mapas FFX — scene graph, edição e distinção battle/field

Medido em 2026-09-21 sobre um corpus local de assets extraídos do FFX HD
(não distribuído neste repo), phyre-meshedit `scene`/`worldmat`,
SDK PhyreEngine como referência de layout (uso local, não redistribuído).

## 1. Layout dos diretórios

| Lado | Conteúdo |
|---|---|
| `ffx_data/gamedata/ps3data/map/<região>/<mapa>/` | **Field map** — `mdl/d3d11/<mapa>.dae.phyre` (cena 3D), `tex/d3d11/*.dds.phyre`, `mdl/d3d11/textureanimation.ags.phyre`, `<mapa>.ahwin32`, variante `2d/` |
| `ffx_data/gamedata/ps3data/btlmap/<região>/<mapa>NN_<var>/` | **Battle arena** — mesma estrutura-base, + `fp/tex/` (tiles de textura) e `tex/texswap/` (variantes). Sufixo `_a`, `_b` = variante de arena |
| `ffx_ps2/ffx/master/jppc/map/<região>/<mapa>/bin/mapout.vpa` | pacote PS2-era com magic `MAP1` — dados de placement/lógica do mapa fora do .phyre |

Nem toda região tem `btlmap/` (znkd não tem — a luta tutorial acontece no
próprio field map znkd08).

## 2. Scene graph dentro do `.dae.phyre`

Mesma base para field e battle:

- **PNode** (84B/elem): `[16B header][PMatrix4 local row-major 64B][4B tail]`
  — a matriz local do nó fica inline em `+16`.
- **PWorldMatrix** (48B/elem): `PMatrix4x3` packed — `m_col1.w=col0.x`,
  `m_col2.w=col0.y`, `m_col3.w=col0.z`, `m_col3.xyz=translation`
  (confirmado em `PhyreMatrix4x3.h` do SDK).
- **Links** (object-links): PNode→PNode (hierarquia), PNode→PWorldMatrix,
  PMeshInstance→PWorldMatrix (1:1 em znkd06: `inst[i] ↔ wm[i+1]`),
  PCamera*→PWorldMatrix, PMesh→PMeshSegment/PMaterial,
  PMaterial→PAssetReference (nome) + PParameterBuffer (params),
  PParameterBuffer→PAssetReference (texturas) + PSamplerState +
  PShaderParameterDefinition.
- **PAssetReferenceImport**: records zerados + **tabela de strings no rabo
  do bloco** — shader `PhyreDefaultLitShader.fx#<hash>` por variante,
  paths `PS3Data/.../*.dds`, nomes collada `#_collada_*`.

znkd06: 64 nós, 49 world matrices, 44 instâncias, 59 materiais, 5 câmeras.
znkd08 (mapa da luta tutorial): 486 nós, 369 wm, 364 instâncias.

## 3. Battle vs field — distinção medida

- **Diretório e nome**: `btlmap/` + sufixo `_<letra>` (bsil03_a, maca00_a).
- **DynamicGeometry/morph**: blocos `PDynamicDataBlock`,
  `PDynamicSegmentDesc`, `PModifierNetwork*`, `PMorphModifierWeights*`,
  `PRenderStream` aparecem **só em arenas de batalha** (censo: ~3/12
  btlmap vs 0/12 field) — geometria animada de arena, não marcador universal.
- **Extras de diretório**: `fp/tex/` e `tex/texswap/` só em battle.
- `mapout.vpa` (placement/script) existe no lado jppc dos dois tipos.

## 4. Edição (phyre-meshedit)

```
phyre-meshedit scene <in>                          # dump do grafo completo
phyre-meshedit worldmat <in> <out> --matrix M [--translate x,y,z] [--scale s]
phyre-meshedit worldmat <in> <out> --node N  [--translate x,y,z] [--scale s]
```

- `--matrix M`: patcheia `PWorldMatrix[M]` — translação em `m_col3.xyz`
  (f[8..10]), escala multiplica as 9 floats de base.
- `--node N`: patcheia `PNode[N].local` (PMatrix4 em +16; translação na
  linha 3, escala na base 3×3).
- Localidade provada: `--translate` altera exatamente 4 bytes (float alvo);
  arquivo sai com tamanho idêntico.

Para efeito em runtime o alvo era `PWorldMatrix` (hipótese: o que a instância
consome no draw); `PNode.local` é a fonte autoral. **Refutado em runtime —
ver §5b.**

## 5b. Camada de transforms NÃO é consumida no PC (P27)

Três probes isolados via `data/mods` em znkd06, todos servidos pelo loader
(OUTPUT.TXT por-run) e renderizando idêntico ao baseline:

| Probe | Alvo | Records | Resultado |
|---|---|---|---|
| A | `PWorldMatrix` das 44 instâncias, ty+300 | 44 | inerte |
| B | `PNode.local` dos nós 20–63 (1:1 instâncias) | 44 | inerte |
| C | `PWorldMatrix` wm[0] = camP0 (câmera), ty+300 | 1 | inerte |

`PMeshInstance` (132B) é record todo-zero — sem transform inline. Combinado
com P16 (vertex stream +300Y **visível** no mesmo cenário) e P23 (skeleton
inerte): **o render D3D11 do FFX PC consome vértices em world-space; a camada
PNode/PWorldMatrix/PCamera/PMeshInstance é metadado serializado não
consumido** — nem para instâncias nem para a câmera da cutscene (dirigida por
dados de motion/evento, cf. P20). Consequência para o editor: deslocamento de
geometria de mapa se faz editando `PVertexStream` (+ bounds), não transforms;
placement de entidades de gameplay vive fora do `.dae.phyre` (`mapout.vpa`,
`.ahwin32`, event data — escopo de P28).

## 5. Persistência em projeto (`phyre.ops.v1`)

Edições worldmat viram patches byte-level no journal:

```
phyre-project new <src> <projdir>
phyre-project add-patch <projdir> <key> <offset> <hex>   # por diff-run
phyre-project materialize <projdir> <out>                # replay = byte-idêntico
```

Testado: patch de 4 bytes (wm[1] ty+300) materializa com sha256 idêntico
à saída direta do meshedit. Undo/redo pelo cursor do journal.

## 6b. Contratos de mapa jogável — `mapout.vpa` (MAP1) e eventos (P28)

O placement/gameplay **não** mora no `.dae.phyre` (§5b) — mora na árvore
`ffx_ps2/ffx/master/jppc/`, que o loader do PC **requisita ao vivo**
(`OUTPUT.TXT` mostra `../../../ffx_ps2/ffx/...`; mods dir já serve
`jppc/.../mapout.vpa`). Consumidor identificado em IDA:
`FFX_FieldMap_ProcessMapDataBlob @0x9097C0` (PC FFX.exe).

**`mapout.vpa`** (magic `MAP1`, header 0x80 = slot table):

| Slot | Conteúdo | Consumidor |
|---|---|---|
| +0x10 | cena (YNDT/YNPR/YNSC/YNTM) | scene |
| +0x14 | GS DMA packet | upload direto |
| +0x18 | **geom** — base dos offsets de dispatch | — |
| +0x38 | meta/PPP → `FFX_Render_LoadPppResourceBlob` | partículas |
| +0x3C | guide `YNDT` → `Yn_GuideMapSetData` | **walkmesh** |
| +0x40 | `Yn_FpSetData` | field path |

Meta block: `u32(meta+0x1C)` = offset da dispatch table (rows 8B
`{u16 key, u16 tag, u32 blobOff}`, blobOff relativo a geom).
**Lei: tamanho do record = 2×tag**; rows ordenadas tilelam a região
contiguamente (98,7% corpus). Zone-tags 0x19/0x71/0x0004 = janelas de zona.

**Onde mora cada contrato:**

| Contrato | Local | Formato | Cobertura corpus |
|---|---|---|---|
| Walkmesh/guia | slot +0x3C → `YNDT`+`YNGM` | tris 20B `{3×RGBA sentinel, iA,iB,iC,0}` + pool `vc×6B` s16 X,Y,Z | **259/259** field ok; 3 btlmap |
| Encounter zones | zone rows → `s16framed` | 16B `{3×(s16 X,Z), flags, -2}` scale 1/256 | 6 mapas field (znkd08!, bika02/03, kino09, mtgz09/11) |
| Zone painting | record stream | soup32/quad40/paint20/edge24 (verts packed + cores RGBA zona) | 47 arquivos |
| Navmesh | ring stream F2 | mesmos records s16 sentinel | mihn00 (95 tris) |
| Placement xforms | zone rows `float32` | records 0x32 (escala identidade, −4.0f) | mihn00/kino05/djyt04 |
| PPP/partículas | meta + range16 | `{start,end,id,f,k}` (entradas de programa) | 8 arquivos |
| Cena/DMA/fp | slots +0x10/+0x14/+0x40 | YN-family/DMA | decodificação pendente |
| **Eventos/scripts** | `event/obj/<r>/<m><cena>/<m><cena>.ebp` | magic `EV01` + tabela de seções + strings Shift-JIS | identificado; bytecode RE-pendente |
| Cutscene motion | `event/obj/.../*.mgrp` | codec MGRP (P18/P19) | 100% decodificado |
| Câmera/spawn/transição | dentro do `.ebp` (bytecode tipo ATEL) + DMA slot +0x14 | — | **RE-pendente** |
| Manifesto | `<mapa>.ahwin32` | tabela de índices de assets (sem gameplay) | decodificado |

Censo (parser `tools/map1_check.py`, port do codec validado): **491/491**
arquivos — ok 286, stub 103, nometa 71, nodispatch 31; YNDT ok 262;
walk pairs 1505/1525 contíguos; shapes: quad40 533, edge24 130, range16 79,
soup32 74, paint20 54 records; F1: 465 tris de zona; F2: 95 tris navmesh.
Distribuição field vs battle: field `map/` 433 (259 ok, todos com YNDT),
`btlmap/` 58 (27 ok, 3 com YNDT — arena usa outro mecanismo).

Comandos: `map1_check.py check|info|census`.

## 7. Não provado / próximos passos

- ~~Efeito runtime do `worldmat`~~ — **P27 refutou**: transforms serializados
  inertes no render PC (ver §5b). Placement de geometria = `PVertexStream`.
- ~~`mapout.vpa` (MAP1)~~ — **P28 fechou o contrato** (§6b); pendente só o
  bytecode `.ebp` (EV01) e a prova runtime de edit de zona/walkmesh.
- Edição de materiais/parâmetros (PParameterBuffer values, PSamplerState).
- `textureanimation.ags.phyre` — animação de UV/material.
- Resolução nome↔material via SharedDataId nos object-links (strings já
  expostas na tabela de imports; vínculo fino por confirmar).
