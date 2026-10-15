using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Application.Tests.Tweaks;

/// <summary>
/// Relire ce qu'un levier a ecrit, et surtout ne pas en dire plus.
///
/// Le snapshot ne porte que l'etat original. Il ne porte pas la valeur visee. Toute la valeur de
/// ces tests est de figer cette limite : la relecture sait dire « la valeur est revenue a celle
/// d'avant » — le gain a disparu, c'est le constat qui manquait — et elle ne sait pas dire « le
/// levier est correctement applique ». Le jour ou quelqu'un renommera `DifferentDeAvant` en
/// `Applique`, un de ces tests devra le contredire.
///
/// Les snapshots sont fabriques par `PrivilegedOpCodec.Serialize`, jamais ecrits a la main :
/// un JSON invente testerait ma lecture du format, pas le format.
/// </summary>
public class TweakReadbackTests
{
    private sealed class LecteurFictif(uint? valeur, bool echoue = false) : IRegistryReader
    {
        public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name)
            => echoue
                ? Result<uint?>.Fail(ErrorCodes.TweakSdkUnavailable, "lecture refusee")
                : Result<uint?>.Ok(valeur);

        public Result<string?> ReadString(RegistryHiveKind hive, string path, string name)
            => Result<string?>.Ok(null);

        public Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name)
            => Result<string?>.Ok(null);
    }

    private static TweakSnapshot Snapshot(PrivilegedOp op)
    {
        var json = PrivilegedOpCodec.Serialize(op);
        json.IsOk.Should().BeTrue("le codec doit savoir serialiser l'op que le revert rejouerait");
        return new TweakSnapshot("levier.test", json.Value!);
    }

    private static readonly SetRegistryValueOp ValeurAvant =
        new(RegistryHiveKind.LocalMachine, @"SYSTEM\Test", "Reglage", RegistryValueType.DWord, "20");

    [Fact]
    public void Une_valeur_revenue_a_celle_d_avant_dit_que_le_GAIN_A_DISPARU()
    {
        // C'est le constat que personne ne faisait : onze leviers sur quinze ne sont pas persistes,
        // et leurs gains disparaissent au reboot sans que rien ne le remarque.
        var relu = TweakReadback.Relire(Snapshot(ValeurAvant), new LecteurFictif(20));

        relu.Etat.Should().Be(EtatRelu.RevenuAvantApplication);
        relu.Cible.Should().Contain("Reglage");
        relu.ValeurCourante.Should().Be("20");
    }

    [Fact]
    public void Une_valeur_differente_ne_prouve_PAS_que_le_levier_est_applique()
    {
        var relu = TweakReadback.Relire(Snapshot(ValeurAvant), new LecteurFictif(0));

        relu.Etat.Should().Be(EtatRelu.DifferentDeAvant,
            "le snapshot ne porte pas la valeur visee : un tiers ayant pose une troisieme valeur "
            + "donnerait exactement ce resultat, et l'appeler « applique » serait une invention");
        relu.ValeurAvant.Should().Be("20");
        relu.ValeurCourante.Should().Be("0");
    }

    [Fact]
    public void Un_original_ABSENT_se_compare_a_une_absence()
    {
        // Quand le revert SUPPRIME la valeur, c'est que rien n'existait avant. « Revenu a avant »
        // veut alors dire « la valeur n'existe plus ».
        var suppression = new DeleteRegistryValueOp(RegistryHiveKind.LocalMachine, @"SYSTEM\Test", "Reglage");

        TweakReadback.Relire(Snapshot(suppression), new LecteurFictif(null))
            .Etat.Should().Be(EtatRelu.RevenuAvantApplication);

        TweakReadback.Relire(Snapshot(suppression), new LecteurFictif(1))
            .Etat.Should().Be(EtatRelu.DifferentDeAvant);
    }

    [Fact]
    public void Une_lecture_qui_echoue_est_un_TROU_et_jamais_un_etat()
    {
        var relu = TweakReadback.Relire(Snapshot(ValeurAvant), new LecteurFictif(20, echoue: true));

        relu.Etat.Should().Be(EtatRelu.NonLu,
            "« je n'ai pas pu lire » ne doit jamais se ranger avec « la valeur est revenue »");
    }

    // Seuls les DWORD se relisaient. Le VRR (chaîne), le mode Jeu (composite) et le
    // fichier d'échange (chaîne multiple) étaient « non relisables » : la carte « Ce que DrDXT a posé » les montrait
    // encore posés après qu'ils étaient revenus, et « Annuler » aurait réécrit la chaîne d'avant par-dessus un réglage
    // fait depuis.
    private sealed class LecteurDe(Dictionary<string, string?> valeurs) : IRegistryReader
    {
        public Result<uint?> ReadDword(RegistryHiveKind hive, string path, string name)
            => Result<uint?>.Ok(valeurs.TryGetValue(name, out var v) && v is not null ? uint.Parse(v) : null);
        public Result<string?> ReadString(RegistryHiveKind hive, string path, string name)
            => Result<string?>.Ok(valeurs.GetValueOrDefault(name));
        public Result<string?> ReadMultiString(RegistryHiveKind hive, string path, string name)
            => Result<string?>.Ok(valeurs.GetValueOrDefault(name));
    }

    [Theory]
    [InlineData("SwapEffectUpgradeEnable=1;", EtatRelu.RevenuAvantApplication)]
    [InlineData("SwapEffectUpgradeEnable=1;VRROptimizeEnable=1", EtatRelu.DifferentDeAvant)]
    public void Une_chaine_se_relit(string courante, EtatRelu attendu)
    {
        var avant = new SetRegistryValueOp(RegistryHiveKind.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences",
            "DirectXUserGlobalSettings", RegistryValueType.String, "SwapEffectUpgradeEnable=1;");

        TweakReadback.Relire(Snapshot(avant), new LecteurDe(new() { ["DirectXUserGlobalSettings"] = courante }))
            .Etat.Should().Be(attendu);
    }

    [Theory]
    [InlineData(null, null, EtatRelu.RevenuAvantApplication)]
    [InlineData("1", null, EtatRelu.DifferentDeAvant)]
    [InlineData("1", "1", EtatRelu.DifferentDeAvant)]
    public void Un_composite_n_est_revenu_que_si_toutes_ses_feuilles_le_sont(string? allow, string? enabled, EtatRelu attendu)
    {
        var avant = CompositeSnapshotCodec.Encode(new PrivilegedOp[]
        {
            new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode"),
            new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled"),
        }).Value!;

        TweakReadback.Relire(new TweakSnapshot("latency.gameModeOn", avant),
                new LecteurDe(new() { ["AllowAutoGameMode"] = allow, ["AutoGameModeEnabled"] = enabled }))
            .Etat.Should().Be(attendu);
    }

    // Le levier ne s'applique que fichier d'échange désactivé ; l'origine réelle est vide.
    [Theory]
    [InlineData("", EtatRelu.RevenuAvantApplication)]
    [InlineData("?:\\pagefile.sys", EtatRelu.DifferentDeAvant)]
    public void Une_chaine_multiple_se_relit(string courante, EtatRelu attendu)
    {
        var avant = new SetRegistryValueOp(RegistryHiveKind.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles",
            RegistryValueType.MultiString, "");

        TweakReadback.Relire(Snapshot(avant), new LecteurDe(new() { ["PagingFiles"] = courante }))
            .Etat.Should().Be(attendu);
    }

    [Fact]
    public void Une_op_qui_ne_touche_pas_le_registre_se_dit_NON_RELISABLE()
    {
        var plan = new SetActivePowerPlanOp(Guid.NewGuid());

        TweakReadback.Relire(Snapshot(plan), new LecteurFictif(0))
            .Etat.Should().Be(EtatRelu.NonRelisable,
                "un levier qu'on ne sait pas relire doit le DIRE, pas passer pour applique");
    }

    [Fact]
    public void Un_snapshot_vide_ou_absent_ne_fait_pas_planter_la_relecture()
    {
        TweakReadback.Relire(null, new LecteurFictif(0)).Etat.Should().Be(EtatRelu.NonRelisable);
        TweakReadback.Relire(new TweakSnapshot("x", ""), new LecteurFictif(0))
            .Etat.Should().Be(EtatRelu.NonRelisable);
    }
}
