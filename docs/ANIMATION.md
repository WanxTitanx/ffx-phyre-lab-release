# Rotas reais de animação — FFX HD PC (P18)

Status: consolidado com corpus + EXE hashado + runtime loader trace.
Escopo: onde a animação esquelética/de corpo realmente mora, e o que isso
significa para o laboratório.

## Veredito central

**A animação de corpo do FFX NÃO passa por `PAnimationChannel` nem por
qualquer objeto Phyre serializado.** As classes `PAnimation*` estão linkadas
no `FFX.exe` (RTTI: `PAnimationController`, `PAnimationChannelTargetBind*`,
`PAnimationHierarchyNode`, blenders, `PTimeController`), mas **nenhum dos 145
clusters `.phyre` do corpus serializa uma delas** — o único "Animation"
encontrado é `PSpriteAnimationInfo`/`PSpriteAnimationInfoChar` em
`textureanimationchar.ags.phyre` (animação de UV/sprite, não esquelética).

A animação real é o pipeline **PS2-era da Square**, reimplementado nativamente
em x86 no port HD (não é emulação VU0):

```
.mgrp (motion-group) ── MSEQ records ── seqProg bytecode VM
      │                                     │ PLAY/GATE/WAIT/…
      ▼                                     ▼
clip entries → a2 blob → 2-bit mode stream → value region
                        (9 canais/osso)      (const s16 | keyed delta-RLE)
                                                  │
                                                  ▼
                          samples s16 → dequant → TRS por osso → pose
                                                  │
.chr (FFXMAP) ── SKL skeleton + remap canal→bone ─┘
                                                  ▼
                          skin palette → malha .dae.phyre renderizada
```

## Rota de carregamento (provada no runtime)

`OUTPUT.TXT` (`[FFX_section_data_win32]`) do boot na lane Proton mostra, por
personagem jogável (c001…c007, c101, c102) e weapons (w001/w011/w041):

| Ordem | Arquivo | Papel |
|---|---|---|
| 1 | `chr/pc/cNNN/cNNN.ah` + `cNNN.ahwin32` | manifesto de assets gerado (lista `mdl/D3D11/*.dae.phyre`, `tex/D3D11/*.dds.phyre`, shaders `.fx#hash.phyre`) |
| 2 | `cNNN.cdf` | definição do char (não mapeada) |
| 3 | `mdl/D3D11/cNNN.dae.phyre` | malha/skin D3D11 (nosso formato alvo) |
| 4 | `mdl/D3D11/textureAnimationChar.ags.phyre` + `tex/D3D11/cNNN_anim_nN_pM.dds.phyre` | animação de textura (flipbook UV) |
| 5 | `ffx_ps2/.../chr/pc/cNNN/mdl/cNNN.chr` | **container FFXMAP**: esqueleto + bind |
| 6 | `ffx_ps2/.../chr/pc/cNNN/mot/resident0.mgrp` | banco residente de motion |

Mais `event/obj/lo/loopdemo/loopdemo00.mgrp` (×3 requests) — motion de objeto
de evento para o loop demo do título.

## `.mgrp` — container de motion group

Layout consolidado (codec 100% reversado na lane do editor, sonda
2026-09-19, replicado aqui por `tools/mgrp_check.py` em 3.157 arquivos):

