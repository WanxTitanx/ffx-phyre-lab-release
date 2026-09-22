# Recibos pequenos, artifacts locais

Cada task concluída usa JSON com este formato conceitual (substituir dados reais):

```json
{
  "task_id": "P00",
  "result": "pass",
  "summary": "Resultado observado e critérios cobertos",
  "code_revision": "commit ou digest da árvore testada",
  "environment": {"platform": "linux", "details": "versões e backend"},
  "inputs": [{"path": ".lab/corpus/input", "sha256": "64-hex-reais"}],
  "checks": [{"command": "comando exato", "exit_code": 0, "expected_exit_code": 0}],
  "artifacts": [{"path": ".lab/runs/run-id/result.json", "sha256": "64-hex-reais"}],
  "limitations": ["O que este resultado não demonstra"]
}
```

`inputs` e `artifacts` devem conter pelo menos um arquivo real e hash conferível
na raiz do lab. Symlinks/traversal são rejeitados. Cada check precisa de exit code
igual ao esperado; testar falha prevista é legítimo. Código/ambiente devem ser
identificados. Os hashes são recalculados ao completar a task.

Não incluir tokens, SDK, dumps, conteúdo de assets ou logs brutos. Referenciar
artifacts ignorados. Um hash sem arquivo acessível documenta história, mas não
permite ao helper aprovar nova conclusão. Mudar entrada exige reabrir task e
dependentes. O helper não impede uma pessoa de mentir no JSON: revisão de conteúdo
e observação real continuam necessárias.
