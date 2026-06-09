# Política de Dependências — MailVault Recovery

Este documento descreve as diretrizes mandatórias para adição e gestão de dependências externas no ecossistema do **MailVault Recovery**.

## Princípios Gerais

1. **Uso de Versões Estáveis e Suportadas**: Todas as dependências adicionadas a qualquer projeto devem estar em suas versões mais recentes, estáveis e oficialmente suportadas pelos mantenedores.
2. **Preferência por LTS**: O runtime do ecossistema e as principais bibliotecas devem, prioritariamente, rodar em versões LTS (Long Term Support) para garantir máxima estabilidade e suporte corporativo de longo prazo. Atualmente, o projeto é fundado no **.NET 10 LTS**.
3. **Evitar Pré-lançamentos no Núcleo**:
   - É terminantemente proibido utilizar pacotes em estado de Preview, Beta, Alpha, Release Candidate (RC) ou instáveis nas camadas centrais do sistema (`MailVault.Domain`, `MailVault.Core`, `MailVault.Audit`).
   - Versões instáveis/preview só são toleradas em branches experimentais dedicadas e isoladas, devendo obrigatoriamente ser promovidas para a versão estável antes de qualquer merge na branch principal.
4. **Isolamento de Acoplamento**:
   - Bibliotecas específicas de interface com o usuário (UI), infraestrutura de linha de comando ou leitores externos de PST/OST (como `System.CommandLine`, `Avalonia`, `XstReader.Api`) devem ficar estritamente contidas em seus respectivos projetos de borda (`Cli`, `Desktop`, `Adapters`).
   - Nenhuma dependência externa deve vazar para as camadas puras do domínio (`MailVault.Domain`) ou orquestração principal (`MailVault.Core`).

## Dependências homologadas

1. **`Microsoft.Data.Sqlite`**
   - **Escopo**: Restrito exclusivamente à camada de persistência (`MailVault.Indexing`).
   - **Versão**: `10.0.0` (ou superior estável homologada no .NET 10 LTS).
   - **Restrição**: Proibida a importação de tipos do SQLite ou de ADO.NET nas camadas Domain ou Core. Toda a persistência deve ser exposta via interfaces abstratas do Core.
