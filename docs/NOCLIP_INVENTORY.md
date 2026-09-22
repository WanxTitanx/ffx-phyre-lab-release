# NOCLIP inventory — FFX (Fahrenheit) renderer stack

Inventário da lógica do **noclip.website módulo Fahrenheit** (build local em
`ffx-editor-main/ExternalLibs/NoclipViewer/`, snapshot `39605028765aa2cfaf2cea175f01f3a77cd99c2e`,
MIT). Referência TypeScript: `ffx-editor-main/research_tools/Noclip/noclip_reference/`
(21.669 linhas, 7 arquivos). O viewer renderiza cenas **como no jogo** porque lê os
mesmos pacotes de dados que o runtime consome para gameplay (`mapout.vpa` MAP1,
bins de ator, partículas PPP, eventos ATEL) e emula o pipeline PS2 GS.

## Arquitetura em 3 camadas

```
bin.ts      → parsers: MAP1 (geo/heightmap/particles/guidemap/water), atores, anim, texturas GS
script.ts   → VM ATEL: Threads/Workers/Signals, MotionState, queries de heightmap, LevelObjectHolder
render.ts   → renderer: FFXProgram shader, 12 layers ordenados, GS state→megaState, instâncias
GS.ts       → modelo do Graphics Synthesizer PS2 (registradores, memória 4MB swizzled, pixel fmts)
particle.ts → sistema PPP completo (Emitter, Flipbook, Trail, Water, PointChain)
magic.ts    → magic/overdrive layouts + MagicSceneRenderer
actor.ts    → Actor (flags, FloorMode, shadow), MonsterParticle
```

## bin.ts — o que cada slot do MAP1 contém (resposta definitiva)

`parseMapFile(id, buffer, textureData)`:

| Slot | Conteúdo | Uso |
|------|----------|-----|
| +0x14 | `parseLevelGeometry` — stream de seções | **geometria renderizável** |
| +0x18 | `HeightMap` | **colisão/walkmesh + encontro** |
| +0x38 | `parseParticleData` | partículas PPP do mapa |
| +0x3C | "guidemap" (YNDT) | minimapa |
| +0x40 | `parseParticleWaterTex` | texturas de água (mesmo formato guidemap) |

### HeightMap (+0x18) — a superfície editável de gameplay

- Header: `hasLight = u16@+4 > 0x1202`, `vertexCount = u16@+0xA`, `scale = f32@+0xC / LEVEL_MODEL_SCALE`,
  `vertexOffs = u32@+0x18` (relativo à seção), `triOffs = u32@+0x1C` → `triCount = u16@triOffs+8`,
  records em `u32@triOffs+0xC` + seção.
- Vértices: `Int16 x4` por vértice.
- **MapTri = 16 bytes**: `u16 v0,v1,v2`, `s16 e01,e12,e20` (adjacência — índices das tris vizinhas),
  `u32 data`:
  - `data & 0x7F` → **passability** (≠0 = colisão)
  - `(data>>>7) & 3` → **encounter** (≠0 = encontro possível)
  - `(data>>>0xB) & 3` → location
  - `(data>>>0xF) & 3` → surfaceType (tipo de piso — som/passo)
  - `(data>>>0x11/0x16/0x1B) & 0x1F` → **light[3]** (luz por vértice, 5 bits, <31 = nontrivial)

**Correção ao meu erro do znkd08**: os anéis s16framed que desloquei são trigger zones
(consumidos por `IterateAndRenderTriggerZones`), não encontros. Encontro = bits das tris
do heightmap. Teste futuro de encontro deve editar `data & 0x180` (bits 7–8).

### parseLevelGeometry (+0x14) — a cena renderizável

Magic `0x65432100`, `sectionCount @+0x0C`, seções desde +0x40, cada uma com
`{type u32, length u32}` + header de 0x40:

| MapSectionType | Payload |
|----------------|---------|
| LEVEL_PART | `{flags u16, isSkybox u16==1, layer u16, euler f32×3@+0x10, pos f32×3@+0x20, eulerOrder u16@+0x30 (0|5), effectCount u32@+0x34 ≤4, effectIndices u16[]@+0x38}` — parte nomeada da cena com transform + layer + flag skybox |
| MODEL | `parseLevelModel` — header `{flags, isTranslucent u16@+4==1, float_08, modelQWC u32@+0xC, center/bboxMin/bboxMax f32×3, radius@+0x3C}` + VIF packets (`modelQWC*0x10` bytes) |
| LIGHTING | `clearColor RGBA bytes`, `fog {rgb@+0x0C, opacity f32@+0x10, near@+0x14, far@+0x18}`, envMap dir `azimuthal@+0x1C, polar@+0x20` (graus) |
| EFFECT / COMBINED_EFFECT | efeitos por parte (append ou indexado; combined usa shift 5, normal 7) |
| ANIMATED_TEXTURE | upload de frames: índice, qwc, paletteOffset/Size/Index, giftag DIRECT + comandos GS `BITBLTBUF/TRXPOS/TRXREG/TRXDIR` + imagem. Liga efeito TEXTURE à textura |

### parseLevelModel — VIF/GIF interpreter

- Vertex-run header: `triCount u32@+0` (vertexCount=3×triCount), offsets de buffer
  `color@+4, texCoord=color+vc, pos=texCoord+vc, extra=pos+vc`, `effect u32@+0x10`.
- GIF inline: até 4 registradores GS escritos por run (`TEX0_2`, `CLAMP_2`, `ALPHA_2`)
  → textura resolvida por match `tex0+clamp` em `decodeTexture` (GS memory).
- GIFtag de vértices: `vtxCycles = 0x8000|triCount`, `vtxPrim` mask `0xFF87C000`
  (aa/fog/alpha/tex variam), base `0x9105C000` = 9 regs PACKED ctxt2 STQ shaded-tri.
  `gsConfig.prim = (vtxPrim>>>15)&0x7FF` guarda fog/alpha flags.
- VIF commands: `imm u16, qwc u8, cmd u8&0x7F`; `atITOP = imm&0x8000`,
  `signExtend = !(imm&0x4000)` — interpreter loop sobre packets.

### Texturas — emulação de VRAM GS (GS.ts)

- `GSMemoryMap` = 4MB VRAM com funções de endereçamento swizzled por formato
  (`getBlockIdPSMCT32/16/T8/T4`, `getPixelAddress*`).
- Uploads: `gsMemoryMapUploadImage*` por formato (inclui PSMT8H/4HH half-shift).
- `decodeTexture(gsMap, textures, tex0, clamp)`: lê pixels do endereço `tex0.tbp0`
  com dimensões de `tex0`, aplica CLUT (`cbp`, `csa`) e `cropTexture` por CLAMP.
- Palettes: `paletteAddress(paletteType, index)`; CLUT modes PSMCT32/16.

## render.ts — o pipeline

### FFXProgram (shader do nível)

- `ub_SceneParams`: `u_Projection, u_LightDirection Mat3x4, u_LightColor Mat3x4,
  u_FogColor, u_ScreenSize, u_FogStrength, u_RenderHacks`
- `ub_ModelParams`: `u_BoneMatrix Mat3x4, u_EnvMapMatrix Mat3x4, u_TextureMatrix Mat2x4, u_Params`
- Defines: `FOG` (prim bit 0x20), `EFFECT` (effectType), `TEXTURE`, `SKYBOX`(?), `ENV_MAP`.
- Atributos: a_Position=0, a_Color=1, a_TexCoord=2, a_Extra=3.
- Shaders dedicados: `FFXActorProgram` (atores, skinning u_BoneMatrix),
  `FlipbookProgram`/`TrailProgram`, `ParticleProgram`, `ShatterProgram`, `WaterProgram`.

### Ordenação — RenderLayer (igual ao jogo)

```
OPA_SKYBOX → OPA → OPA_LIGHTING → SHADOW → ACTOR → XLU_SKYBOX → XLU →
XLU_LIGHTING → LATE_SHADOW → LATE_ACTOR → OPA_PARTICLES → PARTICLES
```

- DrawCall de nível: `isSkybox → *_SKYBOX`, `flags&0x10 → *_LIGHTING`, senão
  `OPA/XLU` + depthSort. Sort key = layer.
- GS→megaState: `depthCompare = translate(ztst)` (reversed-depth),
  `depthWrite = gsConfiguration.depthWrite`,
  **cull Front para nível / Back para atores** (winding PS2 invertido).
- Blends GS (`alpha_data0`): `0x44` = SrcAlpha/1-SrcAlpha, `0x48` = SrcAlpha/One,
  `0` = sem blend. Prim bit 0x40 habilita alpha.
- `textureMatrix` animada por efeito TEXTURE (keyframes → `findTextureIndex(frame)`)
  = UV scroll/water/placas animadas.
