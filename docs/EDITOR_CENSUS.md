# Censo do editor — o que falta pro "editor completo"

Atualizado após a onda datum+estrutural (selftest 88/88, commits até
`b2a48f2`). Escopo: cobertura de **edição** sobre os bins FFX, não só
inspeção/render. Ordenado por valor × custo.

## Coberto (edição persistente ou overlay servido)

| Família | Superfície | Via |
|---|---|---|
| Cena MAP1 | walkmesh flags+vértices, lighting, LEVEL_PART TRS/hide/**dup**, textura PNG↔GS, CLUT mapa | overlay bin |
| PPP (mapa) | emissor 0x50 TRS/params/**dup**/desativar; datum emit (count/period/mask/pattern/program); datum cor (glare/ftrail RGBA); **datum genérico 40+ kinds** | overlay bin |
| PPP (magia 11/) | idem, via funcMap remap; FX live (size/speed/tint/emit/life) | overlay bin + JSON |
| Encontro 0e/ | deltas pos/heading/scale, add monster, remove, **swap monster**, party/other | edits/<enc>.json |
| Field EV01 0c/ | mapPoint deltas (drag), **modelList actor swap** | field/<ev>.json |
| Ator eri | textura PNG export/import, CLUT 256 cores | overlay bin |
| Kernel | command/item/monster1-3/monmagic*/important/panel/sphere/ply_rom/ply_save/a_ability/w_name — campos+rename | edits JSON + bin |
| Textos jogo | todos `*_txt.bin` (HelpText/NameHelpText) | edits JSON + bin |
| Journal/undo | bin+json com history+hashes, writes atômicos | .lab/editlog |

## Falta — por prioridade estimada

1. **Edição de MODEL (geometria)** — mover/deletar vértices ou tris nos
   vertex runs V4 do stream VIF. Editor de mapa "de verdade" exige ao
   menos mover vértices; delete/insert é equivalente a re-empacotar o
   run (triCount+bases) — médio/alto.
2. **Edição de animação erl** — curvas delta-encoded; editar keyframe
   exige re-encode do stream de opcodes + shift de offsets do pacote.
   Alto custo; playback/inspeção já existem.
3. **Add estrutural em magic bins** — DuplicateEmitter funciona só em
   MAP1 (header fixup próprio do magic bin: tabelas dataStart
   absolutas). Emitters synth são VM-side — cobertura parcial já via
   datum.
4. **Remoção física** (shift-delete) — hoje tudo é "hide reversível"
   (type=0xFFFFFFFF, behavior=-1, remove-list). Remoção de verdade =
   mesma máquina de inserção ao contrário — médio, baixo valor vs hide.
5. **ATEL script** — inspeção do bytecode de eventos (disasm) — útil
   pra entender cutscenes; edição ainda mais fundo.
6. **Encountier bins completos** — criar encontro novo do zero
   (schema já cobre add/remove; criação exige bin template).
7. **Kernel fields estruturais** — adicionar registros a tabelas
   (count bump + stride fixo + pool) — mesma máquina de append.
8. **Camera/light de cena** — LIGHTING edita as 3 luzes; câmeras
   fixas de field (se existirem como seção) não mapeadas.
9. **ply_save completo** — só campos iniciais descritos; resto do
   struct save (inventário/sphere-grid progresso) indocumentado —
   mas saves pessoais estão fora de escopo por política.
10. **ChildPos/ChildDir/ChildDelta vec-ref edit nos emits** — offsets
    de vec-map; writable i32 mas semanticamente frágil (remapa slots
    compartilhados).

## Fora de escopo (política)

- Runtime/injection, RT2, deploy DLL — desligado até editor completo.
- Instalação/saves originais — somente leitura.
