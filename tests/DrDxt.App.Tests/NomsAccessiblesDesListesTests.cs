using System.Collections.Generic;
using System.Linq;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Un élément de liste sans nom accessible est annoncé par le ToString de sa donnée. Celui d'un record énumère ses champs
// (« DomaineMachine { Cle = bios, … System.Collections.Generic.List`1… } »), et le lecteur d'écran le lirait pour chaque
// page du rail. Chaque donnée de liste dit son nom.
public sealed class NomsAccessiblesDesListesTests
{
    [Fact]
    public void Les_pages_les_satellites_et_les_points_a_faire_se_nomment_comme_a_l_ecran()
    {
        var domaines = CarteDeLaMachine.Construire([], new Dictionary<string, IReadOnlyList<string>>(), [], k => k);
        domaines.Should().NotBeEmpty("témoin : la carte a des pages");
        var bios = domaines.Single(d => d.Cle == "bios");
        bios.ToString().Should().Be($"{bios.Nom}, {bios.EtatLibelle}");

        var entree = new EntreeCarte("bios", Severity.Major, "Profil XMP désactivé", "", "", "", NonLu: false);
        new PointAFaire(bios, entree).ToString().Should().Be($"{bios.Nom} : Profil XMP désactivé");
        entree.ToString().Should().Be("Profil XMP désactivé");
        new LigneMesure("Fichier d'échange : géré par Windows", "explication.").ToString().Should().Be("Fichier d'échange : géré par Windows");
        (entree with { Valeur = "désactivé" }).ToString().Should().Be("Profil XMP désactivé : désactivé");
        new SatelliteDomaine(new ComponentItem("Cpu", "Processeur", "i5-12400F"), bios).ToString()
            .Should().Be($"Processeur : i5-12400F, {bios.EtatLibelle}");
        new SatelliteDomaine(new ComponentItem("Cpu", "Processeur", "i5-12400F"), null).ToString().Should().Be("Processeur : i5-12400F");

        new LigneLevier("Plan d'alimentation", "déjà bon", null, default).ToString().Should().Be("Plan d'alimentation : déjà bon");
        new ComponentItem("Ram", "Mémoire", "2 modules").ToString().Should().Be("Mémoire : 2 modules");
        new LigneEnAttente("Pilote 617.42", "le 15/10", "pas encore joué").ToString().Should().Be("Pilote 617.42, le 15/10, pas encore joué");
        new ScanViewModel.ChoixScene(default, "Partie classée").ToString().Should().Be("Partie classée");
        new ScanViewModel.LevierPose("os.hags", "HAGS", "HAGS, posé").ToString().Should().Be("HAGS, posé");
        new DrDxt.Application.Parties.EvenementDeFrise(DateTimeOffset.Now, DrDxt.Application.Parties.GenreDEvenement.Partie, "cod.exe")
            { Detail = "141 FPS" }.ToString().Should().EndWith("cod.exe, 141 FPS").And.NotContain("{");

        foreach (var texte in domaines.Select(d => d.ToString()))
            texte.Should().NotContain("{").And.NotContain("System.");
    }
}
