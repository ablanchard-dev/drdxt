using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Application.Tweaks.Latency;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// =======================================================================
// 19/09/2026. Un banc qui aplatit ce qu'il mesure rend un vert qui ne prouve rien.
//
// La premiere phrase du README promet : « il n'applique jamais un levier qu'il ne
// peut pas defaire ». Le gate qui la tient est `RevertRestoresStateTests` : appliquer,
// annuler, et comparer la machine octet pour octet a son etat d'avant.
//
// Deux trous, mesures aujourd'hui, et le second est le pire :
//
// 1. Le banc construit sa propre liste de leviers -- huit, ecrits a la main. Le produit
//    en enregistre QUINZE. Sept leviers n'ont jamais traverse le gate, et rien ne
//    comparait les deux listes : un levier ajoute demain nait hors du gate, en silence.
//
// 2. `FakeElevatedClient` traduisait toute ecriture registre par `ParseDword(r.Value)`.
//    Une valeur CHAINE ne se parse pas : elle devenait le DWORD **0**. Et en face,
//    `FakeRegistryReader.ReadString` rendait toujours `Ok(null)`, « absente ».
//    Consequence, pour un levier qui ecrit une chaine (`latency.vrrFlipModelOn`,
//    `fixit.sane_pagefile`) : la capture d'avant lit « absent », l'inverse devient donc
//    une SUPPRESSION, l'apply ecrit un zero, et la suppression rend bien la machine
//    identique. Le gate serait passe au VERT sans jamais avoir tenu une chaine.
//
// Ce que ce fichier prouve : le banc relit ce qu'il ecrit, et un levier qui ecrase une
// chaine EXISTANTE la remet telle quelle.
// =======================================================================
public sealed class UnBancQuiAPLATITNeProuveRienTests
{
    private const string Prefs = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string Reglages = "DirectXUserGlobalSettings";

    // Une valeur ecrite par WINDOWS, pas par nous : AutoHDR pose par le panneau graphique.
    // C'est elle que l'annulation doit rendre intacte.
    private const string DejaLa = "AutoHDREnable=2097;";

    [Fact]
    public async Task Le_banc_relit_la_CHAINE_qu_il_vient_d_ecrire()
    {
        var etat = new FakeSystemState();
        var client = new FakeElevatedClient(etat);
        var lecteur = new FakeRegistryReader(etat);

        var ecrit = await client.ExecuteAsync(
            new SetRegistryValueOp(RegistryHiveKind.CurrentUser, Prefs, Reglages,
                                   RegistryValueType.String, DejaLa), default);

        ecrit.IsOk.Should().BeTrue();
        lecteur.ReadString(RegistryHiveKind.CurrentUser, Prefs, Reglages).Value
            .Should().Be(DejaLa,
                "un banc qui rend « absent » ce qu'il vient d'ecrire fabrique des inverses "
                + "de suppression, et la suppression restaure toujours : le vert ne dit rien");
    }

    [Fact]
    public async Task Le_banc_relit_la_MULTI_CHAINE_qu_il_vient_d_ecrire()
    {
        var etat = new FakeSystemState();
        var client = new FakeElevatedClient(etat);
        var lecteur = new FakeRegistryReader(etat);
        const string pagefile = "C:\\pagefile.sys 0 0";

        await client.ExecuteAsync(
            new SetRegistryValueOp(RegistryHiveKind.LocalMachine, "Mem", "PagingFiles",
                                   RegistryValueType.MultiString, pagefile), default);

        lecteur.ReadMultiString(RegistryHiveKind.LocalMachine, "Mem", "PagingFiles").Value
            .Should().Be(pagefile);
    }

    [Fact]
    public async Task Une_chaine_n_est_pas_rangee_comme_un_zero()
    {
        // Le temoin du defaut lui-meme : `ParseDword("AutoHDREnable=2097;")` rendait 0.
        var etat = new FakeSystemState();
        var client = new FakeElevatedClient(etat);

        await client.ExecuteAsync(
            new SetRegistryValueOp(RegistryHiveKind.CurrentUser, Prefs, Reglages,
                                   RegistryValueType.String, DejaLa), default);

        etat.Snapshot().Should().Contain(DejaLa,
            "l'empreinte est ce que le gate compare : ce qu'elle n'ecrit pas, il ne le voit pas");
        etat.ReadRegistry(RegistryHiveKind.CurrentUser, Prefs, Reglages)
            .Should().BeNull("une chaine n'est pas un DWORD : la lire comme tel serait mentir deux fois");
    }

    [Fact]
    public async Task Un_levier_qui_ECRASE_une_chaine_existante_la_remet_telle_quelle()
    {
        // Le scenario qui compte : la valeur existe avant nous. Avec un banc qui aplatit,
        // la capture lisait « absent », l'inverse etait une suppression -- et l'annulation
        // effacait un reglage pose par Windows. Byte-identique ? Non : perdu.
        var etat = new FakeSystemState();
        etat.SetRegistryValue(RegistryHiveKind.CurrentUser, Prefs, Reglages,
                              RegistryValueType.String, DejaLa);
        var client = new FakeElevatedClient(etat);
        var levier = new VrrFlipModelOnTweak(new FakeRegistryReader(etat), client);

        var avant = etat.Snapshot();
        var applique = await levier.ApplyAsync();
        applique.IsOk.Should().BeTrue(applique.Error?.Message);

        etat.Snapshot().Should().NotBe(avant, "sans ecriture reelle, l'annulation ne prouve rien");
        etat.ReadRegistryText(RegistryHiveKind.CurrentUser, Prefs, Reglages)
            .Should().Contain("AutoHDREnable=2097", "le reglage d'a cote survit a notre passage");

        var annule = await levier.RevertAsync(applique.Value!);

        annule.IsOk.Should().BeTrue(annule.Error?.Message);
        etat.Snapshot().Should().Be(avant);
    }
}
