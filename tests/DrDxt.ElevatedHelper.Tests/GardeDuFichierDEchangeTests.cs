using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;
using Microsoft.Win32;

namespace DrDxt.ElevatedHelper.Tests;

// PagingFiles vide = plus de fichier d'échange au redémarrage. Un appelant compromis, ou un journal d'annulation forgé, le
// passait par l'allowlist. La mémoire de Windows et la note du helper vivent ici sous HKCU.
public sealed class GardeDuFichierDEchangeTests : IDisposable
{
    private readonly string _racine = @"Software\DrDXT-Tests\" + Guid.NewGuid().ToString("N");
    public void Dispose() { try { Registry.CurrentUser.DeleteSubKeyTree(_racine, false); } catch { } }

    private GardeDuFichierDEchange Garde(Func<char, bool>? fixe = null)
        => new(Registry.CurrentUser, _racine + @"\Memoire", _racine + @"\Helper", fixe ?? (l => l is 'C' or 'D'));

    private void Actuel(params string[] entrees)
    {
        using var cle = Registry.CurrentUser.CreateSubKey(_racine + @"\Memoire");
        cle.SetValue(GardeDuFichierDEchange.Nom, entrees, RegistryValueKind.MultiString);
    }

    // Ecrit comme l'exécutant : la garde avant, l'écriture, la garde après.
    private bool Ecrire(GardeDuFichierDEchange g, string valeur)
    {
        if (g.Avant(valeur) is not null) return false;
        Actuel(valeur.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        g.Apres(valeur);
        return true;
    }

    [Fact]
    public void Un_vide_force_sur_un_fichier_d_echange_en_place_est_refuse()
    {
        Actuel(@"?:\pagefile.sys");

        Garde().Avant("").Should().NotBeNull();
    }

    [Fact]
    public void Le_vide_d_origine_se_rend_meme_apres_un_redemarrage()
    {
        Actuel();   // fichier d'échange coupé : fixit.sane_pagefile le rend à Windows
        Ecrire(Garde(), @"?:\pagefile.sys").Should().BeTrue();

        Ecrire(Garde(), "").Should().BeTrue("l'Annuler rend l'origine, une autre instance après redémarrage");
        Garde().Avant("").Should().BeNull("toujours vide : rien ne change");
        Ecrire(Garde(), @"?:\pagefile.sys").Should().BeTrue();
    }

    [Fact]
    public void La_note_est_consommee_au_rendu()
    {
        Actuel();
        Ecrire(Garde(), @"?:\pagefile.sys");
        Ecrire(Garde(), "");
        Actuel(@"C:\pagefile.sys 0 0");   // le joueur le rallume lui-même

        Garde().Avant("").Should().NotBeNull("la note du vide d'origine a servi une fois");
    }

    // La note restait tant que le vide n'était pas rendu : le joueur réglait lui-même son fichier d'échange, et un appelant
    // compromis pouvait encore le couper. La note garde la valeur posée par DrDXT : changée, le vide est refusé.
    [Fact]
    public void Le_joueur_change_le_fichier_d_echange_le_vide_est_refuse()
    {
        Actuel();
        Ecrire(Garde(), @"?:\pagefile.sys").Should().BeTrue();
        Actuel(@"C:\pagefile.sys 4096 8192");   // réglé par le joueur dans Windows

        Garde().Avant("").Should().NotBeNull();
        Actuel(@"?:\pagefile.sys");             // revenu ensuite à la valeur de DrDXT : la note est partie
        Garde().Avant("").Should().NotBeNull();
    }

    [Fact]
    public void Un_lecteur_qui_n_est_pas_fixe_ou_n_existe_pas_est_refuse()
    {
        Actuel(@"D:\pagefile.sys 0 0");

        Garde().Avant(@"Z:\pagefile.sys 0 0").Should().NotBeNull();
        Garde().Avant(@"C:\pagefile.sys 0 0").Should().BeNull("le levier légitime passe toujours");
        Garde().Avant(@"?:\pagefile.sys").Should().BeNull();
    }

    [Fact]
    public void Seule_la_valeur_de_la_memoire_de_windows_est_gardee()
    {
        GardeDuFichierDEchange.Vise(DrDxt.Core.RegistryHiveKind.LocalMachine, GardeDuFichierDEchange.CheminMemoire, "PagingFiles").Should().BeTrue();
        GardeDuFichierDEchange.Vise(DrDxt.Core.RegistryHiveKind.LocalMachine, GardeDuFichierDEchange.CheminMemoire, "ClearPageFileAtShutdown").Should().BeFalse();
    }

    // Les trois exécutants passent par Win32RegistryActions.SetValue : la garde y est, avant l'écriture.
    [Fact]
    public void L_ecrivain_du_registre_passe_par_la_garde()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var s = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.ElevatedHelper", "Execution", "Win32RegistryActions.cs"));
        var set = s[s.IndexOf("public Result SetValue(", StringComparison.Ordinal)..];
        set = set[..set.IndexOf("private static Result Ecrire(", StringComparison.Ordinal)];

        set.Should().Contain("_fichierDEchange.Avant(value)").And.Contain("_fichierDEchange.Apres(value)");
        set.IndexOf("_fichierDEchange.Avant(value)", StringComparison.Ordinal)
            .Should().BeLessThan(set.IndexOf("var r = Ecrire(", StringComparison.Ordinal));
    }
}
