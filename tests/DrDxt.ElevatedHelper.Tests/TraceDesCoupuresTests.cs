using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// La trace des programmes que DrDXT a coupés au démarrage : notée avant l'écriture, retirée si l'écriture échoue et à la
/// réactivation. Chaque magasin n'est cru que pour sa ruche, et celui de la machine que dans une racine de confiance.
/// </summary>
public sealed class TraceDesCoupuresTests : IDisposable
{
    private readonly string _dossier = Directory.CreateTempSubdirectory("drdxt-coupures-").FullName;
    public void Dispose() { try { Directory.Delete(_dossier, true); } catch (IOException) { } }

    private string Fichier => Path.Combine(_dossier, "coupures.json");
    private TraceDesCoupures Session() => new(Fichier, confiance: false, RegistryHiveKind.CurrentUser);

    private static CoupureDuDemarrage Coupure(string nom = "Discord", RegistryHiveKind ruche = RegistryHiveKind.CurrentUser)
        => new(ruche, SetStartupApprovalOp.Run, nom, "030000000102030405060708");

    // L'App tuée juste après l'écriture : la coupure est déjà notée, son annulation connue.
    [Fact]
    public void La_coupure_est_notee_avant_l_ecriture()
    {
        var trace = Session();
        IReadOnlyList<CoupureDuDemarrage>? vueALEcriture = null;

        TraceDesCoupures.Autour(trace, Coupure(), actif: false, () => { vueALEcriture = Session().Lire(); return Result.Ok(); })
            .IsOk.Should().BeTrue();

        vueALEcriture.Should().Equal(Coupure());
        Session().Lire().Should().Equal(Coupure());
    }

    [Fact]
    public void Une_ecriture_refusee_retire_la_coupure()
    {
        TraceDesCoupures.Autour(Session(), Coupure(), actif: false, () => Result.Fail(ErrorCodes.IoFailure, "refus"))
            .IsOk.Should().BeFalse();

        Session().Lire().Should().HaveCount(0);
    }

    // Sans trace possible, rien ne s'écrit : une coupure sans annulation connue est ce que la garde doit empêcher.
    [Fact]
    public void Sans_trace_possible_rien_ne_s_ecrit()
    {
        var ecrit = false;
        var bloque = new TraceDesCoupures(Path.Combine(_dossier, "absent", "\0", "x.json"), confiance: false, RegistryHiveKind.CurrentUser);

        TraceDesCoupures.Autour(bloque, Coupure(), actif: false, () => { ecrit = true; return Result.Ok(); }).IsOk.Should().BeFalse();

        ecrit.Should().BeFalse();
    }

    [Fact]
    public void La_reactivation_retire_la_coupure()
    {
        TraceDesCoupures.Autour(Session(), Coupure(), actif: false, () => Result.Ok());
        TraceDesCoupures.Autour(Session(), Coupure("Steam"), actif: false, () => Result.Ok());

        TraceDesCoupures.Autour(Session(), Coupure() with { Octets = "02" }, actif: true, () => Result.Ok()).IsOk.Should().BeTrue();

        Session().Lire().Should().Equal(Coupure("Steam"));
    }

    // Le fichier du profil peut être réécrit par tout programme du compte : il n'est jamais cru pour une entrée machine.
    [Fact]
    public void Un_magasin_n_est_cru_que_pour_sa_ruche()
    {
        Session().Noter(Coupure("Indesirable", RegistryHiveKind.LocalMachine)).IsOk.Should().BeTrue();
        Session().Noter(Coupure()).IsOk.Should().BeTrue();

        Session().Lire().Should().Equal(Coupure());
    }

    // Le magasin de la machine n'est lu que dans une racine de confiance (écriture admin) : un fichier posé ailleurs, ou
    // possédé par le compte, n'est jamais lu.
    [Fact]
    public void Le_magasin_machine_hors_racine_de_confiance_n_est_jamais_lu()
    {
        var forge = new TraceDesCoupures(Fichier, confiance: false, RegistryHiveKind.LocalMachine);
        forge.Noter(Coupure("Indesirable", RegistryHiveKind.LocalMachine)).IsOk.Should().BeTrue();
        forge.Lire().Should().ContainSingle("sans exigence de confiance, le fichier se lit");

        new TraceDesCoupures(Fichier, confiance: true, RegistryHiveKind.LocalMachine).Lire().Should().HaveCount(0);
    }

    // Une trace illisible n'est jamais lue vide puis écrasée : les autres coupures qu'elle porte perdraient leur Réactiver.
    [Fact]
    public void Une_trace_illisible_refuse_la_coupure_et_reste_intacte()
    {
        File.WriteAllText(Fichier, "[{ abîmé");
        var avant = File.ReadAllBytes(Fichier);
        var ecrit = false;

        TraceDesCoupures.Autour(Session(), Coupure(), actif: false, () => { ecrit = true; return Result.Ok(); }).IsOk.Should().BeFalse();

        ecrit.Should().BeFalse("sans la note, rien ne se coupe");
        File.ReadAllBytes(Fichier).Should().Equal(avant);
    }

    [Fact]
    public void Retirer_d_une_trace_illisible_echoue_et_la_laisse_intacte()
    {
        File.WriteAllText(Fichier, "[{ abîmé");
        var avant = File.ReadAllBytes(Fichier);

        Session().Retirer(RegistryHiveKind.CurrentUser, SetStartupApprovalOp.Run, "Discord").IsOk.Should().BeFalse();

        File.ReadAllBytes(Fichier).Should().Equal(avant);
    }
}
