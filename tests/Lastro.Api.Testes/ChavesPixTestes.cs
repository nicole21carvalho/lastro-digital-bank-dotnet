using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lastro.Api.Testes;

public class ChavesPixTestes(BancoDeTeste banco)
{
    private readonly HttpClient _http = banco.Cliente();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NovoEmail() => $"cliente.{Guid.NewGuid():N}@exemplo.com";

    [Fact]
    public async Task Pix_por_chave_de_email_chega_na_conta_certa()
    {
        var (ana, bia) = (await _http.AbrirConta("Ana"), await _http.AbrirConta("Bia"));
        await _http.Depositar(ana.Id, 50m);
        var email = NovoEmail();
        Assert.Equal(HttpStatusCode.Created, (await _http.CadastrarChave(bia.Id, "Email", email.ToUpperInvariant())).StatusCode);

        var pix = await _http.PixPorChave(ana.Id, email, 20m);

        Assert.Equal(HttpStatusCode.Created, pix.StatusCode);
        Assert.Equal(20m, await _http.Saldo(bia.Id));
    }

    [Fact]
    public async Task Consultar_a_chave_mostra_so_o_necessario_para_reconhecer_quem_recebe()
    {
        var (bia, cpf) = await _http.AbrirContaComCpf("Beatriz Maria Souza");
        await _http.CadastrarChave(bia.Id, "Cpf");

        var dono = await _http.GetFromJsonAsync<JsonElement>($"/pix/chaves/{cpf}", Ct);

        Assert.Equal("Beatriz M. S.", dono.GetProperty("nome").GetString());
        Assert.Equal($"***.{cpf[3..6]}.{cpf[6..9]}-**", dono.GetProperty("cpf").GetString());
        Assert.DoesNotContain(cpf, dono.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cada_tipo_de_chave_e_cadastrado_no_formato_do_dict()
    {
        var (ana, cpf) = await _http.AbrirContaComCpf("Ana");
        var celular = $"(11) 9{Random.Shared.Next(1000_0000, 9999_9999)}";
        await _http.CadastrarChave(ana.Id, "Cpf");
        await _http.CadastrarChave(ana.Id, "Celular", celular);
        await _http.CadastrarChave(ana.Id, "Aleatoria");

        var chaves = await _http.GetFromJsonAsync<JsonElement>($"/contas/{ana.Id}/chaves", Ct);

        var porTipo = chaves.EnumerateArray().ToDictionary(c => c.GetProperty("tipo").GetString()!, c => c.GetProperty("chave").GetString()!);
        Assert.Equal(cpf, porTipo["Cpf"]);
        Assert.Equal("+5511" + new string([.. celular.Where(char.IsAsciiDigit)])[2..], porTipo["Celular"]);
        Assert.True(Guid.TryParse(porTipo["Aleatoria"], out _));
    }

    [Fact]
    public async Task Nao_da_para_cadastrar_o_cpf_de_outra_pessoa()
    {
        var ana = await _http.AbrirConta();
        var resposta = await _http.CadastrarChave(ana.Id, "Cpf", Api.NovoCpf());
        Assert.Equal("chave-de-outro-titular", (await resposta.Ler<Api.Problema>()).Type);
    }

    [Fact]
    public async Task Uma_chave_uma_conta()
    {
        var (ana, bia) = (await _http.AbrirConta(), await _http.AbrirConta());
        var email = NovoEmail();
        await _http.CadastrarChave(ana.Id, "Email", email);
        var outra = await _http.CadastrarChave(bia.Id, "Email", email);
        Assert.Equal(HttpStatusCode.Conflict, outra.StatusCode);
    }

    [Fact]
    public async Task No_maximo_cinco_chaves_por_conta()
    {
        var ana = await _http.AbrirConta();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await _http.CadastrarChave(ana.Id, "Aleatoria")).StatusCode);
        }
        var sexta = await _http.CadastrarChave(ana.Id, "Aleatoria");
        Assert.Equal("limite-de-chaves", (await sexta.Ler<Api.Problema>()).Type);
    }

    [Fact]
    public async Task Dez_cadastros_simultaneos_na_mesma_conta_param_em_cinco()
    {
        var ana = await _http.AbrirConta();
        var respostas = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _http.CadastrarChave(ana.Id, "Aleatoria")));
        Assert.Equal(5, respostas.Count(r => r.StatusCode == HttpStatusCode.Created));
        var chaves = await _http.GetFromJsonAsync<JsonElement>($"/contas/{ana.Id}/chaves", Ct);
        Assert.Equal(5, chaves.GetArrayLength());
    }

    [Fact]
    public async Task A_mesma_chave_disputada_por_cinco_contas_fica_com_uma_so()
    {
        var contas = new List<Api.Conta>();
        for (var i = 0; i < 5; i++)
        {
            contas.Add(await _http.AbrirConta());
        }
        var email = NovoEmail();
        var respostas = await Task.WhenAll(contas.Select(c => _http.CadastrarChave(c.Id, "Email", email)));
        Assert.Equal(1, respostas.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(4, respostas.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Chave_excluida_nao_recebe_mais()
    {
        var (ana, bia) = (await _http.AbrirConta(), await _http.AbrirConta());
        await _http.Depositar(ana.Id, 10m);
        var email = NovoEmail();
        await _http.CadastrarChave(bia.Id, "Email", email);

        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/contas/{bia.Id}/chaves/{email}", Ct)).StatusCode);

        var pix = await _http.PixPorChave(ana.Id, email, 1m);
        Assert.Equal(HttpStatusCode.NotFound, pix.StatusCode);
        Assert.Equal(10m, await _http.Saldo(ana.Id));
    }

    [Fact]
    public async Task Ninguem_exclui_a_chave_de_outra_conta()
    {
        var (ana, bia) = (await _http.AbrirConta(), await _http.AbrirConta());
        var email = NovoEmail();
        await _http.CadastrarChave(bia.Id, "Email", email);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.DeleteAsync($"/contas/{ana.Id}/chaves/{email}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync($"/pix/chaves/{email}", Ct)).StatusCode);
    }
}
