using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using DrDxt.Application.Parties;
using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.Detection.Repair;

/// <summary>
/// 07/10/2026 (D2, enquête #21) : les plantages et blocages des jeux, lus dans le journal Application, sans droits.
///
/// Sources : Microsoft Learn, « The application or service crashing behavior troubleshooting guidance » (maj 12/02/2026) :
/// l'événement 1000 « Application Error » est le plantage lui-même, avec le module fautif et le code d'exception ; ntdll,
/// kernel32 et kernelbase y sont souvent nommés en victimes d'une corruption faite par un autre module. Les positions des
/// champs viennent du manifeste des fournisseurs « Application Error » (1000 : 0 programme, 3 module, 6 code, 11 chemin du
/// module) et « Application Hang » (1002 : 0 programme), lu sur la machine de référence le 07/10/2026 : le message est
/// traduit, les positions non. Le pilote graphique se reconnaît sans table de noms : Microsoft Learn (« Adding User-Mode
/// Display Driver Names to the Registry ») dit que la carte déclare ses fichiers en mode utilisateur dans
/// <c>UserModeDriverName</c> ; le dossier de ce fichier, dans le magasin des pilotes, porte le nom du paquet
/// (<c>nvmdi.inf_amd64_…</c>), le même d'une version à l'autre au suffixe près. Le module fautif nvwgf2umx.dll du 09/07 (plantage de dwm.exe) vivait
/// dans un paquet nvmdi.inf_amd64 depuis remplacé : c'est le préfixe qui le relie à la carte, pas le dossier exact.
///
/// Les incidents de la carte vus par le noyau (LiveKernelEvent 141, rapports WER) sont lus par
/// <see cref="Hardware.WerLiveKernelReportLog"/>, pas ici. Lecture seule ; aucun chemin dans le texte du constat.
/// </summary>
public static class PlantagesDeJeu
{
    /// <summary>Un plantage (1000) ou un blocage (1002). <paramref name="Paquet"/> : le dossier du module fautif quand il
    /// est dans le magasin des pilotes, son nom seul.</summary>
    /// <param name="Chemin">08/10/2026 : le chemin complet du programme (1000 : propriété 10 ; 1002 : propriété 5, relevés sur
    /// le PC du mainteneur).</param>
    public sealed record Evenement(DateTimeOffset Quand, string Exe, bool Blocage, string? Module = null, string? Code = null,
        string? Paquet = null, string? Chemin = null);

    /// <summary>Le paquet de pilote d'une carte graphique, sans son suffixe de version (« nvmdi.inf_amd64 »).</summary>
    public sealed record PaquetGraphique(string Prefixe, string Fabricant);

    /// <summary>Choix non chiffré, aligné sur la fenêtre des échecs de services.</summary>
    public static readonly TimeSpan Fenetre = TimeSpan.FromDays(30);
    private const int Seuil = 3;
    private const int MaxEvenements = 2000;

    // Microsoft Learn, page citée plus haut : les modules de Windows le plus souvent victimes.
    private static readonly HashSet<string> Victimes = new(StringComparer.OrdinalIgnoreCase)
        { "ntdll.dll", "kernel32.dll", "kernelbase.dll" };

    private static readonly HashSet<string> DirectX = new(StringComparer.OrdinalIgnoreCase)
        { "d3d9.dll", "d3d11.dll", "d3d12.dll", "d3d12core.dll", "dxgi.dll" };

