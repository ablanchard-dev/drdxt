using static DrDxt.Core.TexteFrancais;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Bios;
using DrDxt.Detection.Cpu;
using DrDxt.Detection.Display;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Settings;
using DrDxt.Detection.Wmi;
using Microsoft.Win32;

namespace DrDxt.Detection.Repair;

public sealed partial class RepairAuditService
{
    // 17/09/2026, doc Microsoft « Event ID 41 » (maj 12/02/2026), scenario 3 (arrets sans code) : « Disable
    // overclocking … Check the memory. Use a memory checker ». C'est la source du conseil JEDEC et de cette
    // recommandation. La meme page ne definit pas SleepInProgress : ce que vaut un arret « en transition »
    // pour la memoire reste sans source (D19 du registre de boucle).
    // Le test memoire est le geste qui tranche les arrets brutaux. Le produit dit s'il a ete fait et
    // rend la phrase de Windows telle quelle : le sens des numeros d'evenements est conteste
    // (voir IMemoryDiagnosticLog), donc aucun verdict n'est derive du numero.
    private IEnumerable<RepairFinding> TestMemoire()
    {
        var lus = _testMemoire.Lire();
        if (!lus.IsOk)
        {
            yield return new RepairFinding("STABILITE", "test memoire Windows", null, null, RepairStatus.Unknown,
                "Le journal du Diagnostic de mémoire Windows n'a pas pu être lu.");
            yield break;
        }

        if (lus.Value!.Count == 0)
        {
            // Sans arrets repetes, « jamais lance » est une observation. Avec, c'est le geste qui dit si
            // la memoire est en cause : il doit arriver sous les yeux, pas dans les etats comptes.
            var arrets = _arrets.Detail(DateTimeOffset.UtcNow - FenetreArrets);
            var comptes = arrets.IsOk ? ArretBrutal.HorsBouton(arrets.Value!) : [];
            var n = comptes.Count;
            var repetes = n >= SeuilArretsRepetes;
            yield return new RepairFinding("STABILITE", "test memoire Windows", "jamais lancé", null, RepairStatus.Observed,
                (repetes
                    ? $"Avec {n} arrêts brutaux en {FenetreArrets.TotalDays:0} jours{DontEnVeille(comptes)}, c'est le test qui dit si la mémoire est en cause, "
                      + "et il n'a jamais été lancé sur cette machine (ou son résultat a été effacé). "
                    : "Aucun résultat du Diagnostic de mémoire Windows dans le journal : ce test n'a pas été lancé sur "
                      + "cette machine, ou son résultat a été effacé. ")
                + "C'est l'outil intégré à Windows (Windows + R, puis mdsched.exe) : il redémarre le PC et le garde pour lui "
                + "pendant le test, à lancer quand tu n'en as pas besoin. MemTest86 teste plus longuement.",
                repetes ? RepairAction.Recommandation : RepairAction.Aucune);
            yield break;
        }

        var dernier = lus.Value[0];
        yield return new RepairFinding("STABILITE", "test memoire Windows",
            dernier.Quand.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), null, RepairStatus.Observed,
            string.IsNullOrWhiteSpace(dernier.Message)
                ? $"Windows a noté un passage du test (événement {dernier.EventId}) sans message lisible."
                : $"Windows a noté : « {dernier.Message} »");
    }

    // Le type de vidage dit ce que Windows est configure pour garder ; volmgr 161 dit qu'il n'y est
    // pas arrive. Sans vidage, la cause d'un ecran bleu ne se relit pas apres coup.
    private IEnumerable<RepairFinding> CreationDeVidage()
    {
        var lu = _vidages.Lire(DateTimeOffset.UtcNow - FenetreArrets);
        if (!lu.IsOk)
        {
            yield return new RepairFinding("STABILITE", "creation de vidage", null, null, RepairStatus.Unknown,
                "Le journal des échecs de vidage ou le dossier des vidages n'a pas pu être lu.");
            yield break;
        }

        var v = lu.Value!;
        var garde = v.PetitsVidages > 0 || v.VidageComplet;
        if (v.Echecs.Count > 0 && !garde)
        {
            yield return new RepairFinding("STABILITE", "creation de vidage",
                $"{v.Echecs.Count} échec(s), aucun vidage gardé", null, RepairStatus.Suspicious,
                $"Windows a voulu écrire un fichier de vidage après un plantage et n'y est pas arrivé (le "
                + string.Join(" et le ", v.Echecs.Select(d => d.ToLocalTime().ToString("dd/MM à HH:mm")))
                + "). Sans ce fichier, la cause d'un écran bleu ne peut pas être relue après coup. Changer le "
                + "type de vidage ne réglerait rien : c'est l'écriture qui échoue. La cause de l'échec n'est pas établie."
                + (v.DernierMessage.Length > 0 ? $" Windows a noté : « {v.DernierMessage} »" : ""));
            yield break;
        }

        yield return new RepairFinding("STABILITE", "creation de vidage",
            $"{v.PetitsVidages} petit(s) vidage(s)" + (v.VidageComplet ? " et un vidage complet" : "") + $", {v.Echecs.Count} échec(s)",
            null, RepairStatus.Observed,
            $"Ce que Windows a gardé après ses plantages, et ses échecs d'écriture sur {FenetreArrets.TotalDays:0} jours.");
    }

    // 03/10/2026 (tour H2) : chaque incident rapproché de ce qui a été installé juste avant.
    private IEnumerable<RepairFinding> AvantLesIncidents()
        => AvantLIncident.Juger(IncidentsDates() ?? [], [.. AvantLIncident.LirePilotes(), .. AvantLIncident.LireLogiciels()], DateTimeOffset.Now);

    // 06/10/2026 (plan 3.4) : le point de restauration d'avant le souci en cours.
    private IEnumerable<RepairFinding> PointSain()
    {
        var lus = _wmi.QueryInNamespace(@"\\.\root\default", "SELECT CreationTime, Description FROM SystemRestore");
        List<(DateTimeOffset, string)>? points = null;
        if (lus.IsOk)
        {
            points = [];
            foreach (var l in lus.Value!)
            {
                // Une date illisible est laissée : un point dont on ne sait pas l'âge ne peut pas être dit « d'avant ».
                try
                {
                    if (l.Properties.TryGetValue("CreationTime", out var c) && c is string dmtf)
                        points.Add((new DateTimeOffset(System.Management.ManagementDateTimeConverter.ToDateTime(dmtf)),
                            l.Properties.TryGetValue("Description", out var d) ? d?.ToString() ?? "" : ""));
                }
                catch (Exception) { }
            }
        }
        return PointDeRestaurationSain.Juger(points, IncidentsDates(), DateTimeOffset.Now, DateTimeOffset.UtcNow - FenetreArrets);
    }

    // Revue adverse du 09/10/2026 (P1) : null quand un des deux journaux est illisible (« non lu » n'est pas « aucun »).
    private List<FaitDate>? IncidentsDates()
    {
        var depuis = DateTimeOffset.UtcNow - FenetreArrets;
        var incidents = new List<FaitDate>();
        var lus = _arrets.Detail(depuis);
        var rapports = _noyau.Lire();
        if (!lus.IsOk || !rapports.IsOk) return null;
        if (lus is { IsOk: true } arrets)
            incidents.AddRange(ArretBrutal.HorsBouton(arrets.Value!).Select(a =>
                new FaitDate(a.Quand, a.EcranBleu ? $"écran bleu {a.CodeHexa}"
                    : a.TransitionEnCours ? "arrêt brutal (mise en veille signalée)" : "arrêt brutal sans code")));
        if (rapports is { IsOk: true } noyau)
            // Revue adverse du 09/10/2026 (R6) : un vidage à chaud isolé n'est pas un plantage (la machine a continué) ;
            // seule une signature répétée (3 fois, comme son constat) entre dans la chaîne des incidents.
            incidents.AddRange(noyau.Value!.Where(r => r.Quand >= depuis).GroupBy(r => (r.Code, r.Parametre1))
                .Where(g => g.Count() >= 3).SelectMany(g => g).Select(r =>
                new FaitDate(r.Quand, r.EstGraphique ? "blocage de la carte graphique" : $"incident noyau 0x{r.Code:X}")));
        return incidents;
    }

    private IEnumerable<RepairFinding> IncidentsNoyau()
    {
        var rapports = _noyau.Lire();
        // F-36 : la cause d'un refus dépend des droits : sans eux, les relancer ; avec, le dossier est durci.
        string Cause() => _eleve() ? " DrDXT tourne déjà en administrateur : le dossier des rapports est verrouillé."
            : " " + SansDroits;
        if (!rapports.IsOk)
        {
            yield return new RepairFinding("STABILITE", "incident noyau conserve", null, null,
                RepairStatus.Unknown, (rapports.Error?.Message ?? "rapports WER illisibles") + Cause());
            yield break;
        }

        // F-31 : refus partiel : les lus sont jugés plus bas, les refusés sont dits à part.
        if (_noyau.Refuses > 0)
            yield return new RepairFinding("STABILITE", "incidents noyau non lus", $"{_noyau.Refuses}", null, RepairStatus.Unknown,
                $"{_noyau.Refuses} rapport(s) d'incident noyau n'ont pas pu être lus (accès refusé) ; ceux qui suivent sont "
                + "les seuls lus." + Cause());

        if (rapports.Value!.Count == 0)
        {
            yield return new RepairFinding("STABILITE", "incident noyau conserve", "aucun", null,
                RepairStatus.ExpectedDefault, "aucun rapport noyau conserve par WER");
            yield break;
        }

        foreach (var groupe in rapports.Value.GroupBy(r => (r.Code, r.Parametre1)))
        {
            var dernier = groupe.First();
            var n = groupe.Count();
            // DOC-QUALITE (09/10/2026) : répété ET récent (30 jours), c'est une condition qui revient ; plus ancien, un fait daté.
            var plusRecent = groupe.Max(r => r.Quand);
            var recent = DateTimeOffset.UtcNow - plusRecent <= TimeSpan.FromDays(30);
            yield return new RepairFinding("STABILITE", "incident noyau conserve",
                // 24/09/2026 : un incident isolé est « observé » et s'affiche dans la liste sans geste,
                // qui ne montre que la valeur. L'explication de CauseUsb3 n'était donc jamais lue :
                // sa famille entre dans la valeur.
                $"{n}x {dernier.Phrase}" + FamilleUsb3(dernier.Code, dernier.Parametre1), null,
                // Un incident isole est une observation ; repete a l'identique, il decrit une
                // condition qui revient.
                n >= 3 && recent ? RepairStatus.Suspicious : RepairStatus.Observed,
                // 25/09/2026 (photo) : deux incidents distincts portaient le même titre, l'un dans
                // « Mesuré », l'autre dans « Constaté » ; le code n'était que dans l'étiquette. Il est
                // nommé dès la première phrase.
                (n >= 3 ? $"{n} fois la même signature, 0x{dernier.Code:X}{(dernier.NomSdk is { } sdk ? " " + sdk : "")} : "
                          + (recent ? "ce n'est pas du bruit. " : $"ce n'était pas du bruit, mais rien depuis le {plusRecent.ToLocalTime():dd/MM}. ")
                        : "incident isolé, conservé par Windows. ")
                + (dernier.EstGraphique
                    ? "Il vient de la carte graphique et de son pilote : Windows a photographié le pilote "
                      + "pendant un blocage. DrDXT ne sait pas ce que veut dire le paramètre, il ne l'invente pas. "
                    : CauseUsb3(dernier.Code, dernier.Parametre1))
                + "Dates : "
                + string.Join(", ", groupe.Select(r => r.Quand.ToLocalTime().ToString("dd/MM/yyyy HH:mm")))
                // 17/09/2026, reference Microsoft « Bug Check 0x193 VIDEO_DXGKRNL_LIVEDUMP » (maj 10/09/2024) :
                // « This code can never be used for a real bug check; it is used to identify live dumps »,
                // et le parametre 1 n'y est documente que pour 0x100 (Internal). Le 0x80e vu sur la machine de
                // reference n'y figure pas : « ne l'invente pas » est la seule phrase juste.
                + ". Un vidage à chaud n'est pas un plantage : la machine a continué de tourner.");
        }
    }

    // 24/09/2026, référence Microsoft « Bug Check 0x144 BUGCODE_USB3_DRIVER » : les paramètres
    // 0x1001-0x1022 et 0x3000-0x3003 portent un contexte de vidage à chaud (XHCI_LIVEDUMP_CONTEXT,
    // USBHUB3_LIVEDUMP_CONTEXT). Seul 0x1009, vu sur la machine de référence, a sa cause recopiée ;
    // les autres sont nommés par leur famille, rien de plus.
    /// <summary>La famille seule, pour la valeur affichée : « , contrôleur USB 3 (vidage à chaud) ».</summary>
    internal static string FamilleUsb3(uint code, string? parametre1)
    {
        var cause = CauseUsb3(code, parametre1);
        return cause.Contains("concentrateur", StringComparison.Ordinal) ? ", concentrateur USB 3 (vidage à chaud)"
            : cause.Contains("contrôleur", StringComparison.Ordinal) ? ", contrôleur USB 3 (vidage à chaud)"
            : "";
    }

    internal static string CauseUsb3(uint code, string? parametre1)
    {
        if (code != 0x144
            || !uint.TryParse(parametre1, System.Globalization.NumberStyles.HexNumber,
                   System.Globalization.CultureInfo.InvariantCulture, out var p))
            return "";
        var cause = p == 0x1009
            ? "une commande du contrôleur n'a pas abouti, même après son abandon sur délai dépassé. "
            : "";
        return p is >= 0x1001 and <= 0x1022
            ? "Il vient du contrôleur USB 3 de la carte mère : Microsoft range ce paramètre parmi les "
              + "vidages à chaud du contrôleur" + (cause.Length > 0 ? ", ici : " + cause : ". ")
            : p is >= 0x3000 and <= 0x3003
                ? "Il vient d'un concentrateur USB 3 : Microsoft range ce paramètre parmi les vidages à "
                  + "chaud du concentrateur. "
                : "";
    }

    /// <summary>
    /// Les arrets brutaux, dits sans condition.
    ///
    /// Ce lecteur existait depuis le 06/09 et n'atteignait l'ecran que par
    /// <see cref="Settings.MemoryStabilityAdvisor"/>, qui ne parle que si XMP est actif et que des
    /// arrets existent. Sur une machine dont la memoire tourne au JEDEC, onze arrets brutaux en
    /// quatre-vingt-dix jours n'etaient donc dits nulle part. Un fait de cette taille ne peut pas
    /// dependre d'une hypothese sur la memoire pour avoir le droit d'exister.
    ///
    /// Ce constat ne DESIGNE aucune cause. Un `Kernel-Power 41` dit que Windows a redemarre sans
    /// avoir ete arrete proprement — gel, coupure de courant, reset, gardien materiel, tout y
    /// ressemble. Nommer un coupable ici serait inventer.
    /// </summary>
    private IEnumerable<RepairFinding> ArretsBrutaux()
    {
        var jours = (int)FenetreArrets.TotalDays;
        var lus = _arrets.Detail(DateTimeOffset.UtcNow - FenetreArrets);

        if (!lus.IsOk)
        {
            yield return new RepairFinding("STABILITE", "arrets brutaux", null, "aucun",
                RepairStatus.Unknown,
                $"le journal n'a pas pu être lu ({lus.Error?.Message}). « Non lu » n'est pas "
                + "« aucun arrêt ».");
            yield break;
        }

        // 16/09/2026 : un appui long sur le bouton est un geste, pas une instabilite. Il est dit,
        // jamais compte.
        var tous = lus.Value!;
        var comptes = ArretBrutal.HorsBouton(tous);
        var bouton = tous.Count - comptes.Count;
        if (comptes.Count == 0)
        {
            yield return new RepairFinding("STABILITE", "arrets brutaux", "aucun", "aucun",
                RepairStatus.ExpectedDefault, $"aucun redémarrage non propre sur {jours} jours"
                + (bouton > 0 ? $" ({bouton} appui(s) long(s) sur le bouton, non compte(s))" : ""));
            yield break;
        }

        var repete = comptes.Count >= SeuilArretsRepetes;
        yield return new RepairFinding("STABILITE", "arrets brutaux",
            $"{comptes.Count} sur {jours} jours", "aucun",
            repete ? RepairStatus.Suspicious : RepairStatus.Observed,
            (repete
                ? $"{comptes.Count} redémarrages non propres{DontEnVeille(comptes)} : ce n'est plus un accident isolé. "
                : "redémarrage non propre, isolé à ce stade. ")
            + "Ce que le journal en dit : " + Repartition(comptes, bouton, PartiesInterrompues()) + "."
            // Revue adverse du 09/10/2026 (R9) : « à corriger » portait son geste ailleurs, ou nulle part. Celui de la page
            // Microsoft « Event ID 41 » (scénario 3, citée en tête de ce fichier).
            + (repete ? " Ce que conseille Microsoft : couper tout surcadençage (y compris la mémoire), puis tester la mémoire "
                        + "(voir le constat du test mémoire)." : ""),
            repete ? RepairAction.Recommandation : RepairAction.Aucune);
    }

    // DOC-QUALITE (09/10/2026) : la part signalée en veille dès la première phrase. Sur la machine de référence, 8 des 11
    // arrêts ; le chiffre seul se lisait comme 11 plantages. Le sens de ce signal reste non documenté : il est dit, pas jugé.
    private static string DontEnVeille(IReadOnlyList<ArretBrutal> comptes)
        => comptes.Count(a => !a.EcranBleu && a.TransitionEnCours) is var t and > 0
            ? $", dont {t} pendant une mise en veille signalée"
            : "";

    /// <summary>D1 (08/10/2026) : les parties que le relevé n'a pas pu clore (écrites par PartiesMemorisees, dans le dossier
    /// des données). Illisible ou absent : aucune, rien n'est inventé.</summary>
    internal static IReadOnlyList<PartieInterrompue> PartiesInterrompues()
    {
        try
        {
            var chemin = DossierDonnees.Chemin("parties-interrompues.json");
            return File.Exists(chemin)
                ? (System.Text.Json.JsonSerializer.Deserialize<List<PartieInterrompue?>>(File.ReadAllText(chemin)) ?? []).OfType<PartieInterrompue>().ToList()
                : [];
        }
        catch (Exception) { return []; }
    }

    /// <summary>La repartition lue dans `Kernel-Power 41`, dans l'ordre de ce qui pese le plus.</summary>
    internal static string Repartition(IReadOnlyList<ArretBrutal> comptes, int bouton, IReadOnlyList<PartieInterrompue>? interrompues = null)
    {
        static string Pluriel(int n, string un, string plusieurs) => n == 1 ? un : plusieurs;
        var morceaux = new List<string>();

        var ecrans = comptes.Where(a => a.EcranBleu).OrderByDescending(a => a.Quand).ToList();
        if (ecrans.Count > 0)
            morceaux.Add($"{ecrans.Count} {Pluriel(ecrans.Count, "écran bleu", "écrans bleus")} ("
                         + string.Join(", ", ecrans.Select(a => $"{a.CodeHexa}"
                             + (WerLiveKernelReportLog.NomSdk(a.CodeArret) is { } nom ? $" {nom}" : "")
                             + F($" le {a.Quand.ToLocalTime():dd/MM}"))) + ")");

        // 17/09/2026 : les dates changent la lecture. Sur la machine de reference, les 7 arrets en
        // transition datent du 22/07 au 05/08 et plus rien depuis : sans les dates, ils passaient
        // pour un probleme en cours.
        static string Quand(IEnumerable<ArretBrutal> a)
        {
            var d = a.Select(x => x.Quand.ToLocalTime()).OrderBy(x => x).ToList();
            return d.Count == 1 ? F($"le {d[0]:dd/MM}") : F($"du {d[0]:dd/MM} au {d[^1]:dd/MM}");
        }

        var sansCodeListe = comptes.Where(a => !a.EcranBleu && !a.TransitionEnCours).ToList();
        var sansCode = sansCodeListe.Count;
        if (sansCode > 0)
            morceaux.Add($"{sansCode} {Pluriel(sansCode, "arrêt sans aucun code", "arrêts sans aucun code")} "
                         + $"(gel, coupure de courant ou alimentation ; {Quand(sansCodeListe)})");

        var transitionListe = comptes.Where(a => !a.EcranBleu && a.TransitionEnCours).ToList();
        var transition = transitionListe.Count;
        if (transition > 0)
            // 24/09/2026 : « pendant une mise en veille » lisait le NOM du champ. Microsoft ne définit
            // SleepInProgress nulle part (page Event ID 41, archive TechNet, Q&A relues ce jour).
            morceaux.Add($"{transition} avec une mise en veille signalée en cours, sens que Microsoft "
                         + $"ne documente pas ({Quand(transitionListe)})");

        // 08/10/2026 (D1 « sous charge ») : le journal ne le dit pas ; une partie interrompue juste avant le dit.
        if (interrompues is { Count: > 0 }
            && ClasseurDArrets.MarquerSousCharge(comptes.Select(a => a.Classe).ToList(), interrompues)
                .Where(a => a.PendantLaPartieDe is not null).ToList() is { Count: > 0 } enPartie)
            morceaux.Add($"{enPartie.Count} en pleine partie ({string.Join(", ", enPartie.Select(a => a.PendantLaPartieDe).Distinct())}"
                         + F($", {string.Join(", ", enPartie.Select(a => a.Quand.ToLocalTime().ToString("dd/MM", System.Globalization.CultureInfo.InvariantCulture)))})"));

        if (bouton > 0)
            morceaux.Add($"{bouton} {Pluriel(bouton, "appui long sur le bouton, non compté", "appuis longs sur le bouton, non comptés")}");

        return string.Join(", ", morceaux);
    }
}