- `u_RenderHacks` bitfield para correções por cena.

## script.ts — a VM que move a cena

- `Thread`/`Worker`/`Signal`/`SignalStatus`: execução de opcodes ATEL (signal/cleanup
  por `shouldCleanup`, `compareSignals` pra prioridade).
- `EventScript` (~3,5k linhas): interpretador completo — spawns, diálogo, câmeras,
  efeitos, warps. `charLabel(id)` nomeia personagens; `isTrial` marca Cloister.
- `MotionState`/`RotationState`/`angleStep`: movimento de atores com turning rate.
- **Heightmap queries** (a ponte gameplay): `fillMapTri`, `triEdgeCrossedFlags`
  (arestas s16 = paredes), `triHeight` (Y do chão sob o ator) — o movimento colide
  com os bits de passability/aresta do HeightMap.
- `LevelObjectHolder`: registry dos objetos da cena (parts, effects, texturas,
  atores) — onde scripts tocam/desligam efeitos (`activateEffect/deactivateEffect`).
- `truncateValue/wrapValue` por `BIN.DataFormat` — escrita de campos de dados.

## particle.ts / magic.ts / actor.ts — efeitos

- `parseParticleData` (slot +0x38): programas PPP — emitter specs, instruções
  (`PointChain` etc.), geometria (parseGeometry com blur), `EmitterState`,
  `EMITTER_DONE_TIMER=-0x1000`.
- `parseFlipbook`: sprites animados decodificados de uploads GS.
- `TrailArgs`/`WaterArgs`/`ShatterProgram`: trails, superfície d'água, shatter.
- `magic.ts`: `sniffMagic` detecta layout por ID; `MagicSceneRenderer` = cena de
  efeito de magia/overdrive (mesma engine).
- `actor.ts`: `ActorFlags`, `FloorMode` (chão/seguindo/altura fixa), sombra baked,
  `MonsterParticle`, comandos de magia do ator.

## O que isso responde para o phyre-lab

1. **"Ver a cena como no jogo"** = ler `mapout.vpa` (+ texturas/ator/partículas do
   mesmo mapa) — NÃO o `.dae.phyre`. O `.dae.phyre` é a conversão D3D11 dos mesmos
   dados (prova P16: vértices afetam visual; P27: transforms Phyre são inertes).
   O dado de gameplay (colisão/encontro/zonas/spawn) mora só no MAP1.
2. **Fidelidade visual** vem de: ordenação de layers, cull Front, blends GS,
   fog/LIGHTING, textureMatrix animada, env-map, texturas da VRAM GS — tudo portável.
3. **Edição de mapa** = escrever de volta no MAP1: HeightMap tris (colisão/encontro/
   superfície/luz), LEVEL_PART (posição/layer/skybox), MODEL VIF (geometria),
   LIGHTING (fog/clear/env), ANIMATED_TEXTURE, seções de partículas.
4. **Criação de mapa** = emitir um MAP1 válido novo (slot table +0x80, seções de
   geometria, heightmap com adjacência, dispatch/meta, YNDT) — a lei de container já
   validada (header slot table, dispatch {key,tag,blobOff}, record size = 2*tag).
5. **Atores/PC** = parseActorGeometry (formato próprio, paletas + GS) — caminho
   separado do `.chr`/`.dae.phyre` dos monstros PC.

## Ponte de integração (decisão pendente)

- `dist-ffxstudio/` já é um bundle estático funcional (index.html + wasm basis) que
  resolve assets sob `/data/` → pode ser embedado como viewport no editor Avalonia
  (WebView) para "ver cena ingame" imediato.
- Para edição: portar os parsers MAP1/HeightMap/LevelGeometry/GS para C# (mesma
  disciplina do meshedit) e manter writers com preservação de bytes desconhecidos.
- Runtime PC: validar quais seções o consumidor PC realmente lê (heightmap/zonas via
  `IterateAndRenderTriggerZones`/ResolveEncounterZoneIndices; geometria visual vem do
  `.dae.phyre` paralelo — editar MAP1 muda gameplay, editar `.dae.phyre` muda visual).

## Fonte

`research_tools/Noclip/noclip_reference/{bin,render,script,GS,particle,magic,actor}.ts`
— snapshot do módulo Fahrenheit, proveniência em `ExternalLibs/NoclipViewer/PROVENANCE.md`.
