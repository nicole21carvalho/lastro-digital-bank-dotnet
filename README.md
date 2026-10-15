# 🏦 Lastro · Banco digital em .NET

Núcleo de um banco digital em que **o dinheiro nunca some nem duplica**: livro-razão com partidas dobradas,
Pix com chave de idempotência e event sourcing sobre o PostgreSQL. Mil Pix simultâneos saindo da mesma conta
com saldo para cem: passam exatamente cem.

> Em construção. Pronto até agora: livro-razão, contas, depósito, Pix entre contas do Lastro, idempotência e
> testes de concorrência (fase 1 de 5). Próximas: chaves Pix e Pix para outros bancos, login com passkeys,
> observabilidade com .NET Aspire e publicação.

## Como o dinheiro anda

```
POST /pix  { origem: Ana, destino: Bia, valor: 30,50 }    Idempotency-Key: 7f3c...
  │
  ├─ trava as contas de Ana e Bia (SELECT ... FOR UPDATE), sempre na ordem do id
  ├─ a chave já foi usada?  sim → devolve a resposta guardada, sem mexer em nada
  ├─ regras do domínio: valor > 0, saldo suficiente, descrição até 140 caracteres
  └─ uma transação só:
       ValorDebitado  Ana  -30,50 ┐ mesmo lançamento,
       ValorCreditado Bia  +30,50 ┘ mesmo valor
       + saldo das duas contas e linhas do extrato (projeções)
       + a resposta, guardada sob a chave de idempotência
```

## Decisões

**Partidas dobradas: a soma de todos os saldos é sempre zero.** Todo lançamento debita uma conta e credita
outra no mesmo valor. Até um depósito tem duas metades: o dinheiro sai da conta interna de liquidação (que fica
negativa) e entra na do cliente. A rota `GET /livro-razao/conferencia` refaz a conta do livro inteiro a partir
dos eventos, e os testes a chamam depois de cada rodada de concorrência.

**Event sourcing com [Marten](https://martendb.io).** O que fica gravado são os fatos (`ContaAberta`,
`ValorCreditado`, `ValorDebitado`), imutáveis. Saldo e extrato são projeções desses fatos, atualizadas na mesma
transação (CQRS sem atraso). Como nada se perde, `GET /contas/{id}/saldo?em=2026-10-15T09:00:00-03:00` diz o
saldo em qualquer instante do passado. O Marten roda no modo de gravação `Rich`: no modo rápido, que é o
padrão, o número de sequência do evento só nasce dentro do banco, e o extrato perdia a ordem dos lançamentos
(um teste pegou isso).

**Domínio sem dependência.** `Lastro.Dominio` não conhece banco de dados nem web: as regras são funções que
recebem o estado e devolvem os eventos (`Lancamento.Transferir`), e o estado é os eventos aplicados em ordem
(`Conta.Aplicar`). As projeções do Marten chamam essas mesmas funções.

**Concorrência: trava pessimista, em ordem.** Para conta disputada, trava (`SELECT ... FOR UPDATE`) sai melhor
que retentativa otimista: mil transferências na mesma conta viram uma fila, e não novecentas colisões. As contas
são travadas sempre na ordem do id, então A→B e B→A ao mesmo tempo nunca esperam uma pela outra em círculo
(deadlock). Se a trava demorar demais, a resposta é `409 conta-ocupada` com `Retry-After`, não um erro 500.

**Idempotência de verdade.** O app manda uma `Idempotency-Key` por operação. A chave é conferida *depois* da
trava e gravada *na mesma transação* do lançamento: vinte envios simultâneos da mesma chave viram um Pix só.
Repetir a chave devolve a mesma resposta (inclusive uma recusa por falta de saldo), com o cabeçalho
`Idempotent-Replayed`. A mesma chave com outro valor é recusada.

**Dinheiro em centavos inteiros.** `Dinheiro` é um `long` de centavos: R$ 0,10 + R$ 0,20 é exatamente R$ 0,30,
fração de centavo é recusada na entrada e uma soma que estoura o limite falha em vez de virar negativa.

**Dado pessoal fora do event store.** Nome e CPF ficam no cadastro do titular, que pode ser anonimizado
(LGPD). Os eventos, que são para sempre, só levam o id do titular. O CPF passa pela validação dos dígitos, é
único no banco (índice do PostgreSQL) e só aparece mascarado (`***.456.789-**`).

**Erros no padrão RFC 9457.** Toda recusa tem um código estável em `type` (`saldo-insuficiente`,
`conta-nao-encontrada`, `chave-de-idempotencia-reutilizada`...), para o app decidir o que mostrar.

## Testes

```bash
dotnet test     # 51 testes; os de integração sobem um PostgreSQL com Testcontainers (precisa do Docker)
```

- **Propriedades (FsCheck)**: centenas de sequências aleatórias de depósitos e Pix entre quatro clientes. Em
  todas, o dinheiro não é criado nem some, nenhum cliente fica negativo, o livro fecha e repassar o histórico
  reconstrói o mesmo saldo. Quando uma falha, o FsCheck reduz ao menor exemplo que quebra.
- **Concorrência, no PostgreSQL de verdade**: mil Pix simultâneos com saldo para cem (passam exatamente cem e
  novecentos são recusados), 400 Pix cruzados entre duas contas (nenhum trava) e 250 depósitos em paralelo.
- **Idempotência**: repetição, recusa lembrada, chave reaproveitada e vinte envios simultâneos.

## Rodando

```bash
docker run -d --name lastro-db -e POSTGRES_PASSWORD=<uma senha> -p 127.0.0.1:5432:5432 postgres:18-alpine
export ConnectionStrings__Lastro="Host=localhost;Database=postgres;Username=postgres;Password=<a senha>"
export ASPNETCORE_ENVIRONMENT=Development
dotnet run --project src/Lastro.Api
```

A documentação da API (OpenAPI + Scalar) fica em http://localhost:5000/docs.

## Stack

.NET 10 · C# 14 · ASP.NET Core (minimal APIs, validação nativa, OpenAPI) · Marten 9 (event store e documentos)
· PostgreSQL 18 · xUnit v3 · FsCheck · Testcontainers
