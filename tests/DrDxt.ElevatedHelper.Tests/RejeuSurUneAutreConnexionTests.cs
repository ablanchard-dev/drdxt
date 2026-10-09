using System.Text.Json;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Ce que la lecture du 17/09/2026 a etabli, et que rien ne prouvait encore.
//
// `HandshakeAcceptingServer` construit un `TypedPrivilegedServer` NEUF a chaque
// connexion — donc un `NonceTracker` neuf — et ne sert qu'une requete typee.
// Consequence : le tracker de nonces ne peut pas, a lui seul, empecher le rejeu
// d'une requete capturee sur une NOUVELLE connexion, puisqu'il y est vide.
//
// Ce qui porte reellement cette securite, c'est la cle de session : elle est
// negociee a chaque connexion, et le HMAC est calcule dessus. Une requete signee
// avec la cle d'hier presentee a la connexion d'aujourd'hui doit tomber sur
// l'integrite, pas sur le nonce.
//
// C'est exactement cette phrase que ces tests figent. Sans eux, un jour ou l'autre
// quelqu'un « optimisera » la cle de session en la rendant stable, et le rejeu
// entre connexions redeviendrait possible sans qu'un seul test rougisse.
// -----------------------------------------------------------------------
public class RejeuSurUneAutreConnexionTests
{
    private static byte[] NouvelleCle()
    {
        var k = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(k);
        return k;
    }

    private sealed class ToutAutorise : ICallerAuthenticator
    {
        public Result Authorize(CallerInfo caller) => Result.Ok();
    }

    private sealed class ExecuteurTemoin : IPrivilegedOpExecutor
    {
        public int Appels { get; private set; }

        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            Appels++;
            return Task.FromResult(Result.Ok());
        }

        public Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct)
        {
            Appels++;
            return Task.FromResult(Result<byte[]>.Ok(Array.Empty<byte>()));
        }
    }

    private static TypedPrivilegedRequest Signee(byte[] cle)
    {
        var opJson = PrivilegedOpCodec.Serialize(new PingOp()).Value!;
        var nonce = Guid.NewGuid().ToString("N");
        return new TypedPrivilegedRequest
        {
            OpJson = opJson,
            Nonce = nonce,
            HmacBase64 = TypedRequestHmac.Compute(cle, opJson, nonce),
        };
    }

    [Fact]
    public async Task Une_requete_capturee_ne_rejoue_pas_sur_la_connexion_suivante()
    {
        var cleDHier = NouvelleCle();
        var requeteCapturee = Signee(cleDHier);

        // La connexion suivante a negocie sa cle, et son tracker de nonces est vierge :
        // si la securite tenait au nonce, ce rejeu passerait.
        var executeur = new ExecuteurTemoin();
        var connexionSuivante = new TypedPrivilegedServer(
            NouvelleCle(), new ToutAutorise(), new OpAllowlist(), executeur);

        var reponse = await connexionSuivante.ProcessTypedRequestAsync(requeteCapturee, default);

        reponse.Ok.Should().BeFalse();
        executeur.Appels.Should().Be(0, "rien de privilegie ne doit s'executer sur un rejeu");
    }

    [Fact]
    public async Task La_meme_requete_passe_sur_la_connexion_qui_l_a_signee()
    {
        // Le temoin : sans lui, un serveur qui refuserait tout ferait passer le test
        // precedent sans rien garantir.
        var cle = NouvelleCle();
        var executeur = new ExecuteurTemoin();
        var serveur = new TypedPrivilegedServer(cle, new ToutAutorise(), new OpAllowlist(), executeur);

        var reponse = await serveur.ProcessTypedRequestAsync(Signee(cle), default);

        reponse.Ok.Should().BeTrue();
        executeur.Appels.Should().Be(1);
    }

    [Fact]
    public void Deux_connexions_ne_recoivent_jamais_la_meme_cle_de_session()
    {
        // La propriete dont tout le reste depend, prise a sa source.
        var generateur = new CryptoSessionKeyGenerator();
        var cles = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 200; i++)
            cles.Add(Convert.ToBase64String(generateur.NewSessionKey()));

        cles.Should().HaveCount(200);
    }
}
