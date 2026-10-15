using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks;

/// <summary>
/// Remarquer une perte, et ne pas crier sur ce qu'on n'a pas su lire.
///
/// Le risque de ce type n'est pas de rater une perte : c'est d'en INVENTER. Un levier qu'on ne
/// sait pas relire, ou une lecture qui echoue, ne sont pas des gains perdus — les compter ferait
/// annoncer au produit des pertes qui n'existent pas, sur des machines ou tout va bien.
/// </summary>
public class GainsDisparusTests
{
    private sealed class LecteurParFeuille(Dictionary<string, uint?> valeurs, bool echoue = false)
        : IRegistryReader
    {
        public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name)
            => echoue
                ? Result<uint?>.Fail(ErrorCodes.TweakSdkUnavailable, "lecture refusee")
                : Result<uint?>.Ok(valeurs.TryGetValue(name, out var v) ? v : null);

        public Result<string?> ReadString(RegistryHiveKind hive, string path, string name)
            => Result<string?>.Ok(null);

        public Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name)
            => Result<string?>.Ok(null);
    }

    private static TweakSnapshot Applique(string id, string nomFeuille, string valeurAvant)
    {
        var op = new SetRegistryValueOp(RegistryHiveKind.LocalMachine, @"SYSTEM\Test", nomFeuille,
            RegistryValueType.DWord, valeurAvant);
        var json = PrivilegedOpCodec.Serialize(op);
        json.IsOk.Should().BeTrue();
        return new TweakSnapshot(id, json.Value!);
    }

    [Fact]
    public void Un_levier_revenu_a_son_etat_d_avant_est_rendu_comme_perdu()
    {
        var appliques = new[] { Applique("os.hags", "HwSchMode", "1") };
        var lecteur = new LecteurParFeuille(new() { ["HwSchMode"] = 1 });   // revenu a 1

        var perdus = GainsDisparus.Detecter(appliques, lecteur);

        perdus.Should().ContainSingle().Which.TweakId.Should().Be("os.hags");
        perdus[0].Cible.Should().Contain("HwSchMode",
            "le constat doit etre verifiable a la main, donc nommer la feuille");
    }

    [Fact]
    public void Un_levier_dont_la_valeur_a_change_n_est_PAS_rendu_comme_perdu()
    {
        var appliques = new[] { Applique("os.hags", "HwSchMode", "1") };
        var lecteur = new LecteurParFeuille(new() { ["HwSchMode"] = 2 });   // toujours different

        GainsDisparus.Detecter(appliques, lecteur).Should().BeEmpty();
    }

    [Fact]
    public void Une_lecture_en_echec_n_invente_AUCUNE_perte()
    {
        var appliques = new[] { Applique("os.hags", "HwSchMode", "1") };

        GainsDisparus.Detecter(appliques, new LecteurParFeuille(new(), echoue: true))
            .Should().BeEmpty("« je n'ai pas pu lire » n'est pas « le gain a disparu » : "
                              + "le confondre ferait crier le produit sur une machine saine");
    }

    [Fact]
    public void Un_levier_non_relisable_n_invente_aucune_perte()
    {
        var plan = PrivilegedOpCodec.Serialize(new SetActivePowerPlanOp(Guid.NewGuid()));
        var appliques = new[] { new TweakSnapshot("fixit.sanePowerPlan", plan.Value!) };

        GainsDisparus.Detecter(appliques, new LecteurParFeuille(new())).Should().BeEmpty();
    }

    [Fact]
    public void Plusieurs_leviers_sont_tries_un_par_un_et_seuls_les_perdus_sortent()
    {
        var appliques = new[]
        {
            Applique("perdu.un", "A", "5"),
            Applique("tenu.deux", "B", "5"),
            Applique("perdu.trois", "C", "0"),
        };
        var lecteur = new LecteurParFeuille(new() { ["A"] = 5, ["B"] = 9, ["C"] = 0 });

        GainsDisparus.Detecter(appliques, lecteur)
            .Select(p => p.TweakId).Should().BeEquivalentTo(["perdu.un", "perdu.trois"]);
    }

    [Fact]
    public void Aucun_levier_applique_ne_rend_aucune_perte_et_ne_leve_rien()
    {
        GainsDisparus.Detecter(null, new LecteurParFeuille(new())).Should().BeEmpty();
        GainsDisparus.Detecter([], new LecteurParFeuille(new())).Should().BeEmpty();
    }

    // Une origine égale à la valeur posée : DrDXT n'a rien changé, il n'y a rien de « défait ». Sinon le VRR
    // s'afficherait à la fois « revenu à la valeur d'avant » et « posé, Annuler ».
    [Fact]
    public void Une_origine_egale_a_la_valeur_posee_n_est_pas_un_gain_perdu()
    {
        var avant = Applique("latency.vrrFlipModelOn", "VRR", "1");
        var sansChangement = avant with { AppliedValueJson = avant.OriginalValueJson };
        GainsDisparus.Detecter([sansChangement], new LecteurParFeuille(new() { ["VRR"] = 1 })).Should().BeEmpty();
        GainsDisparus.Detecter([avant], new LecteurParFeuille(new() { ["VRR"] = 1 })).Should().ContainSingle("témoin : sans valeur posée connue, le retour se dit");
    }
}