```
header 20B:  +0 flag(0)  +4 motionCount  +8 reserved(0)  +0x0C dataLen
             SIZE LAW: fileSize == dataLen + motionCount*20
             (stub universal = 16B `00×12 + 10 00 00 00`, sha 083f5dce…)
record ×20B (no FIM, @dataLen):
             +0 f0 (anchor reloc; em event/obj pode ser sujeira float)
             +4 subid = (catNibble<<12)|id  (m002→0x1002, f011→0x500B,
                                             regmot→1..8 = groupKeys)
             +8  countA (seqProgs)   +10 countB (clips)
             +12 offA → tableA       +16 offB → tableB
             dummy: {subid=0,a=0,b=0,offA=offB=0x10} → pular
tableA seqProg entry 16B: {clipId, key(=subid), flags, ?, lblOffTbl, codeBase}
  bytecode ISA:  op0 END(1B) | op1 PLAY(9B: clipTblIdx,repeat,segIdx,mode)
                 op2 GATE(1B) | op3 WAIT(3B) | op4 GOTO(3B,unused)
                 op5 ENDREL(1B,unused) | op6 WAITCNT(3B)
tableB clip entry 16B:  {f0=0, tag bitmask, ptrA→segFrameTbl, ptrB→a2 blob}
a2 clip blob:  +0 frameCount  +2 targetCount (canais=9×targets)
               +4 frameRate=7680 (30fps em fixed .8; outlier único w001=0)
               +6 extraCount (event region 8B/entry — semântica ABERTA)
               +8 modeOff +12 keyedOff +16 extraOff (relativos ao blob)
               dois emissores: completo modeOff=0x18 | compacto modeOff=0x10
mode stream:   2 bits/canal LSB-first, target-major; comps rotXYZ,trXYZ,scXYZ
               0=const 0 | 1=const 1 | 2=const s16 (value region) | 3=keyed
value region:  consts s16 (/4096) intercalados com blocos keyed {u16 len | RLE}
keyed RLE:     c<0x80 → delta 7-bit | c&0xC0 → delta 14-bit (2B)
               | c&0x80 (sem 0x40) → run: segura delta por (c&0x3F) frames
               sample = (sample+delta)&0xFFFF por frame
dequant:       rot = unit·2π rad (wrap ±π) | trans = unit·instScale·4096
               | scale = unit;  unit = s16/4096
```

Runtime (FFX.exe base 0x400000, nomes da IDB canônica da lane do editor):
`FFX_Mgrp_RelocateMseqRecordPointers@0x837040`, `…BindMseqToActiveInstance
@0x837B40`, `FFX_Mseq_InitChannelTracks@0x839A00`, `…AdvanceKeyedChannelCursors
@0x839550`, `…EvaluateKeyedChannelsAtFrame@0x839440`,
`…WriteSampledTransformChannels@0x838300`, `S16ToUnitFloat@0x839E50`,
VM `StepScriptVM@0x837980`, registry `g_FFX_Mgrp_MgrpRegistry@0x1301198`
(50×12B, kind {0=resident chr, 1=event obj, 2=battle regmot}), pool 100×20B.
Strings de loader: `chReadSystemMGRP`, `SG:Add MGRP`, `SG:AddMseq`,
`[%s:resident%d.mgrp]`, `nb mgrp err`, `nb mseq err`.

## Banks por família (censo da árvore extraída, 3.157 arquivos)

| Família | Path | n | Conteúdo |
|---|---|---|---:|
| resident chr | `chr/{mon,pc,npc,obj,sum,skl,wep}/<id>/mot/resident{0..3}.mgrp` | ~2.6k | maioria stubs 16B; reais = motion por rig (subid do char) |
| event obj | `event/obj/<mapa>/<obj>/<nn>.mgrp` | 532 | motions de cena; os maiores arquivos (até 3,7 MB) |
| battle | `battle/mot/regmot.mgrp` | 1 | banco compartilhado: 8 records, subid=groupKey 1..8 |

Medido por `tools/mgrp_check.py` (exit 0): **960 ok + 2.197 stub16 + 1 null
clip tolerado** (`chr/wep/w001/mot/resident1.mgrp`, placeholder rate=0) —
**0 anomalias**. 4.332 records (249 dummies), 25.755 seqProgs, 24.032 clips,
**3.164.362 keyed streams decodificados sem erro**. Distribuições replicam a
sonda: tag bitmask {2:83%, 6:14%, …}, modeOff {0x18:19.781, 0x10:4.251},
targetCount top {118, 68, 134, 142, 156, 162}.

