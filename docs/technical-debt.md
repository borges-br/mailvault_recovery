# Registro de Débitos Técnicos — MailVault Recovery

Este documento serve para catalogar, justificar e planejar a mitigação de débitos técnicos controlados introduzidos durante o ciclo de desenvolvimento do **MailVault Recovery**.

---

## Débito Técnico 001: Localização de `IAuditTrailWriter` no Domain

### Descrição
A interface `IAuditTrailWriter` está atualmente definida na camada `MailVault.Domain` (sob a pasta `src/MailVault.Domain/IAuditTrailWriter.cs`), em vez de uma camada de orquestração superior (como `MailVault.Core`) ou um projeto dedicado de abstrações de infraestrutura.

### Justificativa de Engenharia
Na fundação da arquitetura limpa do projeto, a camada de Auditoria (`MailVault.Audit`) possui uma dependência restritiva forte: ela pode referenciar **apenas** `MailVault.Domain`. Essa restrição impede que o projeto `MailVault.Audit` referencie `MailVault.Core` para obter a definição de `IAuditTrailWriter` (o que causaria dependências circulares, visto que o Core precisa referenciar a Auditoria ou o ecossistema precisa de abstrações comuns).

Ao manter a interface `IAuditTrailWriter` contida em `MailVault.Domain`, permitimos que:
1. `MailVault.Audit` implemente a interface de escrita de auditoria (ex: `FileAuditTrailWriter`) de forma limpa, conhecendo apenas o Domain.
2. `MailVault.Core` (que referencia Domain) faça uso polimórfico de `IAuditTrailWriter` em seus orchestrators sem violar as regras de referências estabelecidas.

Embora interfaces de infraestrutura como gravação de auditoria idealmente fiquem fora de um domínio de lógica pura, esta escolha foi adotada de forma pragmática para respeitar a estrutura estrita de diretórios sem exigir a introdução de um projeto intermediário de abstrações cruzadas (ex: `MailVault.Abstractions`).

### Estratégia de Mitigação Futura
Se no futuro houver necessidade de mover mais contratos de borda ou infraestrutura (como audit trail, logging cross-cutting, ou exporters), introduziremos um projeto comum e desacoplado chamado `MailVault.Abstractions` (ou `MailVault.Shared`). 
Tanto `Core`, `Audit` quanto `Exporters` referenciarão este projeto de abstrações, movendo `IAuditTrailWriter` para ele e liberando o `Domain` para conter estritamente records de dados e lógicas puras.

Atualmente, este débito está classificado como **baixo risco / controlado**, e sua refatoração não é recomendada no momento para priorizar as entregas funcionais da Milestone 2.
