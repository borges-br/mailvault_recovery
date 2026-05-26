# MailVault Recovery

**MailVault Recovery** é uma ferramenta profissional local e offline desenvolvida em **.NET 10 LTS** para técnicos de TI, MSPs (Managed Service Providers) e especialistas em recuperação forense corporativa. O objetivo é permitir a inspeção, leitura, recuperação e exportação seguras de arquivos de dados do Microsoft Outlook (`.ost` e `.pst`).

---

## Visão do Produto

Em ambientes corporativos, é comum que perfis de usuários do Windows Active Directory sejam corrompidos ou recriados, deixando arquivos de cache local `.ost` órfãos. Quando as caixas de correio originais não residem mais no servidor de e-mail local ou em nuvem, o cache offline `.ost` torna-se a única cópia disponível dos dados de negócios da organização.

O **MailVault Recovery** se diferencia no mercado por não ser apenas mais um "conversor obscuro". A ferramenta baseia-se em princípios forenses e de engenharia de software de ponta:
- **Operação 100% Local e Offline**: Garantia de confidencialidade de dados críticos corporativos (em conformidade com a LGPD e GDPR). Sem processamento ou uploads em nuvem.
- **Cadeia de Custódia e Integridade**: Preserva os arquivos de dados originais intocados, tratando-os como evidências read-only e documentando cada ação técnica por meio de trilhas de auditoria criptográficas e manifestos digitais.
- **Arquitetura Limpa (Clean Architecture)**: Camadas de regras de negócios puras e livres de acoplamento com bibliotecas de terceiros através do uso extensivo de adapters.

---

## Escopo do MVP

### O que está no MVP (Funcional ou Estruturado):
- [x] **CLI Forense**: Comando integrado `mailvault inspect <file>`.
- [x] **Cálculo de Hash SHA-256 por Streaming**: Leitura eficiente de arquivos maciços em blocos sem saturação de memória RAM.
- [x] **Trilha de Auditoria e Manifesto**: Geração automática de `manifest.json` e `audit.log` isolados em pastas de caso estruturadas (`./mailvault-cases/CASE-YYYY-MM-DD-HHMMSS/`).
- [x] **Segurança e Isolamento**: Garantia arquitetural de que o domínio principal não acopla engines proprietárias ou binárias.
- [ ] **Adapters de Leitura**: Integração com APIs como XstReader e wrappers libpff (milestones subsequentes).
- [ ] **Recuperação de Pastas e E-mails**: Listagem hierárquica e contagens de mensagens.
- [ ] **Exportação Corporativa**: Geração de saídas nos formatos abertos e amplamente aceitos EML e MBOX.
- [ ] **Extração de Anexos**: Recuperação técnica de arquivos anexados às mensagens.

### O que NÃO está no MVP (Fora do Escopo):
- Escrita ou gravação reversa de arquivos `.pst` proprietários.
- Upload para armazenamento ou processamento em nuvem.
- Integração direta com APIs como Microsoft 365 Graph ou Exchange Online.
- Recuperação física por carving direto de setores em discos formatados.
- Descriptografia de e-mails em formato S/MIME sem chaves criptográficas legítimas.

---

## Regras de Segurança e Privacidade

Para garantir conformidade legal corporativa (ex: LGPD):
1. **Preservação de Evidência**: O arquivo `.ost` ou `.pst` de origem deve ser aberto exclusivamente em modo leitura (`FileAccess.Read` / `FileShare.Read`). Nenhuma modificação é permitida.
2. **Minimização de Dados**: Nenhum dado confidencial corporativo ou e-mail de cliente é logado nas saídas normais de log ou enviado fora da máquina.
3. **Bloqueio de Controle de Versão**: Nenhuma mídia de e-mail real (`.ost`, `.pst`, `.msg`, `.eml`, `.mbox`) ou dumps de testes reais devem ser commitados no Git. Isso é mantido de forma rígida através de regras restritivas do `.gitignore`.

---

## Como Compilar e Executar

### Pré-requisitos
- **SDK do .NET 10 LTS** instalado.

### Compilação do Ecossistema
```bash
dotnet build
```

### Executar Testes Automatizados
```bash
dotnet test
```

### Inspecionar um Arquivo
```bash
dotnet run --project src/MailVault.Cli/MailVault.Cli.csproj -- inspect <caminho_do_arquivo>
```
*Dica:* Use a opção `--out <diretorio>` para redirecionar a saída das pastas de caso geradas.
