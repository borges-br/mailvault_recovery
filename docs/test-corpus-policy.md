# Política de Corpus de Teste e Privacidade

Este documento define as diretrizes obrigatórias de segurança, conformidade forense, desidentificação e controle de privacidade para o gerenciamento de mídias de correio de teste reais no MailVault Recovery.

---

## 1. Princípios de Segurança e Privacidade

1. **Exclusão Absoluta no Controle de Versão (Git)**:
   - É terminantemente proibido incluir, comitar ou sincronizar no repositório git qualquer arquivo contendo dados reais ou mídias de evidência extraídas de ambientes corporativos ou pessoais.
   - Isso inclui arquivos `.ost`, `.pst`, `.msg`, `.eml`, `.mbox`, `.db`, `.dump`, logs brutos e relatórios de validação que contenham caminhos físicos privados do sistema local.
2. **Minimização de Dados e Higienização**:
   - Os relatórios de validação e trilhas de auditoria gerados devem operar sob o princípio de minimização de dados.
   - Corpos completos de mensagens (HTML ou texto), cabeçalhos brutos completos, dumps MAPI volumosos ou anexos reais **nunca** devem ser salvos nas ferramentas de auditoria e relatórios.
3. **Mascaramento de Caminhos Físicos e E-mails**:
   - Qualquer caminho de diretório local contendo pastas de usuário do Windows (ex: `C:\Users\username\...`) deve ser mascarado como `C:\Users\<USER>\...` antes de ser persistido em qualquer relatório técnico ou log.
   - Endereços de e-mail e nomes sensíveis devem ser higienizados ou reportados em formato de contadores ou hashes sanitizados.

---

## 2. Gestão do Laboratório Local de Validação

- O laboratório físico local reside exclusivamente sob a pasta `.local-corpus/` na raiz de trabalho, configurada ativamente no arquivo `.gitignore`.
- Mídias reais usadas para homologação de performance e compatibilidade devem ser salvas exclusivamente nesta estrutura física local.
- Scripts automáticos de validação que operem sobre o corpus local devem ser projetados para gravação de resultados no escopo local ignorado do git.
- Testes que executem no fluxo de Integração Contínua (CI) ou testes automatizados versionados devem utilizar exclusivamente dados **sintéticos** gerados dinamicamente em memória (como o `FakeMailStoreReader`), prevenindo vazamento acidental.
