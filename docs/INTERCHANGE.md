# INTERCHANGE — contrato de representação intermediária (v1)

Escopo: formato de projeto/versionamento interno do lab para conteúdo Phyre/FFX.
Não é formato de runtime. Toda afirmação de compatibilidade exige evidência de
execução (ver ACCEPTANCE.md). Sem UI acoplada: consumo via CLI/JSON.

## Envelope

Todo documento de intercâmbio é um objeto JSON:

```json
{
  "schema": "phyre.project.v1",
  "source": {"path": "...", "sha256": "…", "platform": "pc-d3d11|ps3|ps2|wine", "profile": "ffx-hd|phyre-3.1.5"},
  "nodes": [], "meshes": [], "rigs": [], "animations": [], "materials": [],
  "opaque": [], "loss": {}
}
```

`schema` é obrigatório e versionado (`phyre.project.v1`). Readers falham com
diagnóstico em versão desconhecida; nunca inferem silenciosamente.

## IDs

- `id` de nó/mesh/material: `string` estável derivada do bloco de origem
  (`<class>@<offset>` ou nome do asset quando único); nunca índice de array
  como identidade persistente.
- `source.sha256` identifica o arquivo de origem; entradas modificadas levam
  `derived_from` com o hash do original.

## Unidades, eixos e matrizes

- Comprimento: **metros** (float). Rotação interna: **radianos**; quaternions
  `[x,y,z,w]`. Tempo: **segundos**.
- Convenção de coordenadas: **right-handed, Y-up, -Z forward** (glTF).
  Conversão de/para o espaço nativo FFX é um campo explícito
  (`axes.source_convention`) — valor atual `unknown-pending-verification`;
  não presumir handedness sem evidência de runtime.
- Matrizes: **column-major** `float[16]` (M[col][row]), composição
  `world = parent * local`. Qualquer matriz em outra ordem é convertida na
  fronteira e marcada no loss report.

## Malhas (meshes)

`meshes[]`: `id`, `name?`, `primitives[]` com `attributes`
(`POSITION` obrigatório; `NORMAL`, `TEXCOORD_n`, `JOINTS_0`, `WEIGHTS_0`,
`COLOR_n` quando presentes), `indices`, `material` (ref), `bounds`
(`{min:[3], max:[3]}` obrigatório — validação exige finitos).

## Rigs e animações

- `rigs[]`: `id`, `joints[]` (`id`, `parent` ref, `inverse_bind_matrix`),
  `root` ref.
- `animations[]`: `id`, `channels[]` (`target` node ref, `path`
  translation|rotation|scale, `times[]` s, `values[]`, `interpolation`).
  Canais fora de PAnimationChannel (MGRP/MSEQ/rotas legadas) entram como
  `animations[].extensions.ffx_motion` com `codec` declarado — nunca
  descartados silenciosamente.

## Materiais e texturas

`materials[]`: `id`, `shader` (ref de efeito ou nome), `parameters` (mapa
opaco chave→valor), `textures[]` (refs para `images[]` com `uri` ou
`opaque` ref). Parâmetros sem equivalente no modelo vão para `opaque`.

## Payload opaco (preservação)

Todo bloco reconhecido mas não modelado, ou desconhecido, vai para
`opaque[]`: `{node?, kind, byte_range|base64, reason}`. Reimportação deve
conseguir reembutir `opaque` sem perda — round-trip é gate, não glTF.

## Loss report

`loss` (obrigatório, pode ser vazio): `dropped[]` (kind+razão),
`approximated[]` (campo+error), `unsupported[]` (feature+evidência).
Export sem loss report explícito é inválido — "sem perdas" também é uma
afirmação que precisa de evidência.

## CLI

`tools/phyre-reader` emite `phyre.report.v1` (diagnóstico). O envelope de
projeto `phyre.project.v1` é o contrato de autoria; ambos são JSON UTF-8
determinísticos (chaves ordenadas, LF) para diff/hash estáveis.

## Round-trip DCC (P21) — glTF como vista, não autoridade

Ida/volta `.phyre`→glTF→Blender→glTF medida em `tools/rt_check.py`. O glTF
carrega identidade por `node.extras.phyre_id` (Blender preserva extras de
nó como custom props; `asset.extras` de topo NÃO sobrevive — o exporter do
Khronos reescreve o bloco `asset` inteiro). Resultado do m001 skinned:

- **Preservado**: todos os 67 nomes de nó, hierarquia sobre nós comuns,
  66 joints por nome, conjunto de posições (638 distintas), mapa de
  skinning por posição (jointName+weight), proveniência por nó.
- **Delta aditivo (não-perda)**: nó wrapper `m001_skin` adicionado;
  vértices duplicados em seams (3449→3462, posições idênticas); ordem do
  array `skin.joints` muda (índices JOINTS_0 não são comparáveis — compare
  por nome de joint); reparent de `m001_mesh`/`joint_0` sob o wrapper.
- **Perdas reais conhecidas**: `asset.extras` de topo; materiais mapeiam
  para Principled BSDT (parâmetros Phyre/custom ficam fora); canais fora
  do modelo glTF (MGRP extras/events) não viajam — ficam no envelope do
  projeto, nunca no glTF.

Ferramentas: `tools/gltf_tag.py` (injeta ids+proveniência),
`tools/blender_io.py` (import/export dentro do Blender),
`tools/rt_check.py` (verificador de identidade + loss report JSON).
