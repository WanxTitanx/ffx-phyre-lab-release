# Formato alvo e rotas de escrita (P09)

Decisões fixadas em 2026-09-20; recibo local conforme `evidence/README.md`.

## Runtime alvo pinado

| Item | Valor |
|------|-------|
| Executável | `FFX.exe` — FINAL FANTASY X/X-2 HD Remaster (Steam, PC) |
| SHA-256 | `78ce34397da5e6f49b72c2aebadedaf4cd3f6720e1949d46a1b8ed67d3db5ced` |
| Tamanho | 10 675 712 bytes, PE32+ x86-64 |
| Timestamp PE | 2015-08-18 08:58:52 UTC |
| Build path | `R:\hg_code\ffx_w32\D3D11Final\FFX.pdb` |
| Backend | PhyreEngine D3D11 (threads `PhyreEngineRenderThread` etc.; importa `d3d11.dll`) |

## Autodescrição do cluster `.phyre`

O cluster serializado carrega o próprio namespace de classes: após o header
(`RYHP` + tag de plataforma) há uma tabela com `classCount` entradas de 36 B
(offset de nome em +8) e `memberCount` descritores de 24 B, seguidos do blob
de nomes. Parser independente (`tools/phyre-exporter/PhyreLib.cs`) lê essa
tabela sem `PhyreClassLayout.*` externo — por isso a desserialização offline
funciona apesar de o SDK não distribuir o layout D3D11.

Tags de plataforma observadas no header (offset 0xC, big-endian visual):
`11XD` = FFX PC/D3D11, `LGCP` = PC/GL do SDK 3.1.5.0.

## SDK 3.1.5.0 × serializer independente × FFX

| Aspecto | SDK PhyreClassLayout.GL | FFX `m001.dae.phyre` (namespace embutido) |
|---------|------------------------|------------------------------------------|
| Classes-leaf de dados | `PDataBlockGL`, `PMeshSegmentGL`, `PTexture2DGL`, `PCgParameterGL`, `PShaderPassGL` | `PDataBlockD3D11`, `PDataBlockBufferD3D11`, `PMeshSegmentD3D11`, `PTexture2DD3D11`, `PShaderProgramD3D11`, `PStreamInputLayoutD3D11`, `CD3D11_*_DESC` |
| Cluster header | `PClusterHeaderGL` | `PClusterHeaderD3D11` |
| Classes de topo | PNode, PMesh, PMaterial, PVertexStream comuns | mesmas + campos FFX-only |
| Extensões Square | — | `m_animCt`, `m_animID0..3`, `m_groupID`, `m_DObjKind`, `m_RotType`, `m_mimeCt`, `m_dObjFlag`, `m_objID`, `m_layerz0..3`, `m_render_order`, `m_clothmodelIndex`, `m_clothIndex` |

Incompatibilidades documentadas:

1. **Layout de plataforma**: toda a camada de dados (buffers, shaders,
   texturas, estados raster/blend/depth) é tipada por plataforma; o runtime
   GL da lane não reconhece classes `*D3D11` e o SDK não tem
   `PhyreClassLayout.D3D11` para gerar/carregar esse perfil. Carregar
   `11XD` no runtime GL da lane não é uma rota.
2. **Versão do schema**: FFX usa build `D3D11Final` (Phyre posterior ao
   3.1.5.0); mesmo classes comuns divergem em membros (ex.: `PMesh` ganha
   `m_defaultPose`, `m_matrixNames`, `m_matrixParents` no arquivo FFX).
3. **Campos de jogo**: `PNode` FFX carrega metadados de gameplay
   (anim IDs, group/mime/dObj) que não existem no schema do SDK — um writer
   precisa preservá-los opacamente.

## Rota mínima preservadora escolhida

**Escrita in-place same-size sobre blocos de dados**, mantendo byte-a-byte:
header, tabela de namespace, blocos de objeto, shared data, links
(array/object/object-array) e fixups. Como o parser conhece todos os
offsets (`DataOffset`, `ElemSize`, link tables), um patch que só altera
bytes dentro de um elemento de data block (ex.: matriz, vértice,
parâmetro de material, texel same-size) não perturba nenhuma estrutura —
o arquivo resultante permanece válido por construção e re-parseável.

Verificação exigida por patch: re-parse completo + diff binário restrito
ao intervalo editado + comparação de bounds/contagens.

**Fora da rota mínima** (exige recomputar links/fixups/header — futuro):
inserir/remover objetos, mudar contagens, realocar buffers, re-empacotar
shared data.
