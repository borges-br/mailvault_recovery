# Relatório Técnico de Conformidade — Milestone 2

Este relatório atesta a conclusão e os critérios de aceitação alcançados na **Milestone 2** do **MailVault Recovery**.

---

## 1. Escopo de Entrega e Resultados

Todos os itens especificados para esta milestone foram desenvolvidos de acordo com as restrições arquiteturais propostas:

| Requisito / Critério de Aceitação | Estado | Evidência / Abordagem Técnica |
| :--- | :---: | :--- |
| **Padrão Result Pattern & Value Objects** | `COMPLETO` | Implementação de `MessageId`, `AttachmentId` no Domain e `OperationResult<T>` no Core. |
| **Isolamento de Acoplamento do Adapter** | `COMPLETO` | A CLI (`MailVault.Cli`) e o Domain possuem 0% de dependência direta do assembly `XstReader`. |
| **Runtime Plugin Loading** | `COMPLETO` | A CLI localiza e instancia `XstReaderMailStoreReader` dinamicamente a partir do assembly DLL do build. |
| **Comando `mailvault tree`** | `COMPLETO` | Varredura recursiva da árvore de pastas impressa em texto estruturado estilizado. |
| **Comando `mailvault list`** | `COMPLETO` | Listagem tabular de e-mails em pastas com offset/limit (paginação) e impressão de alertas. |
| **Comando `mailvault preview`** | `COMPLETO` | Visualização detalhada segura de headers, anexos e corpo truncado (default 30 linhas). |
| **Mitigação NU1903 (Pkcs)** | `COMPLETO` | Atualização direta de dependência para `System.Security.Cryptography.Pkcs` (v9.0.0) confinada no adapter. |
| **Testes da CLI e Adapters** | `COMPLETO` | Testes automatizados com fakes rodando todos os comandos CLI e validando saídas, paginação, truncamento e issues. |
| **Dotnet Build & Test Verde** | `COMPLETO` | 100% de compilação verde e todos os 12 testes do projeto passando com sucesso. |

---

## 2. Testes Automatizados

A suíte de testes de integração da CLI foi criada para simular a execução e validar o console. Abaixo estão descritos os cenários testados com sucesso em `MailVault.Adapters.Tests.dll`:

1. **`ExploreTypes`**: Mapeamento e validação de assinatura de tipos.
2. **`TreeCommand_WithFakeMailStoreReader_PrintsHierarchyCorrectly`**:
   - Executa `mailvault tree` com `FakeMailStoreReader`.
   - Valida a formatação de ramificações do diretório (`├──`, `└──`).
   - Valida que as contagens de mensagens por pasta estão corretas.
   - Verifica a integridade da geração automática de `manifest.json` e `audit.log` na pasta do caso.
3. **`ListCommand_WithLimitAndOffset_PaginatesCorrectly`**:
   - Testa a paginação do comando `mailvault list`.
   - Solicita limite 2 e offset 1 (ignora msg 1, exibe mensagens 2 e 3).
   - Valida que mensagens fora do intervalo de paginação são ocultadas.
   - Garante que avisos (`ExtractionIssue`) associados a mensagens específicas são impressos no terminal sem travar a execução.
4. **`PreviewCommand_WithTruncatedBody_DisplaysSafely`**:
   - Garante conformidade de segurança e LGPD.
   - Executa `mailvault preview` com limite de 5 linhas de corpo.
   - Valida que o corpo é impresso exatamente até a 5ª linha e seguido da mensagem de alerta forense: `[... TEXTO TRUNCADO SEGURAMENTE PARA COMPLIANCE FORENSE - X LINHAS OCULTAS ...]`.
5. **`PreviewCommand_WithIssues_DisplaysIssuesWithoutCrash`**:
   - Testa exibição limpa e estruturada de issues de e-mails com propriedades MAPI de cabeçalho corrompidas no preview.

---

## 3. Segurança Forense e Gestão de Evidências

Durante as execuções de comandos de leitura, a integridade da evidência foi rigorosamente protegida:
- O arquivo original **nunca é aberto com permissão de escrita**. A biblioteca XstReader é instruída a abrir o arquivo sempre como `Read-Only` sob compartilhamento de leitura (`FileShare.Read`).
- Cada comando CLI executado gera automaticamente uma nova pasta de caso técnico em `./mailvault-cases/` (exemplo: `CASE-2026-05-26-093012`) contendo:
  1. `manifest.json`: Registra operador, data de início/término, hash SHA-256 gerado por streaming seguro e a lista de avisos detectados.
  2. `audit.log`: Uma trilha contínua de ações forenses imutáveis e carimbos de tempo, desde a abertura do store até a sua finalização ou falha.
- **Nenhum arquivo de teste real `.pst` ou `.ost` foi incluído no repositório do Git**, preservando a conformidade de conformidade e o tamanho reduzido do código fonte.
