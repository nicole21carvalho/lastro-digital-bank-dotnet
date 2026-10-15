using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lastro.Api.Testes;

public class ChavesPixTestes(BancoDeTeste banco)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NovoEmail() => $"cliente.{Guid.NewGuid():N}@exemplo.com";

    [Fact]
    public async Task Pix_por_chave_de_email_chega_na_conta_certa()
    {
        var (ana, bia) = (await banco.NovoCliente("Ana"), await banco.NovoCliente("Bia"));
        await banco.Operador.Depositar(ana.Id, 50m);
        var email = NovoEmail();
        Assert.Equal(HttpStatusCode.Created, (await bia.CadastrarChave("Email", email.ToUpperInvariant())).StatusCode);

        var pix = await ana.PixPorChave(email, 20m);

        Assert.Equal(HttpStatusCode.Created, pix.StatusCode);
        Assert.Equal(20m, await bia.Saldo());
    }

    [Fact]
    public async Task Consultar_a_chave_mostra_so_o_necessario_para_reconhecer_quem_recebe()
    {
        var (ana, bia) = (await banco.NovoCliente(), await banco.NovoCliente("Beatriz Maria Souza"));
        await bia.CadastrarChave("Cpf");

        var dono = await ana.Http.GetFromJsonAsync<JsonElement>($"/pix/chaves/{bia.Cpf}", Ct);

        Assert.Equal("Beatriz M. S.", dono.GetProperty("nome").GetString());
        Assert.Equal($"***.{bia.Cpf[3..6]}.{bia.Cpf[6..9]}-**", dono.GetProperty("cpf").GetString());
        Assert.DoesNotContain(bia.Cpf, dono.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cada_tipo_de_chave_e_cadastrado_no_formato_do_dict()
    {
        var ana = await banco.NovoCliente();
        var celular = $"(11) 9{Random.Shared.Next(1000_0000, 9999_9999)}";
        await ana.CadastrarChave("Cpf");
        await ana.CadastrarChave("Celular", celular);
        await ana.CadastrarChave("Aleatoria");

        var chaves = await ana.Http.GetFromJsonAsync<JsonElement>($"/contas/{ana.Id}/chaves", Ct);

        var porTipo = chaves.EnumerateArray().ToDictionary(c => c.GetProperty("tipo").GetString()!, c => c.GetProperty("chave").GetString()!);
        Assert.Equal(ana.Cpf, porTipo["Cpf"]);
        Assert.Equal("+5511" + new string([.. celular.Where(char.IsAsciiDigit)])[2..], porTipo["Celular"]);
        Assert.True(Guid.TryParse(porTipo["Aleatoria"], out _));
    }

    [Fact]
    public async Task Nao_da_para_cadastrar_o_cpf_de_outra_pessoa()
    {
        var ana = await banco.NovoCliente();
        var resposta = await ana.CadastrarChave("Cpf", Api.NovoCpf());
        Assert.Equal("chave-de-outro-titular", (await resposta.Ler<Api.Problema>()).Type);
    }

    [Fact]
    public async Task Uma_chave_uma_conta()
    {
        var (ana, bia) = (await banco.NovoCliente(), await banco.NovoCliente());
        var email = NovoEmail();
        await ana.CadastrarChave("Email", email);
        Assert.Equal(HttpStatusCode.Conflict, (await bia.CadastrarChave("Email", email)).StatusCode);
    }

    [Fact]
    public async Task No_maximo_cinco_chaves_por_conta()
    {
        var ana = await banco.NovoCliente();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await ana.CadastrarChave("Aleatoria")).StatusCode);
        }
        Assert.Equal("limite-de-chaves", (await (await ana.CadastrarChave("Aleatoria")).Ler<Api.Problema>()).Type);
    }

    [Fact]
    public async Task Dez_cadastros_simultaneos_na_mesma_conta_param_em_cinco()
    {
        var ana = await banco.NovoCliente();
        var respostas = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => ana.CadastrarChave("Aleatoria")));
        Assert.Equal(5, respostas.Count(r => r.StatusCode == HttpStatusCode.Created));
        var chaves = await ana.Http.GetFromJsonAsync<JsonElement>($"/contas/{ana.Id}/chaves", Ct);
        Assert.Equal(5, chaves.GetArrayLength());
    }

    [Fact]
    public async Task A_mesma_chave_disputada_por_cinco_contas_fica_com_uma_so()
    {
        var clientes = new List<Cliente>();
        for (var i = 0; i < 5; i++)
        {
            clientes.Add(await banco.NovoCliente());
        }
        var email = NovoEmail();
        var respostas = await Task.WhenAll(clientes.Select(c => c.CadastrarChave("Email", email)));
        Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(4, respostas.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Chave_excluida_nao_recebe_mais()
    {
        var (ana, bia) = (await banco.NovoCliente(), await banco.NovoCliente());
        await banco.Operador.Depositar(ana.Id, 10m);
        var email = NovoEmail();
        await bia.CadastrarChave("Email", email);

        Assert.Equal(HttpStatusCode.NoContent, (await bia.Http.DeleteAsync($"/contas/{bia.Id}/chaves/{email}", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await ana.PixPorChave(email, 1m)).StatusCode);
        Assert.Equal(10m, await ana.Saldo());
    }
}
