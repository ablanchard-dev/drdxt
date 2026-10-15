using System.Globalization;
using System.Text.RegularExpressions;
using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Ce qui se lance au démarrage de Windows, lu seulement : les clés <c>Run</c> (utilisateur
/// et machine, 64 et 32 bits) et les dossiers Démarrage, croisés avec <c>Explorer\StartupApproved</c>, où le Gestionnaire
/// des tâches note ce qu'on y désactive. Le format de cette valeur n'est pas documenté par Microsoft : premier octet pair =
/// activé, impair = désactivé (relevé le 06/10 sur 33 valeurs : 02 et 06 pour des programmes actifs, 03 pour ceux coupés).
/// Le coût vient des mesures de Windows (événement 101), quand il en a. DrDXT n'écrit rien ici sans le clic du joueur, sauf son propre raccourci
/// du relevé des parties (J1b, ReleveAuDemarrage), que le joueur arrête par le bouton du relevé ; le geste de chaque entrée
/// (<see cref="Entree.Geste"/>) se pose par l'op SetStartupApprovalOp, sur le clic du joueur. C'est aussi le seul lecteur
/// de ces clés : l'inventaire (<see cref="InventaireMachine"/>) le lit.
/// </summary>
public static class ProgrammesAuDemarrage
{
    /// <param name="Commande">La commande de la clé Run, ou le chemin du fichier du dossier Démarrage.</param>
    /// <param name="DansRun">Vient d'une clé Run (et non d'un dossier Démarrage).</param>
    /// <param name="Ruche">La ruche de sa clé Run ou de son dossier Démarrage (utilisateur ou tous les utilisateurs).</param>
    /// <param name="Liste">Sa liste sous StartupApproved : Run, Run32 ou StartupFolder.</param>
    /// <param name="Cle">Le nom que StartupApproved porte, s'il diffère du nom affiché (le fichier, extension comprise).</param>
    public sealed record Entree(string Nom, bool Actif, string Commande = "", bool DansRun = true,
        RegistryHiveKind Ruche = RegistryHiveKind.CurrentUser, string Liste = SetStartupApprovalOp.Run, string? Cle = null)
    {
        /// <summary>Le couper (false) ou le réactiver (true), comme le Gestionnaire des tâches.</summary>
        public SetStartupApprovalOp Geste(bool actif) => new(Ruche, Liste, Cle ?? Nom, actif);
    }

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    // « MicrosoftEdgeAutoLaunch_E5F5B93C… » : le suffixe est un identifiant d'installation, pas un nom.
    private static readonly Regex Suffixe = new(@"_[0-9A-F]{16,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Ce qu'un joueur couperait par erreur : la sécurité de Windows n'est pas un programme de confort.
    private static string Garder(string nom) => nom.Equals("SecurityHealth", StringComparison.OrdinalIgnoreCase)
        ? Docteur.T("PD.Securite") : "";

    public static bool Actif(string nom, IReadOnlyDictionary<string, byte[]> approuves)
        => !approuves.TryGetValue(nom, out var v) || v.Length == 0 || (v[0] & 1) == 0;

    /// <summary>L'exécutable d'une commande Run, sans ses arguments, variables développées ; null si vide.
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
    // guillemets : relevé le 06/10, deux raccourcis dits morts à tort sans cette règle).
    private static bool Morte(Entree e, Func<string, bool> existe)
        => e.DansRun && Cible(e.Commande) is { } cible && Path.IsPathRooted(cible) && !existe(cible);

    public static IEnumerable<RepairFinding> Juger(Result<IReadOnlyList<Entree>> lues, IReadOnlyList<DemarrageEtDisque.Ralentisseur> ralentisseurs,
        Func<string, bool>? existe = null)
    {
        if (!lues.IsOk)
        {
            yield return new RepairFinding("DEMARRAGE", "programmes au démarrage", null, null, RepairStatus.Unknown,
                Docteur.T("PD.NonLu"));
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
            ? Docteur.T("PD.Retard", ms / 1000.0) : "";

        var valeur = Docteur.T(actifs.Count > 1 ? "PD.Lances" : "PD.Lance", actifs.Count)
            + Docteur.T(coupes > 1 ? "PD.Desactives" : "PD.Desactive", coupes)
            + (mortes.Count > 0 ? Docteur.T(mortes.Count > 1 ? "PD.Mortes" : "PD.Morte", mortes.Count) : "");
        var morts = mortes.Count == 0 ? ""
            : Docteur.T("PD.MortesExplication", string.Join(", ", mortes.Select(e => e.Nom)));
        yield return actifs.Count == 0
            ? new RepairFinding("DEMARRAGE", "programmes au démarrage", valeur, null, RepairStatus.Observed,
                Docteur.T("PD.Aucun") + morts)
            : new RepairFinding("DEMARRAGE", "programmes au démarrage", valeur, null, RepairStatus.Observed,
                Docteur.T("PD.SeLancent", string.Join(", ", actifs.Select(n => n + Garder(n) + Cout(n)))) + morts
                + Docteur.T("PD.Conseil"),
                RepairAction.Recommandation, Remede: Docteur.T("PD.Remede"));
    }

    private const string Approuve = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    /// <summary>Tout ou rien. Une clé qui refuse la lecture rend un échec, jamais une liste partielle.</summary>
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
                var ruche = racine == Registry.LocalMachine ? RegistryHiveKind.LocalMachine : RegistryHiveKind.CurrentUser;
                entrees.AddRange(r.GetValueNames().Where(n => n.Length > 0)
                    .Select(n => new Entree(n, Actif(n, ok), (r.GetValue(n) as string ?? "").Trim(), Ruche: ruche, Liste: approuve)));
            }
            Cles(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run");
            Cles(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run");
            Cles(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32");

            void Dossier(Environment.SpecialFolder dossier, RegistryKey racine)
            {
                var chemin = Environment.GetFolderPath(dossier);
                if (!Directory.Exists(chemin)) return;
                var ok = Approbations(racine, Approuve + SetStartupApprovalOp.DossierDemarrage);
                var ruche = racine == Registry.LocalMachine ? RegistryHiveKind.LocalMachine : RegistryHiveKind.CurrentUser;
                entrees.AddRange(Directory.EnumerateFiles(chemin)
                    .Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    .Select(f => new Entree(Path.GetFileNameWithoutExtension(f), Actif(Path.GetFileName(f), ok), f, DansRun: false,
                        Ruche: ruche, Liste: SetStartupApprovalOp.DossierDemarrage, Cle: Path.GetFileName(f))));
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