**Compartilhamento por família:** 78 grupos cross-name de sha idêntico —
a vanilla já compartilha blobs de anim entre monstros relacionados.
Atenção à **poluição de install**: a árvore PC-Steam extraída tem 174 stubs
→anims reescritos por um batch do próprio projeto (2026-08-01 13:44:55) + 1
extra (m274 ← clone de m002). Fonte canônica para `.mgrp`/`.bin` é PS3/PS4/
Repack — a árvore Steam é workspace de mods vivo nessas famílias
(`FFX_PARITY_MGRP_BIN_2026-09-15`).

## `.chr` — container FFXMAP (modelo PS2-era)

Não é um blob de mesh: header com tabela de até 11 seções `{offset,count}`,
reloc por anchor=0. Seções mapeadas (sonda `.chr`, 865/865):

- sec[0] **SKL**: esqueleto — `+0x1C` boneTable (offset relativo ao bloco),
  entry 20B = `{parentIdx, rotXYZ(π·x/18000), transXYZ(/1000), scaleXYZ(/4096)}`
- sec[1] **SG**: diretório de mesh (versões 0x1126/0x1029/0)
- sec[4] offset-array; sec[5]/[6] descritores packed por-bone/material
- sec[9] **params** (tamanho nominal 5668/5665/…); sec[10] cauda = pool de vértices
- `skl+0x10`→dir de partes de mesh; `skl+0x14`→dir de skin (tripletos
  {n, 0x3C, off}, stride 60B = pesos bone→vértices)

No PC a malha vem do `.dae.phyre`; o `.chr` fornece esqueleto/bind/params —
é a ponte entre os canais MSEQ e os bones do modelo HD (remap canal→bone
produzido no load do SKL, `inst+470`).

## Correções recentes reconciliadas (sonda 2026-09-19)

- SIZE LAW real: `fileSize == dataLen + motionCount*20` (não `0x14+dataLen`).
- `0x1C` não é "magic 0x001e": é `frameRate=7680` LE do primeiro a2.
- `0x77777777` é prelude/sentinela do clip[0], não header.
- Record dummy `{subid=0,a=0,b=0,offA=offB=0x10}` existe (249 no corpus).
- Clip `tag` é **bitmask** (2,6,4,8,10,5,…), não constante.
- Dois emissores de a2 (completo 0x18 / compacto 0x10) — parser deve usar
  `modeOff`/`keyedOff` tal qual.
- Divergência residual com a sonda: nossos agregados `plays`=35.659 e
  `dummies`=249 são exatamente metade dos 71.318/498 reportados (mesma
  proporção de multi-segmento 27%); records/progs/clips/streams batem
  exatamente — provável double-count no agregador da sonda, sem impacto
  estrutural.

## P19 — decodificação e comparação de movimento

Pipeline provado em `tools/mgrp_decode.py` + `tools/chr_check.py` +
`tools/pose_bake.py`, cross-validado contra o decoder C# de referência
(`Ps2MgrpAnimationReader.cs`, mesmo proprietário, compilado com csc):

- **Tracks**: 9 canais por target (rot XYZ, tr XYZ, scale XYZ), modes 2-bit
  {0=const0, 1=const1, 2=const i16, 3=keyed RLE delta}. Decode de todos os
  bancos de amostra sem erro (c001 resident1: 77 clips/2.793 frames/13.803
  streams; m002: 16 clips/557 frames; battle regmot: 11 clips/497 frames).
- **Time base**: `frameRate=7680` (fixed .8) = 30 fps × 256 em todos os clips
  válidos do corpus (24.031/24.032; o único rate=0 é o null-clip w001).
  Cursor de tempo fixed .8; LERP entre frames inteiros com wrap de ângulo.
- **Interpolação**: `target_trs_lerp` implementa o LERP adjacente + wrap
  angular documentado no codec; amostragem fracionária determinística.
