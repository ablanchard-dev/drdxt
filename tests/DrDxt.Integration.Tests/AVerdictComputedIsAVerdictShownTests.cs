using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;
using DrDxt.Tests.Shared;

namespace DrDxt.Integration.Tests;

/// <summary>
/// Le test qui manquait, et son histoire.
///
/// Le verdict de la boucle BIOS -- « on te dit quoi changer, et on prouve que ca a pris » -- etait
/// calcule a chaque scan, couvert par **17 tests de cablage**, et binde dans **zero
/// fichier .xaml**. Dix-sept tests verts pour zero pixel. Aucun d'eux ne pouvait le voir : ils
/// s'arretaient tous exactement la ou l'ecran commence.
///
/// Ce test ferme cette frontiere. Il ne juge pas l'apparence : il verifie qu'une propriete que le
/// VM expose pour etre MONTREE est effectivement liee quelque part dans une vue. C'est grossier,
/// et c'est precisement ce qui aurait fallu.
/// </summary>
public class AVerdictComputedIsAVerdictShownTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("DrDXT.sln introuvable depuis " + AppContext.BaseDirectory);
    }

    private static string AllXaml()
        => string.Concat(FichiersDuDepot.Sous(Path.Combine(RepoRoot(), "src"), "*.xaml")
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText));

    [Theory]
    // Le verdict du voyage BIOS, et sa carte de recuperation.
    [InlineData("HasBiosLoopResult")]
    [InlineData("BiosLoop.Outcomes")]
    [InlineData("BiosLoop.RecoveryCard")]
    // L'A/B memoire : les deux valeurs brutes, l'ecart, et la conclusion.
    [InlineData("HasMemoryAb")]
    [InlineData("MemoryAb.Lines")]
    [InlineData("MemoryAb.Conclusion")]
    // La phrase de session (dont le refus SceneContract) — calculee, jamais vue par ce gate.
    [InlineData("ResultatOptimisation")]
    // La scene declaree. Sans ces trois liaisons, la question existerait dans le VM et
    // le joueur ne pourrait jamais y repondre : le contrat resterait `Inconnue` pour toujours.
    [InlineData("{Binding ChoixDeScene}")]
    [InlineData("{Binding SceneDeclaree}")]
    [InlineData("{Binding DescriptionScene")]
    // Demande produit : ce qui tournait A cote du jeu, et la MARGE collee au FPS moyen.
    // Un scan a affiche 164 FPS pendant qu'il regardait l'ecran de DrDXT, jeu en arriere-plan.
    [InlineData("{Binding VoisinsDuJeu}")]
    [InlineData("{Binding VoisinsResume}")]
    [InlineData("{Binding VoisinsConnus")]
    [InlineData("{Binding AvgFpsAvecMarge}")]
    [InlineData("{Binding LabelConsigneScene}")]
    public void Ce_que_le_produit_calcule_pour_etre_montre_est_lie_dans_une_vue(string binding)
    {
        AllXaml().Should().Contain(binding,
            $"'{binding}' est calcule a chaque scan : s'il n'est binde nulle part, le client ne le voit jamais");
    }

    /// <summary>
    /// La consigne doit etre vue avant le scan, pas seulement a « OPTIMISER ».
    ///
    /// Le selecteur de scene pose la veille ne vivait que dans le panneau de RESULTAT : le joueur
    /// apprenait apres coup qu'il aurait fallu jouer une vraie partie pendant la mesure. Ce test
    /// verifie que la question de scene apparait aussi dans le panneau d'avant-scan — celui que
    /// l'ecran montre a l'etat Idle, la ou vit le bouton « lancer le scan ».
    /// Grossier, comme le reste de ce fichier : il lit le XAML. C'est ce qui manquait.
    /// </summary>
    [Fact]
    public void La_consigne_de_scene_est_montree_AVANT_le_scan()
    {
        var scan = VueScan.Texte(Path.Combine(RepoRoot(), "src", "DrDxt.App", "Views", "ScanView.xaml")).Replace("\r\n", "\n", StringComparison.Ordinal);

        var idle = scan.IndexOf("ConverterParameter='Idle,Scanning'}\">\n                                    <TextBlock Text=\"{Binding LabelIdleHeroBody}",
            StringComparison.Ordinal);
        if (idle < 0)
            idle = scan.IndexOf("{Binding LabelIdleHeroBody}", StringComparison.Ordinal);
        idle.Should().BeGreaterThan(0, "le panneau d'avant-scan doit exister");

        var bouton = scan.IndexOf("{Binding LabelScanButton}", idle, StringComparison.Ordinal);
        bouton.Should().BeGreaterThan(idle, "le bouton de lancement suit l'accroche d'avant-scan");

        var avantLeScan = scan[idle..bouton];
        avantLeScan.Should().Contain("{Binding LabelConsigneScene}",
            "la consigne (jouer une vraie partie / stand de tir, activer son compteur FPS) doit etre lue AVANT de lancer");
        avantLeScan.Should().Contain("{Binding SceneDeclaree}",
            "le contrat de scene se declare avant la mesure, pas apres");
    }
}
