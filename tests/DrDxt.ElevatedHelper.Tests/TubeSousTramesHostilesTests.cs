using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// Ce qu'un appelant authentifié mais compromis peut envoyer : n'importe quels octets, signés par la clé de session qu'il
// détient. Une trame fausse est refusée sans lever, n'alloue rien au-delà de la limite, n'atteint jamais l'exécuteur, et
// le helper sert la connexion suivante. La graine est fixe : un échec se rejoue à l'identique.
public sealed class TubeSousTramesHostilesTests
{
    private const int Graine = 0x0D5D7;
    private static readonly TimeSpan DelaiCourt = TimeSpan.FromSeconds(1);

    private static readonly PrivilegedOp[] Corpus =
    [
        new PingOp(),
        new SetRegistryValueOp(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueType.DWord, "2"),
        new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance),
        new RunServiceControlOp("AMD3DVCacheSvc", ServiceAction.Start),
        new ApplyGpuFanCurveOp(0, [new FanCurvePointDto(50, 60), new FanCurvePointDto(80, 80)]),
        new SetBcdValueOp("useplatformclock", "No"),
        new RestartDeviceOp(@"PCI\VEN_10DE&DEV_2882\4&1"),
        new RemoveDriverPackageOp("oem12.inf"),
        new CorrigerDroitsDuServiceOp("Steam Client Service"),
        new ApplyUploadCapOp("cod.exe", 50_000_000),
        new CommitManifestOp([new AppliedTweak("latency.reflex.lowLatencyMode", "2", "{}", DateTimeOffset.UnixEpoch)], [], Retirer: ["os.hags"]),
    ];

    private sealed class Executeur : IPrivilegedOpExecutor
    {
        public List<PrivilegedOp> Executees { get; } = [];
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            lock (Executees) Executees.Add(op);
            return Task.FromResult(Result.Ok());
        }
    }

    private sealed class Admis : ICallerAuthenticator
    {
        public Result Authorize(CallerInfo caller) => Result.Ok();
    }

    private sealed class Journal : IServiceLoopLog
    {
        public int Pannes;
        public void Fault(int consecutive, int max, string detail) => Pannes++;
        public void FailedClosed() { }
    }

    // ── Le lecteur de trames ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(WireCodec.MaxMessageBytes + 1)]
    [InlineData(int.MaxValue)]
    public async Task Une_longueur_hors_limite_n_alloue_rien_et_ne_lit_pas_la_suite(int longueur)
    {
        using var flux = new MemoryStream(Prefixe(longueur).Concat(new byte[32]).ToArray());

        // Un MemoryStream répond sans céder : tout se passe sur ce fil, le compteur le voit.
        var avant = GC.GetAllocatedBytesForCurrentThread();
        var lu = await WireCodec.ReadMessageAsync<TypedPrivilegedRequest>(flux, default);
        var alloue = GC.GetAllocatedBytesForCurrentThread() - avant;

        lu.Should().BeNull();
        flux.Position.Should().Be(4);
        alloue.Should().BeLessThan(4096);
    }

    [Fact]
    public async Task Une_trame_tronquee_a_n_importe_quel_octet_rend_null()
    {
        var trame = Trame(Encoding.UTF8.GetBytes("""{"OpJson":"{}","Nonce":"n","HmacBase64":"h"}"""));
        for (var coupe = 0; coupe < trame.Length; coupe++)
        {
            using var flux = new MemoryStream(trame[..coupe]);
            (await WireCodec.ReadMessageAsync<TypedPrivilegedRequest>(flux, default)).Should().BeNull();
        }
    }

    [Theory]
    [InlineData("""null""")]
    [InlineData("""[]""")]
    [InlineData("""{"OpJson":1}""")]
    [InlineData("""{"Inconnu":""}""")]
    [InlineData("""{"OpJson":"\ud800"}""")]
    [InlineData("""{"OpJson":"\udc00\ud800"}""")]
    [InlineData("""{"OpJson":"\u00zz"}""")]
    [InlineData("""{"$type":"System.Object"}""")]
    public async Task Une_enveloppe_fausse_rend_null(string json)
    {
        using var flux = new MemoryStream(Trame(Encoding.UTF8.GetBytes(json)));
        (await WireCodec.ReadMessageAsync<TypedPrivilegedRequest>(flux, default)).Should().BeNull();
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, 0xFD })]
    [InlineData(new byte[] { (byte)'{', (byte)'"', 0xC0, 0xAF, (byte)'"', (byte)':', (byte)'1', (byte)'}' })]
    [InlineData(new byte[] { (byte)'"', 0xED, 0xA0, 0x80, (byte)'"' })] // surrogat encodé en UTF-8
    public async Task Un_utf8_invalide_rend_null(byte[] charge)
    {
        using var flux = new MemoryStream(Trame(charge));
        (await WireCodec.ReadMessageAsync<TypedPrivilegedRequest>(flux, default)).Should().BeNull();
    }

    [Fact]
    public async Task Une_enveloppe_trop_profonde_rend_null()
    {
        var charge = "{\"OpJson\":" + new string('[', 5000) + new string(']', 5000) + "}";
        using var flux = new MemoryStream(Trame(Encoding.UTF8.GetBytes(charge)));
        (await WireCodec.ReadMessageAsync<TypedPrivilegedRequest>(flux, default)).Should().BeNull();
    }

    [Fact]
    public async Task Aucune_trame_mutee_ne_fait_lever_le_lecteur()
    {
        var alea = new Random(Graine);
        var cle = RandomNumberGenerator.GetBytes(32);
        foreach (var op in Corpus)
        {
            var trame = Trame(JsonSerializer.SerializeToUtf8Bytes(Signee(cle, PrivilegedOpCodec.Serialize(op).Value!)));
            for (var i = 0; i < 500; i++)
            {
                using var flux = new MemoryStream(Muter(alea, trame));
                // Le contrat est null ou une requête, jamais une exception.
                var lecture = () => WireCodec.ReadMessageAsync<TypedPrivilegedRequest>(flux, default);
                await lecture.Should().NotThrowAsync();
            }
        }
    }

    // ── Du codec à l'allowlist, signé par la clé qu'un appelant compromis détient ────────────

    [Fact]
    public async Task Aucune_mutation_signee_ne_fait_lever_et_rien_de_hors_liste_n_est_execute()
    {
        var alea = new Random(Graine);
        var cle = RandomNumberGenerator.GetBytes(32);
        var exec = new Executeur();
        var serveur = new TypedPrivilegedServer(cle, new Admis(), new OpAllowlist(), exec);
        var allowlist = new OpAllowlist();
        var refus = 0;

        foreach (var op in Corpus)
        {
            var json = Encoding.UTF8.GetBytes(PrivilegedOpCodec.Serialize(op).Value!);
            for (var i = 0; i < 1500; i++)
            {
                var mutee = Encoding.UTF8.GetString(Muter(alea, json));

                // L'allowlist seule : le serveur rattrape tout, ce qui cacherait une exception ici.
                if (PrivilegedOpCodec.Deserialize(mutee) is { IsOk: true, Value: { } liee })
                {
                    var valider = () => allowlist.Validate(liee);
                    valider.Should().NotThrow(mutee);
                }

                var reponse = await serveur.ProcessTypedRequestAsync(Signee(cle, mutee), default);
                if (!reponse.Ok) refus++;
            }
        }

        refus.Should().BeGreaterThan(0);
        exec.Executees.Should().NotBeEmpty("témoin : des mutations neutres restent des ops permises");
        foreach (var executee in exec.Executees)
        {
            allowlist.Validate(executee).IsOk.Should().BeTrue();
            ChainesSansControle(executee).Should().BeTrue(PrivilegedOpCodec.Serialize(executee).Value);
            if (executee is SetRegistryValueOp r)
                r.Path.Should().BeEquivalentTo(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
        }
    }

    [Theory]
    [InlineData("""{"op":11,"GpuIndex":0,"Points":[null]}""")]
    [InlineData("""{"op":19,"Tweaks":[null],"RevertOnly":[]}""")]
    [InlineData("""{"op":19,"Tweaks":[],"RevertOnly":[],"Retirer":[null]}""")]
    [InlineData("""{"op":19,"Tweaks":[],"RevertOnly":[{"TweakId":null,"DesiredValueJson":null,"OriginalValueJson":null,"AppliedAt":"2026-01-01T00:00:00Z"}]}""")]
    [InlineData("""{"op":45,"PoseJson":"{\"op\":45,\"PoseJson\":\"{\\\"op\\\":0}\"}"}""")]
    [InlineData("""{"op":46,"PoseeJson":"pas du json"}""")]
    [InlineData("""{"op":17,"Name":"Setup","VendorGuid":""}""")]
    public async Task Une_charge_hostile_signee_n_atteint_pas_l_executeur(string json)
    {
        var cle = RandomNumberGenerator.GetBytes(32);
        var exec = new Executeur();
        var serveur = new TypedPrivilegedServer(cle, new Admis(), new OpAllowlist(), exec);

        var reponse = await serveur.ProcessTypedRequestAsync(Signee(cle, json), default);

        reponse.Ok.Should().BeFalse();
        exec.Executees.Should().BeEmpty();
    }

    public static TheoryData<PrivilegedOp> OpsAuNomFerme() => new()
    {
        new RestartDeviceOp("PCI\\VEN_10DE\n"),
        new RemoveDriverPackageOp("oem12.inf\n"),
        new RestoreDriverPackageOp("oem12.inf\n"),
        new CorrigerDroitsDuServiceOp("Steam\n"),
        new RendreDroitsDuServiceOp("Steam\n"),
        new ApplyUploadCapOp("cod.exe\n", 50_000_000),
        new RetainServiceUploadCapOp("cod.exe\n", 50_000_000),
        new SetBcdValueOp("useplatformclock\n", "No"),
        new SetBcdValueOp("useplatformclock", "No\n"),
        new DeleteBcdValueOp("useplatformclock\n"),
        new RunServiceControlOp("AMD3DVCacheSvc\n", ServiceAction.Start),
        new SetStartupApprovalOp(RegistryHiveKind.CurrentUser, SetStartupApprovalOp.Run, "x\n", false),
        new ApplyNvApiDrsSettingOp(0x1057EB71, 0, "C:\\Jeux\\cod.exe\n"),
    };

    // Un nom ferme ne finit jamais par un saut de ligne : `$` en regex .NET l'accepte, seul `\z` ancre à la fin.
    [Theory]
    [MemberData(nameof(OpsAuNomFerme))]
    public void Un_saut_de_ligne_final_sort_un_nom_de_sa_liste(PrivilegedOp op)
        => new OpAllowlist().Validate(op).IsOk.Should().BeFalse();

    // ── Sur un vrai tube ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Le_helper_sert_la_connexion_suivante_apres_chaque_mauvaise_trame()
    {
        var tube = "DrDXT.Fuzz." + Guid.NewGuid().ToString("N");
        var exec = new Executeur();
        var journal = new Journal();
        var serveur = new HandshakeAcceptingServer(
            new Admis(), new CryptoSessionKeyGenerator(), new OpAllowlist(), exec, tube, delaiDeLecture: DelaiCourt);
        using var arret = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var boucle = new HelperServiceLoop(serveur.AcceptOneAsync, journal, _ => Task.CompletedTask).RunAsync(arret.Token);

        // Avant la poignée de main.
        byte[][] avant =
        [
            [0x00],
            Prefixe(-1),
            Prefixe(int.MaxValue),
            Prefixe(WireCodec.MaxMessageBytes + 1),
            [.. Prefixe(100), .. new byte[10]],
            Trame([0xFF, 0xFE]),
            Trame(Encoding.UTF8.GetBytes(new string('[', 5000))),
            Trame(Encoding.UTF8.GetBytes("""{"Protocol":"autre"}""")),
            Trame(Encoding.UTF8.GetBytes("""{"Inconnu":1}""")),
        ];
        foreach (var octets in avant)
        {
            await using var client = await Connecter(tube);
            await EcrireSansAttendre(client, octets);
            await Vider(client);
        }

        // Après la poignée de main : la clé est là, le reste est faux.
        byte[][] apres =
        [
            Prefixe(int.MaxValue),
            [.. Prefixe(100), .. new byte[10]],
            Trame([0xFF]),
            Trame(Encoding.UTF8.GetBytes("""{"OpJson":"x","Nonce":"n","HmacBase64":"AAAA"}""")),
        ];
        foreach (var octets in apres)
        {
            await using var client = await Connecter(tube);
            await PoigneeDeMain(client);
            await EcrireSansAttendre(client, octets);
            await Vider(client);
        }

        // Signés : un discriminant inconnu, une op hors liste, une liaison impossible.
        foreach (var json in new[] { """{"op":33}""", """{"op":9,"ServiceName":"WinDefend","Action":2}""", """{"op":1}""" })
        {
            await using var client = await Connecter(tube);
            var cle = await PoigneeDeMain(client);
            await WireCodec.WriteMessageAsync(client, Signee(cle, json), default);
            var reponse = await WireCodec.ReadMessageAsync<PrivilegedResponse>(client, default);
            reponse!.Ok.Should().BeFalse(json);
        }

        exec.Executees.Should().BeEmpty();

        // Le témoin : après tout ça, une op permise passe.
        await using (var client = await Connecter(tube))
        {
            var cle = await PoigneeDeMain(client);
            var json = PrivilegedOpCodec.Serialize(new SetActivePowerPlanOp(KnownPowerPlans.Balanced)).Value!;
            await WireCodec.WriteMessageAsync(client, Signee(cle, json), default);
            (await WireCodec.ReadMessageAsync<PrivilegedResponse>(client, default))!.Ok.Should().BeTrue();
        }
        exec.Executees.Should().ContainSingle().Which.Should().BeOfType<SetActivePowerPlanOp>();
        journal.Pannes.Should().Be(0, "une mauvaise trame n'est pas une panne de la boucle");

        arret.Cancel();
        (await boucle).Should().Be(0);
    }

    // Le tube n'a qu'une instance et aucun tampon de sortie : une écriture attend que le client lise. Un client qui
    // envoie une requête puis ne lit jamais la réponse ne tient pas le tube.
    [Fact]
    public async Task Un_client_qui_ne_lit_pas_la_reponse_libere_le_tube()
    {
        var tube = "DrDXT.Fuzz." + Guid.NewGuid().ToString("N");
        var serveur = new HandshakeAcceptingServer(
            new Admis(), new CryptoSessionKeyGenerator(), new OpAllowlist(), new Executeur(), tube, delaiDeLecture: DelaiCourt);
        using var arret = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var tache = serveur.AcceptOneAsync(arret.Token);

        await using var client = await Connecter(tube);
        await PoigneeDeMain(client);
        await client.WriteAsync(Trame([0xFF]));
        await client.FlushAsync();

        var fini = await Task.WhenAny(tache, Task.Delay(TimeSpan.FromSeconds(5)));
        var libere = fini == tache;
        arret.Cancel(); // libère le serveur si l'assertion va échouer
        libere.Should().BeTrue("la connexion doit expirer, pas tenir le tube jusqu'à l'arrêt du service");
        await tache;
    }

    // ── Outils ──────────────────────────────────────────────────────────────────────────────

    private static byte[] Prefixe(int longueur)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(p, longueur);
        return p;
    }

    private static byte[] Trame(byte[] charge) => [.. Prefixe(charge.Length), .. charge];

    private static TypedPrivilegedRequest Signee(byte[] cle, string json)
    {
        var nonce = Guid.NewGuid().ToString("N");
        return new TypedPrivilegedRequest { OpJson = json, Nonce = nonce, HmacBase64 = TypedRequestHmac.Compute(cle, json, nonce) };
    }

    private static byte[] Muter(Random alea, byte[] origine)
    {
        var octets = origine.ToList();
        for (var n = alea.Next(1, 4); n > 0 && octets.Count > 0; n--)
        {
            var i = alea.Next(octets.Count);
            switch (alea.Next(4))
            {
                case 0: octets[i] ^= (byte)(1 << alea.Next(8)); break;
                case 1: octets[i] = (byte)alea.Next(256); break;
                case 2: octets.RemoveAt(i); break;
                default: octets.RemoveRange(i, octets.Count - i); break;
            }
        }
        return octets.ToArray();
    }

    // La valeur d'une feuille de registre peut être multiligne (PagingFiles) ; tout autre champ est un nom ou un identifiant.
    private static bool ChainesSansControle(PrivilegedOp op)
    {
        using var doc = JsonDocument.Parse(PrivilegedOpCodec.Serialize(op).Value!);
        return Parcourir(doc.RootElement);

        static bool Parcourir(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => !e.GetString()!.Any(char.IsControl),
            JsonValueKind.Array => e.EnumerateArray().All(Parcourir),
            JsonValueKind.Object => e.EnumerateObject().Where(p => p.Name != "Value").All(p => Parcourir(p.Value)),
            _ => true,
        };
    }

    private static async Task<NamedPipeClientStream> Connecter(string tube)
    {
        var client = new NamedPipeClientStream(".", tube, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000);
        return client;
    }

    private static async Task<byte[]> PoigneeDeMain(Stream client)
    {
        await WireCodec.WriteMessageAsync(client, new HandshakeHello(), default);
        var r = await WireCodec.ReadMessageAsync<HandshakeResult>(client, default);
        r!.Ok.Should().BeTrue();
        return Convert.FromBase64String(r.SessionKeyBase64!);
    }

    // Le serveur peut refuser avant d'avoir tout lu : l'écriture du client s'arrête alors sur un tube fermé ou expire.
    private static async Task EcrireSansAttendre(Stream client, byte[] octets)
    {
        using var borne = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await client.WriteAsync(octets, borne.Token); }
        catch (IOException) { }
        catch (OperationCanceledException) { }
    }

    private static async Task Vider(Stream client)
    {
        using var borne = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tampon = new byte[1024];
        try { while (await client.ReadAsync(tampon, borne.Token) > 0) { } }
        catch (IOException) { }
    }
}