- **Transform/compose**: canais do clip são **deltas sobre a bind pose**,
  comprovado empiricamente: compose absoluto colapsa o rig (figura
  degenerada); soma component-wise de Euler flaila membros com binds ±90°;
  `local = bind ∘ clip` por matrizes produz articulação coerente em strips
  multi-frame (c001 5×frames, m002, s001 — não pose única).
- **Mapa canal→bone**: identidade nos rigs testados — `targetCount` == bone
  count do `.chr` SKL em 100% dos clips (136↔136 c001, 92↔92 m002,
  164↔164 s001). Eixo de Euler XYZ confirmado pela bind pose (ZYX distorce).
- **Paridade determinística**: samples de canal keyed idênticos entre Python
  e C# — ex. m002 clip0 ch32, 62 samples bit-exact
  `0,-8,-17,…,-10,0,9,…,7,0`; contagens de clips/streams idênticas nos dois
  lados em todos os bancos.
- **Root motion**: tr channels existem em targets (m002 clip0 target3
  trY −0,82→−0,50); a semântica de "root" consumida por gameplay é caminho
  separado do skeleton — registrado, não reivindicado.
- **Eventos**: região `extraCount` (8B/entry) contada e preservada
  estruturalmente — 10.825 entradas corpus-wide; semântica (som/trigger)
  permanece aberta.
- **Holdout**: arquivos fora do set de dev decodificam limpos — c007
  resident1 (52 clips/1.984 frames/10.006 streams), s001 resident0
  (1 clip/42 frames), ptkl0000 event obj (1 clip/54 frames); m180 resident1
  é stub16 legítimo.

## P20 — edição de clip sobre o rig original

`tools/mgrp_edit.py` — edits conservadores in-place (mesmo tamanho,
footprint do blob preservado):

- **freeze**: reescreve o payload RLE de cada stream keyed para segurar o
  sample0 — primeiro token preservado (1 ou 2B conforme 7b/14b), depois
  `0x00` (delta=0) + run bytes. **O delta persiste através de run bytes**:
  segurar exige zerá-lo antes, senão o canal continua rampando.
- **const**: sobrescreve o i16 de um canal mode-2 (só mode-2; mode-3 é
  recusado).
- **timing**: `frameCount` menor encolhe o clip; maior extrapola o delta
  persistente (documentado, não "hold").
- **synth**: clip sintético all-const dentro do footprint do blob
  (header 0x18, modes=2, i16 consts); recusa oversized.

**Prova de runtime**: `loopdemo00.mgrp` com os 2.381 keyed streams
congelados foi servido via mods dir e executado no FFX real — a intro
inteira travou num único shot por 72+s com **0 pixels de diferença**
(bit-idêntico entre t+75s e t+111s): o banco dirige a progressão da
cutscene, não apenas a pose. Baseline (arquivo do usuário restaurado)
anima normalmente (199.108 px/8s na banda do título). Edição de timing
(fc 800→200) + 49 rot-consts a 90° também executadas sem crash.

## P22 — pesos, rest pose e retarget

- **Rest pose**: `tools/chr_edit.py` edita TRS do boneTable SKL (20B/bone:
  i16 parent | rot rad·18000/π | tr·1000 | sc·4096). `--out` preserva o
  input; byte-diff locality provada (só os bytes do campo editado mudam).
  bone5.rot +90° X produz bind pose visivelmente diferente no compositor.
- **Pesos**: `phyre-meshedit` ganhou `joints` (SkinIndices u8/slot —
  **índices de paleta do PSkin**, não joints glTF: set=7 → joint 24 no
  export). `weights` renormaliza o tuplo a sum=1. Edição em verts blended
  do m001 (seg0 336:400) re-exporta com joint 19→24 e pesos normalizados.
