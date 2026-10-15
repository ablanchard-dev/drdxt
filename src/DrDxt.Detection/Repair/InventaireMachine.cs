using System.Text.Json;
using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.Detection.Repair;

/// <summary>Une chose installée sur la machine : sa famille, son nom, sa version (ou son chemin), sa date si Windows la donne.</summary>
public sealed record ElementInstalle(string Famille, string Nom, string Version, string? Date = null);

/// <summary>
/// À chaque scan,
/// l'inventaire de ce qui peut casser une machine est relevé (logiciels, pilotes tiers, services tiers, programmes lancés au
/// démarrage), gardé, et comparé au précédent : ce qui est apparu, a disparu ou a changé de version depuis le dernier scan
/// est nommé, avec la date du scan de référence. Lecture seule.
/// </summary>
public static class InventaireMachine
{
    public const string Domaine = "CHANGEMENTS";
    public const string Logiciels = "logiciels", Pilotes = "pilotes tiers", Services = "services tiers", Demarrage = "programmes au démarrage";

    /// <param name="Lues">Les familles lues sans erreur. Une famille vide peut être lue (machine sans service
    /// tiers) ou illisible ; seule la lecture le sait. <c>null</c> dans un relevé d'avant ce champ.</param>
    public sealed record Releve(DateTimeOffset Quand, IReadOnlyList<ElementInstalle> Elements, IReadOnlyList<string>? Lues = null);