    // [MS-ERREF] 2.3.1 (valeurs NTSTATUS) et la page de Microsoft Learn citée plus haut.
    private static readonly Dictionary<string, string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c0000005"] = "accès mémoire interdit (STATUS_ACCESS_VIOLATION)",
        ["c0000022"] = "accès refusé (STATUS_ACCESS_DENIED)",
        ["c0000374"] = "tas mémoire corrompu (STATUS_HEAP_CORRUPTION)",
        ["c0000409"] = "dépassement de tampon détecté (STATUS_STACK_BUFFER_OVERRUN)",
    };

    /// <summary>Le vrai, en lecture seule : le journal, les parties relevées et le jeu retenu pour le ping, la carte.</summary>
    public static IEnumerable<RepairFinding> DuPc()
    {
        var maintenant = DateTimeOffset.Now;
        var jeux = PartiesMemorisees.ParDefaut().Lire().Select(p => p.Jeu)
            .Append(DrDxt.Application.Network.PlafondEnvoiMemorise.ParDefaut().Lire()?.Exe)
            .Where(NomExeDeJeu.EstUnJeuPossible).Select(e => e!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 08/10/2026 (D2) : un jeu jamais relevé compte aussi quand son programme est dans un dossier de jeux installé
        // (Steam, Epic, Battle.net, Riot, EA app, Ubisoft Connect).
        var dossiers = DrDxt.Detection.Hardware.DossiersDeJeux.Lire()?.Select(d => d.Chemin).ToList();
        return Juger(Lire(maintenant - Fenetre), jeux, LirePaquetsGraphiques(),
            JournalDesChangements.ParDefaut().Lire(), maintenant, dossiers);
    }

    /// <summary><c>null</c> quand le journal n'a pas pu être lu ; une liste vide veut dire « lu, rien trouvé ».</summary>
    public static IReadOnlyList<Evenement>? Lire(DateTimeOffset depuis)
    {
        var lus = new List<Evenement>();
        try
        {
            var borne = depuis.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
            var requete = new EventLogQuery("Application", PathType.LogName,
                "*[System[((Provider[@Name='Application Error'] and EventID=1000) or (Provider[@Name='Application Hang'] and EventID=1002))"
                + $" and TimeCreated[@SystemTime>='{borne}']]]") { ReverseDirection = true };
            using var lecteur = new EventLogReader(requete);
            for (var e = lecteur.ReadEvent(); e is not null && lus.Count < MaxEvenements; e = lecteur.ReadEvent())
                using (e)
                {
                    string? P(int i) => e.Properties.Count > i ? e.Properties[i].Value?.ToString() : null;
                    if (e.TimeCreated is not { } t || P(0) is not { Length: > 0 } exe) continue;
                    if (e.Id == 1002) { lus.Add(new(new DateTimeOffset(t), exe, true, Chemin: P(5))); continue; }
                    var dossier = P(11) is { Length: > 0 } chemin ? Path.GetDirectoryName(chemin) : null;
                    var paquet = dossier is not null
                        && string.Equals(Path.GetFileName(Path.GetDirectoryName(dossier)), "FileRepository", StringComparison.OrdinalIgnoreCase)
                        ? Path.GetFileName(dossier) : null;
                    lus.Add(new(new DateTimeOffset(t), exe, false, P(3), P(6), paquet, P(10)));
                }
        }
        catch (Exception) { return null; }
        return lus;
    }

    /// <summary>Les paquets de pilote des cartes graphiques déclarées. Une lecture ratée rend une liste vide : le module
    /// n'est alors pas reconnu, jamais attribué au hasard.</summary>
    public static IReadOnlyList<PaquetGraphique> LirePaquetsGraphiques()
    {
        var paquets = new List<PaquetGraphique>();
        try
        {
            using var classe = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var nom in classe?.GetSubKeyNames() ?? [])
            {
                try
                {
                    using var carte = classe!.OpenSubKey(nom);
                    if (carte is null) continue;
                    var fabricant = carte.GetValue("ProviderName") as string ?? "";
                    foreach (var valeur in new[] { "UserModeDriverName", "UserModeDriverNameWow" })
                    {
                        var fichiers = carte.GetValue(valeur) switch { string[] a => a, string s => [s], _ => [] };
                        foreach (var f in fichiers)
                            if (Prefixe(Path.GetFileName(Path.GetDirectoryName(f))) is { } p)
                                paquets.Add(new(p, fabricant));
                    }
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return paquets.Distinct().ToList();
    }

    // « nvmdi.inf_amd64_3990df11758b8744 » devient « nvmdi.inf_amd64 » : le suffixe change à chaque version.
    internal static string? Prefixe(string? dossier)
    {
        var i = dossier?.LastIndexOf('_') ?? -1;
        return i > 0 ? dossier![..i] : null;
    }

    /// <summary>Ce que désigne le module fautif, quand c'est établi ; sinon il est dit non classé.</summary>
    internal static string Designation(Evenement e, IReadOnlyList<PaquetGraphique> paquets)
    {
        if (e.Module is not { Length: > 0 } m || m.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return "Windows n'a pas nommé le module fautif";
        if (m.Equals(e.Exe, StringComparison.OrdinalIgnoreCase))
            return "le jeu lui-même : l'erreur est dans son propre code, c'est à son éditeur de la corriger";
        if (e.Paquet is { } paquet)
        {
            var carte = paquets.FirstOrDefault(p => Prefixe(paquet) is { } x && x.Equals(p.Prefixe, StringComparison.OrdinalIgnoreCase));
            return carte is not null
                ? $"le pilote graphique{(carte.Fabricant.Length > 0 ? " " + carte.Fabricant : "")} (un fichier du paquet de pilote de la carte, "
                  + "dans la version installée à ce moment-là)"
                // Revue F-35 : sans liste des cartes lue, « pas une carte graphique » serait un négatif affirmé sur rien.
                : paquets.Count == 0
                    ? "un fichier de pilote du magasin de Windows ; les pilotes des cartes graphiques n'ont pas pu être lus, la carte n'est donc pas vérifiée"
                    : "un fichier de pilote du magasin de Windows, qui n'est pas celui d'une carte graphique déclarée";
        }
        if (DirectX.Contains(m)) return "DirectX, le moteur graphique de Windows";
        if (Victimes.Contains(m))
            return "un module de Windows que Microsoft dit souvent victime d'une corruption faite par un autre module, rarement la cause";
        return "un module que DrDXT ne classe pas : rien n'est affirmé sur ce qu'il est";
    }

    /// <summary>Pur : les événements lus, les jeux connus, les paquets graphiques, les changements datés et l'heure.</summary>
    /// <param name="dossiersDeJeux">08/10/2026 : les dossiers des jeux installés ; un programme qui y vit est un jeu, même
    /// jamais relevé en partie.</param>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<Evenement>? lus, IReadOnlySet<string> jeux,
        IReadOnlyList<PaquetGraphique> paquets, IReadOnlyList<Changement> changements, DateTimeOffset maintenant,
        IReadOnlyList<string>? dossiersDeJeux = null)
    {
        var dossiers = (dossiersDeJeux ?? []).Where(d => d.Length > 3)
            .Select(d => d.TrimEnd('\\') + "\\").ToList();
        // Revue n° 11 : une racine de bibliothèque Steam contient Steam lui-même (steamwebhelper.exe plante souvent) :
        // le programme doit aussi pouvoir être un jeu (NomExeDeJeu écarte lanceurs, navigateurs intégrés, outils).
        bool DansUnDossierDeJeux(Evenement e) => e.Chemin is { } c
            && NomExeDeJeu.EstUnJeuPossible(e.Exe)
            && dossiers.Any(d => c.StartsWith(d, StringComparison.OrdinalIgnoreCase));
        const string Nom = "plantages des jeux";
        var jours = Fenetre.TotalDays.ToString("0", CultureInfo.InvariantCulture);
        if (lus is null)
        {
            yield return new RepairFinding("STABILITE", Nom, null, null, RepairStatus.Unknown,
                "le journal Application n'a pas pu être lu : « non lu » n'est pas « aucun plantage ».");
            yield break;
        }
        if (jeux.Count == 0 && dossiers.Count == 0)
        {
            yield return new RepairFinding("STABILITE", Nom, "aucun jeu connu", null, RepairStatus.Observed,
                "aucune partie relevée : DrDXT ne sait pas encore quels programmes sont tes jeux, il ne compte donc aucun "
                + "plantage de jeu. Ce n'est pas « aucun plantage ».");
            yield break;
        }

        var depuis = maintenant - Fenetre;
        var miens = lus.Where(e => e.Quand >= depuis && e.Quand <= maintenant && (jeux.Contains(e.Exe) || DansUnDossierDeJeux(e)))
            .OrderByDescending(e => e.Quand).ToList();
        var noms = jeux.Count > 0
            ? string.Join(", ", jeux.Order(StringComparer.OrdinalIgnoreCase))
                + (dossiers.Count > 0 ? $" et des {dossiers.Count} dossier(s) de jeux installés" : "")
            : $"des programmes des {dossiers.Count} dossier(s) de jeux installés";
        if (miens.Count == 0)
        {
            yield return new RepairFinding("STABILITE", Nom, $"aucun en {jours} jours", "aucun", RepairStatus.ExpectedDefault,
                $"le journal Application ne note aucun plantage ni blocage de {noms} sur {jours} jours.");
            yield break;
        }

        static string D(DateTimeOffset d) => d.ToLocalTime().ToString("dd/MM", CultureInfo.InvariantCulture);
        var plantages = miens.Where(e => !e.Blocage).ToList();
        var blocages = miens.Count - plantages.Count;
        var morceaux = new List<string>
        {
            string.Join(" ; ", miens.GroupBy(e => e.Exe, StringComparer.OrdinalIgnoreCase).Select(g =>
                $"{g.Key} : {g.Count(e => !e.Blocage)} plantage(s), {g.Count(e => e.Blocage)} blocage(s), le dernier le {D(g.First().Quand)}")) + ".",
        };

        if (plantages.Count > 0)
        {
            var module = plantages.GroupBy(e => e.Module ?? "unknown", StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenByDescending(g => g.First().Quand).First();
            morceaux.Add($"Module fautif le plus fréquent : {module.Key} ({module.Count()} sur {plantages.Count}), "
                         + Designation(module.First(), paquets) + ".");
            var code = plantages.Where(e => e.Code is not null).GroupBy(e => e.Code!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).FirstOrDefault();
            if (code is not null)
                morceaux.Add($"Code d'exception le plus fréquent : 0x{code.Key.ToLowerInvariant()}"
                             + (Codes.TryGetValue(code.Key, out var sens) ? $", {sens}" : "") + ".");
        }

        // Le changement daté le plus récent suivi d'au moins un incident : une corrélation de dates, dite comme telle.
        var lien = changements.Where(c => c.Cle?.StartsWith("pose:", StringComparison.Ordinal) != true && c.Quand >= depuis && c.Quand <= maintenant)
            .OrderByDescending(c => c.Quand)
            .Select(c => (c, apres: miens.Count(e => e.Quand > c.Quand), avant: miens.Count(e => e.Quand <= c.Quand)))
            .FirstOrDefault(x => x.apres > 0);
        if (lien.c is not null)
            morceaux.Add($"{lien.apres} de ces incidents depuis {lien.c.Libelle} ({D(lien.c.Quand)}), {lien.avant} avant dans la "
                         + "fenêtre : c'est une corrélation de dates, pas une cause prouvée.");

        var repete = miens.Count >= Seuil;
        yield return new RepairFinding("STABILITE", Nom, $"{plantages.Count} plantage(s), {blocages} blocage(s) en {jours} jours", "aucun",
            repete ? RepairStatus.Suspicious : RepairStatus.Observed,
            string.Join(" ", morceaux),
            repete ? RepairAction.Recommandation : RepairAction.Aucune);
    }
}