- **Retarget**: `pose_bake render` aplica clip de um rig a outro quando
  `targets == bones` e esqueleto é idêntico (m001–m005: 92 bones,
  assinatura `2565e26f`). Guarda recusa `targets > bones` (c001 136-target
  em rig de 92 → exit 2). Movimento multi-frame verificado, não só pose.
- **Limites**: registros de skin de 60B no `.chr` (skin dir) seguem sem
  semântica provada — edição de pesos é no `.dae.phyre`. glTF m001 expõe
  66 joints vs 92 bones `.chr` (subset/remap). Attachments não exercitados.
  Prova desta task é offline (composição + re-export); aceitação em
  runtime desta edição específica pendente.

## P23 — limites do runtime para alteração de esqueleto

Sonda de runtime com 4 sequências (deploy → FFX → captura → rollback),
todas servidas via mods dir e requisitadas pelo loader:

| Edição | Arquivo | Resultado |
|---|---|---|
| SKL bone0 scale=2.0 | `c001.chr` | requisitado (OUTPUT.TXT:877); render idêntico |
| PMatrix4 local-bind joint40 ×3 | `c001.dae.phyre` | requisitado (OUTPUT.TXT:447); render idêntico |
| PMatrix4 local-bind joint1 ×2 | `c001.dae.phyre` | render idêntico |
| PMatrix4[172] (IBM candidato) ×2 | `c001.dae.phyre` | render idêntico |
| **79 local-bind = NaN** | `c001.dae.phyre` | **carrega sem crash, render perfeito** |

**Conclusão**: `.chr` SKL e `PMatrix4` (local-bind e candidato IBM) **não são
consumidos nem validados pelo render skinned do PC** — na cutscene, a pose
é 100% dirigida pelos canais `.mgrp`. Alteração visível de joints hoje só
via edição de canais `.mgrp` (P20). `phyre-meshedit jointmat` escreve
local-bind (`--joint`) ou elemento absoluto (`--abs`) para futuras sondas.

Caveat: conclusão limitada à cutscene `loopdemo00` — contexto idle/gameplay
(onde bind pose poderia transparecer) não exercitado; attachments (`.cdf`)
não exercitados.

## Implicações para o laboratório

1. **Autoria de animação mira `.mgrp`**, não phyre — reader/baker offline é
   direto a partir do decode (encoder ainda não escrito em nenhuma lane).
2. `PAnimationChannel` continua relevante só para clusters que o serializem
   (nenhum no corpus FFX; UI/outros jogos podem usar — não assumir).
3. Para posar monstros offline precisamos de `.mgrp` + `.chr` (esqueleto +
   remap canal→bone) + `.dae.phyre` (skin weights já exportáveis).
4. Pendências honestas: event region 8B (som/trigger) semântica aberta;
   operando `mode` do PLAY (−1..10) só tem o bit de interpolação provado;
   `.cdf` não mapeado; animação de textura (`*_anim_nN_pM.dds.phyre` +
   `textureAnimationChar.ags.phyre`) é rota separada a mapear.

## Fontes

- `tools/mgrp_check.py` — checker estrutural (port com crédito de
  `research_tools/Ps2/mgrp_census.py` + `Ps2MgrpAnimationReader.cs`,
  lane ffx-editor do mesmo autor).
- `docs/reverse/FFX_MGRP_MSEQ_KEYFRAME_CODEC_PROVEN_2026-06-06.md`,
  `FFX_MGRP_FORMAT_VALIDATED_2026-08-01.md`,
  `FFX_MODELS_UNLOCK_MGRP_SONDA_2026-09-19.md`,
  `FFX_PARITY_MGRP_BIN_2026-09-15.md`, `FFX_CHR_SONDA_2026-09-19.md`
  (worktree `atel-codec-20260920`, mesmo proprietário).
- `FFX.exe` sha256 `78ce3439…` (strings + endereços IDB citados acima).
- Loader trace `OUTPUT.TXT` do boot (lane Proton, 2026-09-21).
