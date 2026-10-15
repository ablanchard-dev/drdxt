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

/// <summary>Stabilité : test mémoire, vidages, incidents noyau, point de restauration et arrêts brutaux.</summary>
public sealed partial class RepairAuditService
{
    // Doc Microsoft « Event ID 41 » (maj 12/02/2026), scenario 3 (arrets sans code) : « Disable
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
                Docteur.T("ST.Memoire.NonLu"));
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
            yield return new RepairFinding("STABILITE", "test memoire Windows", Docteur.T("ST.Memoire.JamaisLance"), null, RepairStatus.Observed,
                (repetes
                    ? Docteur.T("ST.Memoire.AvecArrets", n, FenetreArrets.TotalDays, DontEnVeille(comptes))
                    : Docteur.T("ST.Memoire.AucunResultat")).TrimEnd(),
                repetes ? RepairAction.Recommandation : RepairAction.Aucune,
                Remede: Docteur.T("ST.Memoire.Outil"));
            yield break;
        }

        var dernier = lus.Value[0];
        yield return new RepairFinding("STABILITE", "test memoire Windows",
            dernier.Quand.ToLocalTime().ToString(Docteur.T("ST.FormatDateHeure"), Docteur.Langue), null, RepairStatus.Observed,
            string.IsNullOrWhiteSpace(dernier.Message)
                ? Docteur.T("ST.Memoire.SansMessage", dernier.EventId)
                : Docteur.T("ST.WindowsANote", dernier.Message));
    }

    // Le type de vidage dit ce que Windows est configure pour garder ; volmgr 161 dit qu'il n'y est
    // pas arrive. Sans vidage, la cause d'un ecran bleu ne se relit pas apres coup.
    private IEnumerable<RepairFinding> CreationDeVidage()
    {
        var lu = _vidages.Lire(DateTimeOffset.UtcNow - FenetreArrets);
        if (!lu.IsOk)
        {
            yield return new RepairFinding("STABILITE", "creation de vidage", null, null, RepairStatus.Unknown,
                Docteur.T("ST.Vidage.NonLu"));
            yield break;
        }

        var v = lu.Value!;
        var garde = v.PetitsVidages > 0 || v.VidageComplet;
        if (v.Echecs.Count > 0 && !garde)
        {
            yield return new RepairFinding("STABILITE", "creation de vidage",
                Docteur.T("ST.Vidage.EchecsValeur", v.Echecs.Count), null, RepairStatus.Suspicious,
                Docteur.T("ST.Vidage.Echecs", string.Join(Docteur.T("ST.Vidage.EtLe"),
                    v.Echecs.Select(d => d.ToLocalTime().ToString(Docteur.T("ST.FormatDateA"), Docteur.Langue))))
                + (v.DernierMessage.Length > 0 ? Docteur.T("ST.Vidage.WindowsANote", v.DernierMessage) : ""));
            yield break;
        }

        yield return new RepairFinding("STABILITE", "creation de vidage",
            Docteur.T("ST.Vidage.Petits", v.PetitsVidages) + (v.VidageComplet ? Docteur.T("ST.Vidage.Complet") : "")
            + Docteur.T("ST.Vidage.EchecsCompte", v.Echecs.Count),
            null, RepairStatus.Observed, Docteur.T("ST.Vidage.Garde", FenetreArrets.TotalDays));
    }

    // Chaque incident rapproché de ce qui a été installé juste avant.
    private IEnumerable<RepairFinding> AvantLesIncidents()
        => AvantLIncident.Juger(IncidentsDates() ?? [], [.. AvantLIncident.LirePilotes(), .. AvantLIncident.LireLogiciels()], DateTimeOffset.Now);

    // Le point de restauration d'avant le souci en cours.
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

    // Null quand un des deux journaux est illisible (« non lu » n'est pas « aucun »).
    private List<FaitDate>? IncidentsDates()
    {
        var depuis = DateTimeOffset.UtcNow - FenetreArrets;
        var incidents = new List<FaitDate>();
        var lus = _arrets.Detail(depuis);
        var rapports = _noyau.Lire();
        if (!lus.IsOk || !rapports.IsOk) return null;
        if (lus is { IsOk: true } arrets)
            incidents.AddRange(ArretBrutal.HorsBouton(arrets.Value!).Select(a =>
                new FaitDate(a.Quand, a.EcranBleu ? Docteur.T("ST.Fait.EcranBleu", a.CodeHexa)
                    : Docteur.T(a.TransitionEnCours ? "ST.Fait.ArretVeille" : "ST.Fait.ArretSansCode"))));
        if (rapports is { IsOk: true } noyau)
            // Un vidage à chaud isolé n'est pas un plantage (la machine a continué) ;
            // seule une signature répétée (3 fois, comme son constat) entre dans la chaîne des incidents.
            incidents.AddRange(noyau.Value!.Where(r => r.Quand >= depuis).GroupBy(r => (r.Code, r.Parametre1))
                .Where(g => g.Count() >= 3).SelectMany(g => g).Select(r =>
                new FaitDate(r.Quand, r.EstGraphique ? Docteur.T("ST.Fait.BlocageCarte") : Docteur.T("ST.Fait.IncidentNoyau", r.Code))));
        return incidents;
    }

    private IEnumerable<RepairFinding> IncidentsNoyau()
    {
        var rapports = _noyau.Lire();
        // La cause d'un refus dépend des droits : sans eux, les relancer ; avec, le dossier est durci.
        string Cause() => _eleve() ? Docteur.T("ST.Noyau.CauseAdmin") : " " + SansDroits;
        if (!rapports.IsOk)
        {
            yield return new RepairFinding("STABILITE", "incident noyau conserve", null, null,
                RepairStatus.Unknown, (rapports.Error?.Message ?? Docteur.T("ST.Noyau.WerIllisibles")) + Cause());
            yield break;
        }

        // Refus partiel : les lus sont jugés plus bas, les refusés sont dits à part.
        if (_noyau.Refuses > 0)
            yield return new RepairFinding("STABILITE", "incidents noyau non lus", $"{_noyau.Refuses}", null, RepairStatus.Unknown,
                Docteur.T("ST.Noyau.Refuses", _noyau.Refuses) + Cause());

        if (rapports.Value!.Count == 0)
        {
            yield return new RepairFinding("STABILITE", "incident noyau conserve", Docteur.T("ST.Noyau.Aucun"), null,
                RepairStatus.ExpectedDefault, Docteur.T("ST.Noyau.AucunRapport"));
            yield break;
        }

        foreach (var groupe in rapports.Value.GroupBy(r => (r.Code, r.Parametre1)))
        {
            var dernier = groupe.First();
            var n = groupe.Count();
            // Répété et récent (30 jours), c'est une condition qui revient ; plus ancien, un fait daté.
            var plusRecent = groupe.Max(r => r.Quand);
            var recent = DateTimeOffset.UtcNow - plusRecent <= TimeSpan.FromDays(30);
            yield return new RepairFinding("STABILITE", "incident noyau conserve",
                // Un incident isolé est « observé » et s'affiche dans la liste sans geste,
                // qui ne montre que la valeur. L'explication de CauseUsb3 n'était donc jamais lue :
                // sa famille entre dans la valeur.
                $"{n}x {dernier.Phrase}" + FamilleUsb3(dernier.Code, dernier.Parametre1), null,
                // Un incident isole est une observation ; repete a l'identique, il decrit une
                // condition qui revient.
                n >= 3 && recent ? RepairStatus.Suspicious : RepairStatus.Observed,
                // Deux incidents distincts portaient le même titre, l'un dans
                // « Mesuré », l'autre dans « Constaté » ; le code n'était que dans l'étiquette. Il est
                // nommé dès la première phrase.
                (n >= 3 ? Docteur.T("ST.Noyau.MemeSignature", n, dernier.Code, dernier.NomSdk is { } sdk ? " " + sdk : "")
                          + (recent ? Docteur.T("ST.Noyau.PasDuBruit") : Docteur.T("ST.Noyau.PlusDuBruit", plusRecent.ToLocalTime()))
                        : Docteur.T("ST.Noyau.Isole"))
                + (dernier.EstGraphique ? Docteur.T("ST.Noyau.Graphique") : CauseUsb3(dernier.Code, dernier.Parametre1))
                + Docteur.T("ST.Noyau.Dates",
                    string.Join(", ", groupe.Select(r => r.Quand.ToLocalTime().ToString(Docteur.T("ST.FormatDateHeure"), Docteur.Langue))))
                // Reference Microsoft « Bug Check 0x193 VIDEO_DXGKRNL_LIVEDUMP » (maj 10/09/2024) :
                // « This code can never be used for a real bug check; it is used to identify live dumps »,
                // et le parametre 1 n'y est documente que pour 0x100 (Internal). Le 0x80e vu sur la machine de
                // reference n'y figure pas : « ne l'invente pas » est la seule phrase juste.
                + Docteur.T("ST.Noyau.VidageAChaud"));
        }
    }

    // Référence Microsoft « Bug Check 0x144 BUGCODE_USB3_DRIVER » : les paramètres
    // 0x1001-0x1022 et 0x3000-0x3003 portent un contexte de vidage à chaud (XHCI_LIVEDUMP_CONTEXT,
    // USBHUB3_LIVEDUMP_CONTEXT). Seul 0x1009, vu sur la machine de référence, a sa cause recopiée ;
    // les autres sont nommés par leur famille, rien de plus.
    /// <summary>La famille seule, pour la valeur affichée : « , contrôleur USB 3 (vidage à chaud) ».</summary>
    internal static string FamilleUsb3(uint code, string? parametre1)
    {
        // Par le paramètre, pas par le texte de la cause : il change de langue.
        if (code != 0x144 || !uint.TryParse(parametre1, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var p))
            return "";
        return p is >= 0x3000 and <= 0x3003 ? Docteur.T("ST.Usb3.Concentrateur")
            : p is >= 0x1001 and <= 0x1022 ? Docteur.T("ST.Usb3.Controleur")
            : "";
    }

    internal static string CauseUsb3(uint code, string? parametre1)
    {
        if (code != 0x144
            || !uint.TryParse(parametre1, System.Globalization.NumberStyles.HexNumber,
                   System.Globalization.CultureInfo.InvariantCulture, out var p))
            return "";
        return p is >= 0x1001 and <= 0x1022
            ? Docteur.T("ST.Usb3.ControleurCause") + (p == 0x1009 ? Docteur.T("ST.Usb3.Ici", Docteur.T("ST.Usb3.Cause1009")) : ". ")
            : p is >= 0x3000 and <= 0x3003
                ? Docteur.T("ST.Usb3.ConcentrateurCause")
                : "";
    }

    /// <summary>
    /// Les arrets brutaux, dits sans condition.
    ///
    /// <see cref="Settings.MemoryStabilityAdvisor"/> ne parle que si XMP est actif et que des arrets
    /// existent : sur une machine dont la memoire tourne au JEDEC, onze arrets brutaux en
    /// quatre-vingt-dix jours ne seraient dits nulle part. Un fait de cette taille ne depend pas
    /// d'une hypothese sur la memoire.
    ///
    /// Ce constat ne designe aucune cause. Un `Kernel-Power 41` dit que Windows a redemarre sans
    /// avoir ete arrete proprement : gel, coupure de courant, reset, gardien materiel, tout y
    /// ressemble. Nommer un coupable ici serait inventer.
    /// </summary>
    private IEnumerable<RepairFinding> ArretsBrutaux()
    {
        var jours = (int)FenetreArrets.TotalDays;
        var lus = _arrets.Detail(DateTimeOffset.UtcNow - FenetreArrets);

        if (!lus.IsOk)
        {
            yield return new RepairFinding("STABILITE", "arrets brutaux", null, Docteur.T("ST.Arrets.Aucun"),
                RepairStatus.Unknown, Docteur.T("ST.Arrets.NonLu", lus.Error?.Message));
            yield break;
        }

        // Un appui long sur le bouton est un geste, pas une instabilite. Il est dit,
        // jamais compte.
        var tous = lus.Value!;
        var comptes = ArretBrutal.HorsBouton(tous);
        var bouton = tous.Count - comptes.Count;
        if (comptes.Count == 0)
        {
            yield return new RepairFinding("STABILITE", "arrets brutaux", Docteur.T("ST.Arrets.Aucun"), Docteur.T("ST.Arrets.Aucun"),
                RepairStatus.ExpectedDefault, Docteur.T("ST.Arrets.AucunRedemarrage", jours)
                + (bouton > 0 ? Docteur.T("ST.Arrets.BoutonNonCompte", bouton) : ""));
            yield break;
        }

        var repete = comptes.Count >= SeuilArretsRepetes;
        yield return new RepairFinding("STABILITE", "arrets brutaux",
            Docteur.T("ST.Arrets.SurJours", comptes.Count, jours), Docteur.T("ST.Arrets.Aucun"),
            repete ? RepairStatus.Suspicious : RepairStatus.Observed,
            (repete ? Docteur.T("ST.Arrets.Repetes", comptes.Count, DontEnVeille(comptes)) : Docteur.T("ST.Arrets.Isole"))
            + Docteur.T("ST.Arrets.CeQueDit", Repartition(comptes, bouton, PartiesInterrompues()))
            // « à corriger » portait son geste ailleurs, ou nulle part. Celui de la page
            // Microsoft « Event ID 41 » (scénario 3, citée en tête de ce fichier).
            + (repete ? Docteur.T("ST.Arrets.Microsoft") : ""),
            repete ? RepairAction.Recommandation : RepairAction.Aucune);
    }

    // La part signalée en veille dès la première phrase. Sur la machine de référence, 8 des 11
    // arrêts ; le chiffre seul passerait pour 11 plantages. Le sens de ce signal reste non documenté : il est dit, pas jugé.
    private static string DontEnVeille(IReadOnlyList<ArretBrutal> comptes)
        => comptes.Count(a => !a.EcranBleu && a.TransitionEnCours) is var t and > 0
            ? Docteur.T("ST.Arrets.DontVeille", t)
            : "";

    /// <summary>Les parties que le relevé n'a pas pu clore (écrites par PartiesMemorisees, dans le dossier
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
        static string Pluriel(int n, string un, string plusieurs) => Docteur.T(n == 1 ? un : plusieurs);
        var morceaux = new List<string>();

        var ecrans = comptes.Where(a => a.EcranBleu).OrderByDescending(a => a.Quand).ToList();
        if (ecrans.Count > 0)
            morceaux.Add($"{ecrans.Count} {Pluriel(ecrans.Count, "ST.Rep.EcranBleu", "ST.Rep.EcransBleus")} ("
                         + string.Join(", ", ecrans.Select(a => $"{a.CodeHexa}"
                             + (WerLiveKernelReportLog.NomSdk(a.CodeArret) is { } nom ? $" {nom}" : "")
                             + Docteur.T("ST.Rep.Le", a.Quand.ToLocalTime()))) + ")");

        // Les dates changent la lecture : des arrets tous anciens, sans leurs dates,
        // passeraient pour un probleme en cours.
        static string Quand(IEnumerable<ArretBrutal> a)
        {
            var d = a.Select(x => x.Quand.ToLocalTime()).OrderBy(x => x).ToList();
            return d.Count == 1 ? Docteur.T("ST.Rep.QuandLe", d[0]) : Docteur.T("ST.Rep.QuandDuAu", d[0], d[^1]);
        }

        var sansCodeListe = comptes.Where(a => !a.EcranBleu && !a.TransitionEnCours).ToList();
        var sansCode = sansCodeListe.Count;
        if (sansCode > 0)
            morceaux.Add($"{sansCode} {Pluriel(sansCode, "ST.Rep.SansCode", "ST.Rep.SansCodes")}"
                         + Docteur.T("ST.Rep.SansCodeSuite", Quand(sansCodeListe)));

        var transitionListe = comptes.Where(a => !a.EcranBleu && a.TransitionEnCours).ToList();
        var transition = transitionListe.Count;
        if (transition > 0)
            // « pendant une mise en veille » lirait le nom du champ. Microsoft ne définit
            // SleepInProgress nulle part (page Event ID 41, archive TechNet, Q&A relues ce jour).
            morceaux.Add(Docteur.T("ST.Rep.Transition", transition, Quand(transitionListe)));

        // Le journal ne le dit pas ; une partie interrompue juste avant le dit.
        if (interrompues is { Count: > 0 }
            && ClasseurDArrets.MarquerSousCharge(comptes.Select(a => a.Classe).ToList(), interrompues)
                .Where(a => a.PendantLaPartieDe is not null).ToList() is { Count: > 0 } enPartie)
            morceaux.Add(Docteur.T("ST.Rep.EnPartie", enPartie.Count, string.Join(", ", enPartie.Select(a => a.PendantLaPartieDe).Distinct()),
                string.Join(", ", enPartie.Select(a => a.Quand.ToLocalTime().ToString(Docteur.T("ST.Rep.FormatJour"), System.Globalization.CultureInfo.InvariantCulture)))));

        if (bouton > 0)
            morceaux.Add($"{bouton} {Pluriel(bouton, "ST.Rep.Bouton", "ST.Rep.Boutons")}");

        return string.Join(", ", morceaux);
    }
}
