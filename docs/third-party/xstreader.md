# Detalhes da Dependência — XstReader

Este documento registra as informações, licenças, justificativas e diretrizes de isolamento para a dependência externa do **XstReader**.

## Informações do Pacote

- **Nome do Pacote**: `XstReader.Api`
- **Versão Adotada**: `1.0.6`
- **Licença**: Microsoft Public License (Ms-PL) — Licença open-source permissiva compatível com comercialização e distribuição.
- **Autor/Mantenedor**: iluvadev (fork ativamente mantido do projeto original de Dijji).
- **Destino de Uso**: `MailVault.Adapters.XstReader.csproj`

## Motivo da Escolha

O parsing do formato binário estruturado do Microsoft Outlook (`.ost` e `.pst`) é altamente complexo e envolve a decodificação de MAPI, tabelas NDB, sub-nós LTP e outras estruturas de dados Microsoft [MS-PST].

A biblioteca `XstReader.Api` foi escolhida pelas seguintes razões:
1. **Nativa em C#**: Escrita inteiramente em C# e compatível com .NET Standard 2.0+, rodando perfeitamente no runtime moderno **.NET 10 LTS**.
2. **Independência de Plataforma**: Funciona offline em qualquer sistema operacional (Linux, macOS, Windows) sem requerer o Microsoft Outlook, Office ou MAPI nativo do Windows instalados.
3. **Ergonomia e Confiabilidade**: Abstrai com eficácia a leitura hierárquica e decodificação do MAPI e propriedades, permitindo acelerar as entregas do MVP sem comprometer a estabilidade do Core.

## Riscos Identificados & Mitigação

- **Vulnerabilidade Transitiva (NU1903)**:
  - *Detalhe*: O pacote depende indiretamente da versão `6.0.1` de `System.Security.Cryptography.Pkcs`, que possui uma vulnerabilidade de severidade alta reportada (GHSA-555c-2p6r-68mm).
  - *Mitigação*: Adicionar referência direta ao pacote `System.Security.Cryptography.Pkcs` versão `8.0.0` (ou superior estável disponível) diretamente no projeto `MailVault.Adapters.XstReader.csproj` para forçar o .NET a carregar a versão segura corrigida.

## Regras de Isolamento Técnico

1. **Apenas via Adapter**: O pacote `XstReader.Api` deve ser referenciado exclusivamente no projeto `MailVault.Adapters.XstReader`. É expressamente proibido adicioná-lo ou importá-lo em `Domain`, `Core`, `Audit` ou `Cli`.
2. **Nenhum Vazamento de Tipos**: Tipos do namespace `XstReader.Api` (como `XstFile`, `XstFolder`, `XstMessage`, `XstAttachment`, `XstRecipient`) nunca devem aparecer em assinaturas públicas, DTOs de comunicação ou testes fora do projeto adapter. O mapeamento para records do `MailVault.Domain` deve ser explícito e rigoroso.
3. **Mapeamento de Erros**: Falhas no parsing do XstReader devem ser convertidas em records `ExtractionIssue` do domínio para evitar crashes descontrolados da CLI.
