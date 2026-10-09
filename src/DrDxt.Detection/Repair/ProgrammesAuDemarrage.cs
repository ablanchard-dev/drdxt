using System.Globalization;
using System.Text.RegularExpressions;
using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.Detection.Repair;

/// <summary>
/// 06/10/2026 (chantier B7) : ce qui se lance au démarrage de Windows, lu seulement : les clés <c>Run</c> (utilisateur
/// et machine, 64 et 32 bits) et les dossiers Démarrage, croisés avec <c>Explorer\StartupApproved</c>, où le Gestionnaire
/// des tâches note ce qu'on y désactive. Le format de cette valeur n'est pas documenté par Microsoft : premier octet pair =
/// activé, impair = désactivé (relevé le 06/10 sur 33 valeurs : 02 et 06 pour des programmes actifs, 03 pour ceux coupés).
/// Le coût vient des mesures de Windows (événement 101), quand il en a. DrDXT n'écrit rien ici, sauf son propre raccourci
/// du relevé des parties (J1b, ReleveAuDemarrage), que le joueur arrête par le bouton du relevé. C'est aussi le seul lecteur
/// de ces clés : l'inventaire (<see cref="InventaireMachine"/>) le lit (revue F-10).
/// </summary>
public static class ProgrammesAuDemarrage
{
    /// <param name="Commande">La commande de la clé Run, ou le chemin du fichier du dossier Démarrage.</param>
    /// <param name="DansRun">Vient d'une clé Run (et non d'un dossier Démarrage).</param>
    public sealed record Entree(string Nom, bool Actif, string Commande = "", bool DansRun = true);

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    // « MicrosoftEdgeAutoLaunch_E5F5B93C… » : le suffixe est un identifiant d'installation, pas un nom.
    private static readonly Regex Suffixe = new(@"_[0-9A-F]{16,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Ce qu'un joueur couperait par erreur : la sécurité de Windows n'est pas un programme de confort.
    private static string Garder(string nom) => nom.Equals("SecurityHealth", StringComparison.OrdinalIgnoreCase)
        ? " (l'icône de la Sécurité Windows, à garder)" : "";

    public static bool Actif(string nom, IReadOnlyDictionary<string, byte[]> approuves)
        => !approuves.TryGetValue(nom, out var v) || v.Length == 0 || (v[0] & 1) == 0;

    /// <summary>Revue F-9 : l'exécutable d'une commande Run, sans ses arguments, variables développées ; null si vide.
    /// Entre guillemets : jusqu'au guillemet ; sinon jusqu'à « .exe » ; sinon le premier mot.</summary>
    public static string? Cible(string commande)
    {
        var c = Environment.ExpandEnvironmentVariables(commande.Trim());
        if (c.Length == 0) return null;
        if (c[0] == '"') return c.IndexOf('"', 1) is var fin and > 0 ? c[1..fin] : c[1..];
        var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c[..(exe + 4)] : c.Split(' ')[0];
    }

    // Une cible sans dossier (« rundll32.exe ») se cherche dans le PATH de Windows : on ne la dit pas morte. Un fichier du
    // dossier Démarrage existe par définition, et son chemin n'est pas une commande (« Start Menu » a un espace sans
    // guillemets : relevé le 06/10, deux raccourcis dits morts à tort avant cette ligne).
    private static bool Morte(Entree e, Func<string, bool> existe)
        => e.DansRun && Cible(e.Commande) is { } cible && Path.IsPathRooted(cible) && !existe(cible);

    public static IEnumerable<RepairFinding> Juger(Result<IReadOnlyList<Entree>> lues, IReadOnlyList<DemarrageEtDisque.Ralentisseur> ralentisseurs,
        Func<string, bool>? existe = null)
    {
        if (!lues.IsOk)
        {
            yield return new RepairFinding("DEMARRAGE", "programmes au démarrage", null, null, RepairStatus.Unknown,
                "la liste des programmes lancés avec Windows n'a pas pu être lue : « non lu » n'est pas « aucun ».");
            yield break;
        }
        var entrees = lues.Value!;
        if (entrees.Count == 0) yield break;
        existe ??= File.Exists;
        var mortes = entrees.Where(e => e.Actif && Morte(e, existe)).ToList();
        var actifs = entrees.Where(e => e.Actif && !mortes.Contains(e)).Select(e => Suffixe.Replace(e.Nom, ""))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var coupes = entrees.Count(e => !e.Actif);
        string Cout(string nom) => ralentisseurs
            .Where(r => r.Nom.Contains(nom, StringComparison.OrdinalIgnoreCase) || nom.Contains(r.Nom, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.DegradationMs).DefaultIfEmpty(0).Max() is var ms and > 0
            ? string.Format(Fr, " (Windows a mesuré jusqu'à {0:0.0} s de retard)", ms / 1000.0) : "";

        var valeur = $"{actifs.Count} lancé{(actifs.Count > 1 ? "s" : "")}, {coupes} désactivé{(coupes > 1 ? "s" : "")}"
            + (mortes.Count > 0 ? $", {mortes.Count} entrée{(mortes.Count > 1 ? "s" : "")} morte{(mortes.Count > 1 ? "s" : "")}" : "");
        var morts = mortes.Count == 0 ? ""
            : " Ces entrées pointent vers un programme qui n'existe plus (désinstallé ?) et ne lancent rien : "
              + string.Join(", ", mortes.Select(e => e.Nom)) + ".";
        yield return actifs.Count == 0
            ? new RepairFinding("DEMARRAGE", "programmes au démarrage", valeur, null, RepairStatus.Observed,
                "Aucun programme ne se lance avec Windows." + morts)
            : new RepairFinding("DEMARRAGE", "programmes au démarrage", valeur, null, RepairStatus.Observed,
                "Se lancent avec Windows : " + string.Join(", ", actifs.Select(n => n + Garder(n) + Cout(n))) + "." + morts
                + " Chacun prend de la mémoire et parfois du processeur pendant que tu joues. Pour en couper un : Gestionnaire "
                + "des tâches (Ctrl + Maj + Échap) > Applications de démarrage. DrDXT n'y touche pas.",
                RepairAction.Recommandation);
    }

    private const string Approuve = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    /// <summary>Revue F-8 : tout ou rien. Une clé qui refuse la lecture rend un échec, jamais une liste partielle.</summary>
    public static Result<IReadOnlyList<Entree>> Lire()
    {
        try
        {
            var entrees = new List<Entree>();
            void Cles(RegistryKey racine, string run, string approuve)
            {
                using var r = racine.OpenSubKey(run);
                if (r is null) return;
                var ok = Approbations(racine, Approuve + approuve);
                entrees.AddRange(r.GetValueNames().Where(n => n.Length > 0)
                    .Select(n => new Entree(n, Actif(n, ok), (r.GetValue(n) as string ?? "").Trim())));
            }
            Cles(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run");
            Cles(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run");
            Cles(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32");

            void Dossier(Environment.SpecialFolder dossier, RegistryKey racine)
            {
                var chemin = Environment.GetFolderPath(dossier);
                if (!Directory.Exists(chemin)) return;
                var ok = Approbations(racine, Approuve + "StartupFolder");
                entrees.AddRange(Directory.EnumerateFiles(chemin)
                    .Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    .Select(f => new Entree(Path.GetFileNameWithoutExtension(f), Actif(Path.GetFileName(f), ok), f, DansRun: false)));
            }
            Dossier(Environment.SpecialFolder.Startup, Registry.CurrentUser);
            Dossier(Environment.SpecialFolder.CommonStartup, Registry.LocalMachine);
            return Result<IReadOnlyList<Entree>>.Ok(entrees);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<Entree>>.Fail(ErrorCodes.WmiFail, ex.Message);
        }
    }

    private static Dictionary<string, byte[]> Approbations(RegistryKey racine, string chemin)
    {
        using var k = racine.OpenSubKey(chemin);
        return k?.GetValueNames().Where(n => k.GetValue(n) is byte[]).ToDictionary(n => n, n => (byte[])k.GetValue(n)!,
            StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    }
}
