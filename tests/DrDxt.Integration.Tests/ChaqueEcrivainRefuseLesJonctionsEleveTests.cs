using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// Élevé, un processus de DrDXT n'écrit, ne copie ni ne supprime à travers une jonction d'un dossier du compte : une seule règle,
// FichierJsonAtomique.RefusEleve (que l'écriture atomique applique d'elle-même). Un écrivain direct nouveau l'applique, ou dit ici
// pourquoi il n'en a pas besoin ; une exemption qui ne correspond plus à rien tombe aussi.
public sealed class ChaqueEcrivainRefuseLesJonctionsEleveTests
{
    private static readonly Regex EcritureDirecte = new(
        @"File\.(Delete|Copy|Move|WriteAllText|WriteAllBytes|AppendAllText|Create)\(|Directory\.Delete\(|new FileStream\(|CreateFileW\(");

    private static readonly Dictionary<string, string> Exemptes = new(StringComparer.Ordinal)
    {
        ["App.xaml.cs"] = "ses écritures directes sont des harnais de capture, en DEBUG seulement",
        ["CorruptionDuMagasin.cs"] = "lecture seule",
        ["DossierDeLAppEleve.cs"] = "contrôle et tient lui-même le dossier des données, sans suivre les liens",
        ["EcritureDansDocuments.cs"] = "refuse elle-même un lien sur Documents et chaque parent, handles tenus jusqu'à l'écriture",
        ["InstallationMiseAJour.cs"] = "refuse de tourner élevée",
        ["ObsStudio.cs"] = "lecture seule",
        ["FichierTiers.cs"] = "lecture seule",
        ["FrequenceSouris.cs"] = "handle de périphérique, aucun fichier",
        ["PeripheriquesDEntree.cs"] = "handle de périphérique, aucun fichier",
        ["SanteNvme.cs"] = "handle de disque, en lecture",
        ["Natif.cs"] = "déclaration de CreateFileW",
        ["PaquetDeSupport.cs"] = "écrit là où le joueur le choisit, dans la boîte d'enregistrement",
        ["Win32PnpActions.cs"] = "helper : copie des pilotes retirés dans la racine de confiance",
        ["CallerImageIdentity.cs"] = "lecture de l'image de l'appelant",
        ["JournalDEcritureAnticipee.cs"] = "racine de confiance ; le journal du compte s'écrit par FichierJsonAtomique",
        ["ModeDesinstallation.cs"] = "racine de confiance",
        ["MachineManifestKey.cs"] = "racine de confiance",
        ["MachineManifestCommit.cs"] = "verrou dans la racine de confiance, durcie avant son ouverture",
        ["JsonReconcilerStateStore.cs"] = "verrou voisin du sidecar, dans la racine de confiance durcie avant son ouverture",
        ["TrustedManifestRoot.cs"] = "racine de confiance, réparée avant toute écriture",
        ["PreparationDesDroits.cs"] = "juge et écarte les points d'analyse elle-même ; elle n'écrit que sur un objet dont elle a vérifié qu'il n'en est pas un, et renomme (sans suivre) ce qui ne l'est pas",
        ["PauseDuMaintien.cs"] = "racine de confiance",
        ["PlafondEnvoiDuService.cs"] = "racine de confiance",
        ["TemoinDIncidentWorker.cs"] = "racine de confiance",
    };

    private static List<string> EcrivainsDirects()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return FichiersDuDepot.Sous(Path.Combine(d!.FullName, "src"), "*.cs")
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => EcritureDirecte.IsMatch(File.ReadAllText(f)))
            .ToList();
    }

    [Fact]
    public void Chaque_ecrivain_direct_refuse_les_jonctions_eleve_ou_dit_pourquoi()
    {
        var ecrivains = EcrivainsDirects();
        ecrivains.Should().HaveCountGreaterThan(10, "témoin : le crible voit les écrivains directs");

        ecrivains.Where(f => !Exemptes.ContainsKey(Path.GetFileName(f)) && !File.ReadAllText(f).Contains("RefusEleve(", StringComparison.Ordinal))
            .Select(Path.GetFileName).Should().BeEmpty();
    }

    [Fact]
    public void Aucune_exemption_ne_survit_a_son_ecrivain()
    {
        var noms = EcrivainsDirects().Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);

        Exemptes.Keys.Where(n => !noms.Contains(n)).Should().BeEmpty();
    }
}