    /// <summary>Ajouts, retraits et changements de version, par famille et nom.</summary>
    public static IReadOnlyList<(ElementInstalle? Avant, ElementInstalle? Apres)> Comparer(IReadOnlyList<ElementInstalle> avant, IReadOnlyList<ElementInstalle> apres)
    {
        static string Cle(ElementInstalle e) => e.Famille + "|" + e.Nom.ToLowerInvariant();
        // Deux paquets du même nom (l'ancien pilote NVIDIA gardé à côté du nouveau) ne se réduisent plus au
        // premier lu ; toutes leurs versions forment la version de l'entrée.
        static ElementInstalle Fusion(IGrouping<string, ElementInstalle> g) => g.First() with
        {
            Version = string.Join(" + ", g.Select(e => e.Version).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)),
        };
        var a = avant.GroupBy(Cle).ToDictionary(g => g.Key, Fusion);
        var b = apres.GroupBy(Cle).ToDictionary(g => g.Key, Fusion);
        var ecarts = new List<(ElementInstalle? Avant, ElementInstalle? Apres)>();
        foreach (var (cle, e) in b)
            if (!a.TryGetValue(cle, out var x)) ecarts.Add((null, e));
            else if (!string.Equals(x.Version, e.Version, StringComparison.OrdinalIgnoreCase)) ecarts.Add((x, e));
        foreach (var (cle, e) in a)
            if (!b.ContainsKey(cle)) ecarts.Add((e, null));
        return ecarts;
    }

    /// <summary>Les constats : un par famille qui a changé ; sans relevé précédent, le point de référence est posé et dit.</summary>
    public static IEnumerable<RepairFinding> Juger(Releve? precedent, Releve actuel)
    {
        if (precedent is null)
        {
            var parFamille = string.Join(", ", actuel.Elements.GroupBy(e => e.Famille).Select(g => $"{g.Count()} {Famille(g.Key)}"));
            yield return new RepairFinding(Domaine, "point de référence", null, null, RepairStatus.Observed,
                Docteur.T("IM.Reference", parFamille));
            yield break;
        }
        var depuis = Docteur.T("IM.Depuis", precedent.Quand.ToLocalTime());
        // Une famille illisible d'un côté (pnputil absent ou trop lent, registre refusé) annoncerait « tout a
        // disparu » ou « tout est apparu ». Seules les familles lues des deux côtés se comparent. « lue » vient de
        // la lecture, plus du nombre d'éléments ; un relevé ancien, sans ce champ, retombe sur « non vide ».
        static IEnumerable<string> LuesDe(Releve r) => r.Lues ?? r.Elements.Select(e => e.Famille).Distinct();
        var lues = LuesDe(precedent).Intersect(LuesDe(actuel)).ToHashSet();
        foreach (var famille in Comparer(precedent.Elements.Where(e => lues.Contains(e.Famille)).ToList(),
                     actuel.Elements.Where(e => lues.Contains(e.Famille)).ToList()).GroupBy(e => (e.Apres ?? e.Avant)!.Famille))
        {
            var ajouts = famille.Where(e => e.Avant is null).Select(e => $"{e.Apres!.Nom} {e.Apres.Version}".Trim()).ToList();
            var retraits = famille.Where(e => e.Apres is null).Select(e => e.Avant!.Nom).ToList();
            var versions = famille.Where(e => e is { Avant: not null, Apres: not null })
                .Select(e => $"{e.Apres!.Nom} {Ecart(e.Avant!.Version, e.Apres.Version)}").ToList();
            var parties = new List<string>();
            if (ajouts.Count > 0) parties.Add(Docteur.T("IM.Apparus", string.Join(", ", ajouts)));
            if (retraits.Count > 0) parties.Add(Docteur.T("IM.Disparus", string.Join(", ", retraits)));
            if (versions.Count > 0) parties.Add(Docteur.T("IM.Versions", string.Join(", ", versions)));
            yield return new RepairFinding(Domaine, Docteur.Nom("IM.NomChanges", Docteur.Nom("IM.Famille." + famille.Key)), Docteur.T("IM.Changements", famille.Count()),
                null, RepairStatus.Observed, Docteur.T("IM.Explication", depuis, string.Join(Docteur.T("IM.Separateur"), parties)));
        }
    }

    /// <summary>Le nom affiché d'une famille ; la clé (« logiciels »…) reste celle du relevé enregistré.</summary>
    private static string Famille(string cle) => Docteur.Chercher("IM.Famille." + cle) ?? cle;

    /// <summary>Une version ajoutée ou retirée est dite comme telle : « A + B ⇒ A + B + C » passerait pour un changement
    /// alors que C est ajoutée à côté (pilote gardé). Un même programme dans un autre dossier est dit sans les chemins,
    /// qu'un service mis à jour recopierait en entier.</summary>
    internal static string Ecart(string avant, string apres)
    {
        static string[] Parts(string v) => v.Split(" + ", StringSplitOptions.RemoveEmptyEntries);
        var a = Parts(avant);
        var b = Parts(apres);
        var ajoutees = b.Except(a, StringComparer.OrdinalIgnoreCase).ToList();
        var retirees = a.Except(b, StringComparer.OrdinalIgnoreCase).ToList();
        if (retirees.Count == 0 && ajoutees.Count > 0) return Docteur.T("IM.ACote", string.Join(" + ", ajoutees), avant);
        if (ajoutees.Count == 0 && retirees.Count > 0) return Docteur.T("IM.Reste", string.Join(" − ", retirees), apres);
        if (Programme(avant) is { } pa && string.Equals(pa, Programme(apres), StringComparison.OrdinalIgnoreCase))
            return Docteur.T("IM.ChangeDossier", pa);
        return $"{avant} ⇒ {apres}";
    }

    // Le nom du programme d'une ligne de commande de service (chemin, guillemets, arguments) ; null si ce n'est pas un chemin.
    private static string? Programme(string commande)
    {
        var c = commande.Trim();
        var fin = c.StartsWith('"') ? c.IndexOf('"', 1) : c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4;
        if (fin <= 0) return null;
        var chemin = c[(c.StartsWith('"') ? 1 : 0)..fin];
        return chemin.Contains('\\') ? Path.GetFileName(chemin) : null;
    }

    // ── Lecture et stockage ─────────────────────────────────────────────────────────────

    /// <summary>Relève l'inventaire de la machine. Chaque famille est lue à part : une famille illisible n'efface pas les autres.</summary>
    public static Releve Lire(Func<string, string> pnputil)
    {
        var e = new List<ElementInstalle>();
        var lues = new List<string>();
        void Tenter(string famille, Func<IEnumerable<ElementInstalle>> lire)
        {
            try { e.AddRange(lire().ToList()); lues.Add(famille); }
            catch { /* famille illisible : les autres restent, et elle n'est pas comparée */ }
        }
        Tenter(Logiciels, LogicielsInstalles);
        Tenter(Pilotes, () => PilotesTiers(pnputil("/enum-drivers")));
        Tenter(Services, ServicesTiers);
        Tenter(Demarrage, ProgrammesAuDemarrage);
        return new Releve(DateTimeOffset.Now, e, lues);
    }

    internal static IEnumerable<ElementInstalle> LogicielsInstalles()
    {
        foreach (var (racine, chemin) in new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            using var cle = racine.OpenSubKey(chemin);
            if (cle is null) continue;
            foreach (var nom in cle.GetSubKeyNames())
            {
                using var s = cle.OpenSubKey(nom);
                if (s?.GetValue("DisplayName") is not string affiche || string.IsNullOrWhiteSpace(affiche)) continue;
                if (s.GetValue("SystemComponent") is int sys && sys == 1) continue;
                yield return new ElementInstalle(Logiciels, affiche.Trim(), (s.GetValue("DisplayVersion") as string ?? "").Trim(),
                    s.GetValue("InstallDate") as string);
            }
        }
    }

    /// <summary>Une sortie illisible (pnputil absent, trop ancien pour <c>/format xml</c>) lève : la famille est alors « non
    /// lue », jamais « aucun pilote ».</summary>
    internal static IEnumerable<ElementInstalle> PilotesTiers(string xml)
    {
        var racine = System.Xml.Linq.XDocument.Parse(xml).Root ?? throw new FormatException("sortie de pnputil sans racine");
        foreach (var d in racine.Elements("Driver"))
        {
            var version = ((string?)d.Element("DriverVersion") ?? "").Split(' ');
            yield return new ElementInstalle(Pilotes, $"{(string?)d.Element("ProviderName")} {(string?)d.Element("OriginalName")}".Trim(),
                version.LastOrDefault() ?? "", version.Length > 1 ? version[0] : null);
        }
    }

    private static IEnumerable<ElementInstalle> ServicesTiers()
    {
        using var cle = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (cle is null) yield break;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var nom in cle.GetSubKeyNames())
        {
            using var s = cle.OpenSubKey(nom);
            if (s?.GetValue("ImagePath") is not string image || string.IsNullOrWhiteSpace(image)) continue;
            var chemin = Environment.ExpandEnvironmentVariables(image.Trim().Trim('"'));
            // Le dossier Windows lu, plus « C:\Windows » écrit en dur (Windows installé sur D:).
            if (chemin.StartsWith(windows, StringComparison.OrdinalIgnoreCase) || chemin.StartsWith(@"\SystemRoot", StringComparison.OrdinalIgnoreCase)
                || chemin.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || chemin.StartsWith(@"\??\" + windows, StringComparison.OrdinalIgnoreCase))
                continue;
            yield return new ElementInstalle(Services, nom, image.Trim());
        }
    }

    // Un seul lecteur des clés Run (celui du constat « programmes au démarrage »). Les clés Run seulement,
    // comme avant : y ajouter les dossiers Démarrage ferait voir de faux « nouveaux » au premier scan suivant.
    private static IEnumerable<ElementInstalle> ProgrammesAuDemarrage()
    {
        var lu = DrDxt.Detection.Repair.ProgrammesAuDemarrage.Lire();
        if (!lu.IsOk) throw new IOException(lu.Error!.Value.Message);
        return lu.Value!.Where(e => e.DansRun).Select(e => new ElementInstalle(Demarrage, e.Nom, e.Commande));
    }

    /// <summary>Ce qui a changé depuis le dernier scan, relu sans garder l'actuel (le scan
    /// suivant le juge encore). Sans référence, rien : le point de référence est l'affaire du scan.</summary>
    public static IReadOnlyList<RepairFinding> Apercu(string fichier, Func<string, string> pnputil)
    {
        Releve? precedent = null;
        try { if (File.Exists(fichier)) precedent = JsonSerializer.Deserialize<Releve>(File.ReadAllText(fichier)); } catch { }
        return precedent is null ? [] : Juger(precedent, Lire(pnputil)).ToList();
    }

    /// <summary>Relit le relevé précédent, compare, puis garde l'actuel comme nouvelle référence.</summary>
    public static IReadOnlyList<RepairFinding> ComparerEtGarder(string fichier, Func<string, string> pnputil)
    {
        Releve? precedent = null;
        try { if (File.Exists(fichier)) precedent = JsonSerializer.Deserialize<Releve>(File.ReadAllText(fichier)); } catch { /* abîmé : on repart */ }
        var actuel = Lire(pnputil);
        var constats = Juger(precedent, actuel).ToList();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fichier)!);
            var temp = fichier + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(actuel));
            File.Move(temp, fichier, overwrite: true);
        }
        catch { /* référence non gardée : le prochain scan reposera un point de référence */ }
        return constats;
    }
}
