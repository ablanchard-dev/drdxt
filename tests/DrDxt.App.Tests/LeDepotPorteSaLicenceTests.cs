using System.IO;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Décision produit : « gratuit, GPL ». Sans fichier LICENSE, un code publié n'est
// pas libre : tous les droits restent réservés, quoi qu'en dise un README. Le texte est celui de
// gnu.org (gpl-3.0.txt, SHA-256 3972dc97...), jamais réécrit à la main.
// -----------------------------------------------------------------------
public sealed class LeDepotPorteSaLicenceTests
{
    [Fact]
    public void La_racine_porte_le_texte_de_la_GPL_3()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var licence = Path.Combine(d!.FullName, "LICENSE");

        File.Exists(licence).Should().BeTrue("sans LICENSE, le dépôt publié n'est pas sous GPL");
        var texte = File.ReadAllText(licence);
        texte.Should().Contain("GNU GENERAL PUBLIC LICENSE").And.Contain("Version 3, 29 June 2007");
        texte.Length.Should().BeGreaterThan(30000, "le texte complet, pas un résumé");
    }

    [Fact]
    public void Le_programme_livre_porte_sa_licence_a_cote_de_lui()
    {
        // La licence était à la racine du dépôt, mais ni le build publié ni l'installeur
        // ne la contenaient (l'installeur copie `publish\*`, le paquet de Downloads avait 292
        // fichiers, aucun LICENSE). La GPL-3.0 (§4) veut que chaque copie livrée soit accompagnée
        // du texte. Le fichier suit maintenant la sortie de l'application : ce test lit le dossier
        // de sortie réel, qui reçoit ce que le projet de l'application y copie.
        var livree = Path.Combine(AppContext.BaseDirectory, "LICENSE");

        File.Exists(livree).Should().BeTrue("la sortie de l'application doit porter le texte de la GPL");
        File.ReadAllText(livree).Should().Contain("GNU GENERAL PUBLIC LICENSE").And.Contain("Version 3, 29 June 2007");
    }
}
