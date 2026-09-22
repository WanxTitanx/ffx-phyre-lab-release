# Gates, evidência e contratos

Cada gate é scoped por entradas, perfil, plataforma e commit. Uma lista marcada
não substitui comandos e artefatos. Recibos seguem [evidence](../evidence/README.md).

| Gate | Condição de aprovação | Não basta |
| --- | --- | --- |
| G0 Ambiente | Privado/Actions off; custo confirmado; identidade, espaço, lane e proveniência registradas | Uma configuração antiga |
| G1 SDK/render | Build reproduzível; cena real; captura identificada; frames e backend registrados | Janela vazia, mock ou só compilar |
| G2 Leitura | Corpus pinado, hierarquia/texturas/rig coerentes e negativos rejeitados | Um asset favorecido |
| G3 No-op | Writer respeita contrato, preserva dados desconhecidos e jogo carrega o resultado | Exportar glTF |
| G4 Mudança mínima | Uma mudança observada no FFX; original preservado; rollback testado | Viewer de terceiros |
| G5 Autoria | Editar, undo/redo, salvar, fechar processo e reabrir com igualdade semântica | Estado só em memória |
| G6 Mesh/material | Edição representativa aceita no jogo sem regressão de rig/material | Aparência em um ângulo |
| G7 Animação | Clip modificado no rig original; timing, loops e transições comprovados no FFX | Pose única |
| G8 Rig | Alteração de rig/retarget com weights, bounds, attachments e ações testadas | Skin no Blender |
| G9 Monstro | Substituição e adição de ID reportadas separadamente; ações completas verificadas | Novo nome em catálogo |
| G10 Cena | Placement/geometria de cena editada carregada no consumidor certo | Confundir field com battle |
| G11 Mapa jogável | Colisão, spawn, câmera, eventos e transição exercitados | Render de cenário |
| G12 Entrega | Bootstrap/replay em estado limpo, retomada, regressões, matriz e limites | “100% pronto” sem escopo |

## Recibos por task

Recibo JSON inclui `task_id`, `result`, `summary`, `code_revision`, `environment`,
`inputs`, `checks`, `artifacts`, `limitations`. O helper confere estrutura e hashes
locais, não valida por IA a verdade do resumo. Comando esperado falhar é um check
negativo com `expected_exit_code` correspondente e descrição da propriedade.

Para testes gráficos registrar aplicação/processo/run ID, backend, janela/estado,
resolução, timestamp, sequência/frames, esperado e observado. Comparação por pixel
usa tolerância motivada, câmera/tempo/seed fixos e regiões conhecidas; não aceitar
um score visual arbitrário. Inspeção humana/modelo não equivale a ground truth do
formato. Screenshots/dumps ficam locais; Git recebe índices/hash e notas pequenas.

## Writers

- Bounds de todas as regiões e multiplicações, contagens, tamanhos, alinhamentos,
  referências e ciclos. Checagem antes de alocar/escrever, com limites configurados.
- Hash do original antes/depois; arquivo de saída distinto e escrita atômica.
- Roundtrip sem edição, edição mínima, input truncado, overflow, referência inválida,
  versão não suportada e corpus de controle. Checksum quando o formato exigir.
- Preservar bytes opacos e identidade. Se a rota não conseguir, falhar ou declarar
  recompilação restrita com loss report e gate próprio; nunca omitir silenciosamente.
- Reabrir com leitor independente quando possível e depois carregar no consumidor.
  Independência não vem de dois comandos que chamam a mesma biblioteca.

## Animação e rig

Verificar units, handedness, matriz, bind/rest pose, parents, pesos normalizados,
índices de joint, limites, NaN/Inf, quaternion/rotação, time base e root motion.
Testar início/meio/fim, loop, blend e transições. MGRP/MSEQ não é intercambiável
com PAnimationChannel só por representar movimento. Eventos de gameplay e CTB
exigem seu próprio contrato e não são alterados incidentalmente pelo editor visual.

## Compatibilidade e rollback

Identificar exatamente EXE/assets/save de laboratório e loader utilizado. Evitar
modificar arquivos originais. Se cópia licenciada/loader isolado não estiver
disponível, registrar bloqueio de runtime e continuar tarefas offline. Nunca
desabilitar Steam/DRM, adulterar licença ou baixar executável do jogo para resolver
setup. Após teste, demonstrar baseline restaurada e ausência de alteração original.

## Revisão e promoção

Self-check está permitido e é obrigatório para qualidade. Não chamá-lo de revisão
independente. Se uma publicação/integração futura exigir outro revisor, registrar
gate pendente até haver autoridade para isso; não criar subagente por conta própria.
O plano não concede publicação, deploy no jogo pessoal ou integração em outro repo.
