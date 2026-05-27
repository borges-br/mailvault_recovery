# Segurança e Integridade de Dados

MailVault Recovery é uma ferramenta de recuperação técnica. Ela ajuda a organizar evidências, calcular hash, registrar manifesto, indexar metadados e exportar artefatos verificáveis, mas não substitui uma metodologia forense formal.

## Princípios operacionais

| Princípio | Orientação |
| --- | --- |
| Preservar original | Nunca processe o único arquivo original. Trabalhe sobre cópia. |
| Registrar integridade | Guarde SHA-256 calculado pelo MailVault e, quando aplicável, um hash externo independente. |
| Minimizar escrita | Escreva apenas no workspace e pasta de exportação, não na evidência. |
| Preservar logs | `manifest.json`, `audit.log`, `case.db`, reports e diagnostics fazem parte do contexto técnico. |
| Isolar dados sensíveis | Corpos de e-mail e anexos podem conter dados pessoais, segredos ou material regulado. |
| Não prometer recuperação | Arquivos corrompidos podem ser parcialmente recuperáveis ou irrecuperáveis. |

## Fluxo recomendado

1. Receber a evidência e registrar origem, responsável e contexto.
2. Criar uma cópia de trabalho em armazenamento local confiável.
3. Calcular hash da cópia antes de processar.
4. Criar o caso com MailVault Recovery.
5. Preservar `manifest.json` e `audit.log`.
6. Indexar e revisar `issues`.
7. Exportar apenas o escopo necessário.
8. Validar exportação e arquivar `validation-report.json`.
9. Guardar artefatos e logs com controle de acesso.

## Cuidados com a evidência

- Evite abrir PST/OST original no Outlook ou em ferramentas que possam modificar o arquivo.
- Prefira caminhos locais curtos e estáveis para evidências grandes.
- Evite pastas sincronizadas por nuvem durante indexação/exportação.
- Não renomeie ou mova a evidência após criar o caso se pretende exportar depois; a exportação valida o hash da origem.
- Se for necessário mover, registre a mudança e mantenha a cópia original disponível.

## Hash e cadeia de custódia

O projeto calcula SHA-256 por streaming e grava o resultado no índice/manifest. Isso ajuda a detectar troca ou alteração da evidência entre indexação e exportação.

Limites:

- O hash interno não substitui procedimento de cadeia de custódia.
- O operador ainda precisa registrar quem acessou a evidência, quando, onde e com qual finalidade.
- Relatórios técnicos do MailVault não são laudo pericial por si só.

## Privacidade e vazamento de conteúdo

O código contém cuidados para evitar corpo de e-mail em relatórios de validação e diagnósticos. Ainda assim:

- exports EML/MBOX contêm conteúdo integral das mensagens selecionadas;
- anexos extraídos podem conter dados sensíveis;
- logs de ferramentas externas podem incluir nomes de arquivos, paths ou mensagens de erro;
- `case.db` armazena metadados e previews quando cache de preview está habilitado.

Proteja workspace, exports e backups com permissões adequadas.

## Quando envolver análise forense formal

Use processo formal quando:

- o resultado será usado em disputa judicial ou regulatória;
- há exigência de cadeia de custódia auditável;
- há risco de contaminação de evidência;
- a evidência está fisicamente danificada;
- é necessário laudo técnico assinado.

MailVault Recovery pode apoiar a etapa técnica, mas a responsabilidade metodológica continua com o operador e a organização.
