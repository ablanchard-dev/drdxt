using System.Text;
using DrDxt.Core;

namespace DrDxt.Application.Autopilot;

/// <summary>
/// 07/10/2026 (revue n° 8, demande d'une validation complète sur une machine AMD avant la mise en public) : chaque
/// levier du produit est posé, relu, rendu, puis relu encore, par le code du produit (ses leviers, son canal élevé). Rien
/// n'est gardé : la machine finit comme elle a commencé, et le rapport le prouve levier par levier.
///
/// Le filet : avant chaque écriture, l'essai est inscrit dans un journal (le même que celui du produit) posé à côté du
/// rapport. Si la fenêtre meurt pendant un essai, le lancement suivant rend d'abord ce qui y reste (<see cref="RemettreAsync"/>).
/// Un levier qui ne revient pas est écrit en tête du rapport : c'est la seule ligne que le joueur doit voir.
/// </summary>
public static class ValidationComplete
{
    /// <summary>Les leviers essayés. Sur la carte, seule la limite de puissance (règle du 29/09 : jamais tension,
    /// fréquence ni ventilateur) ; `latency.render_queue_cap` est Anti-Lag sur AMD, un réglage du pilote.</summary>
    public static readonly IReadOnlyList<string> Leviers =
    [
        "latency.gameModeOn", "latency.gameDvrBackgroundOff", "latency.vrrFlipModelOn", "latency.hagsOn", "latency.mouse_acceleration_off",
        "fixit.power_plan", "fixit.mode_boost", "fixit.cpu_power_limit", "gpu.power_limit", "latency.render_queue_cap", "latency.vrr_vsync_cap",
    ];

    /// <summary>Ce que la relecture a vu. <paramref name="Pose"/> : vrai = la valeur du levier est en place, faux = la
    /// valeur d'origine est en place, <c>null</c> = pas su lire.</summary>
    public sealed record Lecture(bool? Pose, string Texte);

    /// <summary>Pourquoi la boucle s'est arrêtée avant la fin.</summary>
    public enum Arret { Aucun, LevierNonRendu, RefusDeWindows, JeuOuvert }

    /// <param name="NonRemis">Les leviers à vérifier à la main : pas rendus, ou rendus sans relecture qui le confirme.</param>
    /// <param name="Valides">Posés, relus, rendus, relus. <paramref name="NonTouches"/> : déjà réglés, sans objet ici ou
    /// illisibles. <paramref name="Refuses"/> : l'écriture a été refusée et rien n'a changé.</param>
    public sealed record Resultat(string Texte, IReadOnlyList<string> NonRemis, int Valides = 0, int NonTouches = 0,
        int Refuses = 0, Arret ArretPar = Arret.Aucun)
    {
        /// <summary>ROAD1 (08/10/2026) : les leviers validés, par identifiant (« vérifié ici » dans la liste de l'App).</summary>
        public IReadOnlyList<string> ValidesIds { get; init; } = [];

        /// <summary>Revue n° 15 : les leviers tentés (validés ou non) ; ils perdent leur « vérifié ici » d'avant.</summary>
        public IReadOnlyList<string> EssayesIds { get; init; } = [];

        /// <summary>Revue n° 15 : l'empreinte du matériel vérifié (<see cref="DrDxt.Application.Tweaks.LeviersVerifies.Empreinte"/>).</summary>
        public string Empreinte { get; init; } = "";
    }

    private enum Issue { NonTouche, Refuse, Valide, AVerifier, RefusDeWindows }

    /// <param name="jeuOuvert">Revue n° 10 : relu avant chaque levier ; un jeu lancé en cours arrête la vérification
    /// (un réglage global posé quelques secondes toucherait la partie).</param>
    public static async Task<Resultat> ValiderAsync(IReadOnlyList<ITweak> leviers, HardwareInventory materiel,
        SettingsState etat, IRevertEnAttente journal, Func<TweakSnapshot, Lecture> relire, Action<string>? ecrireBloc = null,
        Func<bool>? jeuOuvert = null)
    {
        var sb = new StringBuilder();
        var nonRemis = new List<string>();
        var validesIds = new List<string>();
        var essayesIds = new List<string>();
        int valides = 0, nonTouches = 0, refuses = 0;
        var arret = Arret.Aucun;
        void Ecrire(string t)
        {
            sb.Append(t);
            try { ecrireBloc?.Invoke(t); }
            catch (Exception) { /* le texte reste dans le rapport final */ }
        }
        // 09/10/2026 : un rapport reçu d'un inconnu doit dire de quelle machine il vient (processeur, carte, pilote),
        // sinon il ne prouve rien pour un matériel précis.
        Ecrire($"Matériel : {Materiel(materiel)}{Environment.NewLine}{Environment.NewLine}");
        foreach (var levier in leviers)
        {
            if (jeuOuvert?.Invoke() == true)
            {
                arret = Arret.JeuOuvert;
                Ecrire($"ARRÊT : un jeu s'est ouvert ; les leviers suivants ne sont pas essayés.{Environment.NewLine}");
                break;
            }
            string bloc;
            Issue issue;
            try { (bloc, issue) = await UnLevierAsync(levier, materiel, etat, journal, relire, nonRemis).ConfigureAwait(false); }
            catch (Exception ex)
            {
                (bloc, issue) = ($"erreur inattendue : {ex.Message}", Issue.AVerifier);
                nonRemis.Add($"{levier.Id} (état inconnu après une erreur)");
            }
            // Un refus de l'accord Windows ne dit rien du levier : il ne retire pas une preuve d'avant.
            if (issue is not (Issue.NonTouche or Issue.RefusDeWindows)) essayesIds.Add(levier.Id);
            switch (issue)
            {
                case Issue.Valide: valides++; validesIds.Add(levier.Id); break;
                case Issue.NonTouche: nonTouches++; break;
                case Issue.Refuse: refuses++; break;
            }
            Ecrire($"=== {levier.Id}{Environment.NewLine}{bloc.TrimEnd()}{Environment.NewLine}{Environment.NewLine}");

            // Revue n° 10 : l'invite Windows refusée ne se repropose pas sept fois de suite.
            if (issue == Issue.RefusDeWindows)
            {
                arret = Arret.RefusDeWindows;
                Ecrire($"ARRÊT : l'accord de Windows a été refusé ; rien n'a été posé, les leviers suivants ne sont pas essayés.{Environment.NewLine}");
                break;
            }
            // Revue n° 9 : un levier non rendu reste au journal. Le levier suivant l'écraserait (un seul essai y
            // tient) : on s'arrête là, et le journal le garde pour la remise au prochain lancement.
            if (journal.Lire() is not { IsOk: true, Value: null })
            {
                arret = Arret.LevierNonRendu;
                Ecrire($"ARRÊT : {levier.Id} n'est pas revenu ; les leviers suivants ne sont pas essayés, et le journal le garde : DrDXT le remettra au prochain lancement.{Environment.NewLine}");
                break;
            }
        }
        return new Resultat(sb.ToString(), nonRemis, valides, nonTouches, refuses, arret) { ValidesIds = validesIds, EssayesIds = essayesIds, Empreinte = DrDxt.Application.Tweaks.LeviersVerifies.Empreinte(materiel) };
    }

    private static async Task<(string, Issue)> UnLevierAsync(ITweak levier, HardwareInventory materiel, SettingsState etat,
        IRevertEnAttente journal, Func<TweakSnapshot, Lecture> relire, List<string> nonRemis)
    {
        if (!levier.AppliesTo(materiel, etat))
            return (levier.EtatNonLu(etat)
                ? "pas touché : son état n'a pas pu être lu."
                : "pas touché : déjà réglé, ou ne concerne pas ce PC.", Issue.NonTouche);

        // Le journal avant la machine : sans filet, rien n'est écrit.
        var pose = journal.Poser(levier.Id, null);
        if (!pose.IsOk) return ($"pas touché : journal impossible à écrire ({pose.Error?.Message}).", Issue.NonTouche);

        var ecrit = await levier.ApplyAsync().ConfigureAwait(false);
        if (!ecrit.IsOk || ecrit.Value is not { } snapshot)
        {
            // Revue n° 10 : posé en partie, retour arrière raté. Le journal reste (sans origine) : le lancement
            // suivant dit de vérifier ce réglage, et la boucle s'arrête ici.
            if (ecrit.Error?.Code == ErrorCodes.TweakPartiallyApplied)
            {
                nonRemis.Add($"{levier.Id} : posé en partie, le retour arrière a échoué ({ecrit.Error?.Message})");
                return ($"PAS REMIS D'ORIGINE : posé en partie, retour arrière raté : {ecrit.Error?.Message}", Issue.AVerifier);
            }
            journal.Lever();
            return ($"écriture refusée, rien n'a changé : {ecrit.Error?.Code} {ecrit.Error?.Message}",
                ecrit.Error?.Code == ErrorCodes.ElevationDeclined ? Issue.RefusDeWindows : Issue.Refuse);
        }
        var complete = journal.Poser(levier.Id, snapshot);

        var sb = new StringBuilder();
        var posee = Relire(relire, snapshot);
        sb.AppendLine($"posé, relu   : {Mot(posee.Pose, "en place", "pas pris")} ({posee.Texte})");

        var rendu = await levier.RevertAsync(snapshot).ConfigureAwait(false);
        if (!rendu.IsOk) rendu = await levier.RevertAsync(snapshot).ConfigureAwait(false);   // une seconde chance
        var remis = Relire(relire, snapshot);
        sb.AppendLine($"rendu        : {(rendu.IsOk ? "oui" : $"NON ({rendu.Error?.Code} {rendu.Error?.Message})")}");
        sb.AppendLine($"rendu, relu  : {Mot(remis.Pose is { } p ? !p : null, "origine en place", "PAS à l'origine")} ({remis.Texte})");

        if (rendu.IsOk && remis.Pose != true) journal.Lever();
        if (!rendu.IsOk || remis.Pose == true) nonRemis.Add($"{levier.Id} : PAS remis d'origine ({remis.Texte})");
        else if (remis.Pose is null) nonRemis.Add($"{levier.Id} : rendu, mais la relecture n'a pas pu le confirmer ({remis.Texte})");
        if (!complete.IsOk) sb.AppendLine($"journal incomplet pendant l'essai : {complete.Error?.Message}");

        var (verdict, issue) = (posee.Pose, rendu.IsOk, remis.Pose) switch
        {
            (true, true, false) => ("validé (écrit, relu, rendu, relu)", Issue.Valide),
            (_, true, not true) => ("rendu, mais l'écriture n'a pas été vue à la relecture", Issue.AVerifier),
            _ => ("PAS REMIS D'ORIGINE", Issue.AVerifier),
        };
        sb.AppendLine("verdict      : " + verdict);
        return (sb.ToString(), issue);
    }

    /// <param name="Propre">Vrai seulement quand rien ne restait, ou que ce qui restait est rendu. Faux = à vérifier à la main.</param>
    public sealed record Remise(string Texte, bool Propre);

    /// <summary>Rend ce qu'un essai interrompu a laissé dans le journal. Ne lève jamais.
    /// 09/10/2026 (revue) : le journal est rejoué en administrateur ; comme au démarrage de l'App, seul un inverse que ce
    /// levier écrit lui-même est rejoué (<paramref name="rejouable"/> = RevertAuDemarrage.InverseRejouable en production).</summary>
    public static async Task<Remise> RemettreAsync(IRevertEnAttente journal, Func<string, ITweak?> trouver, Func<string, string?, bool> rejouable)
    {
        try
        {
            var lu = journal.Lire();
            if (!lu.IsOk) return new($"journal illisible : {lu.Error?.Message}", false);
            if (lu.Value is not { } entree) return new("rien à remettre.", true);
            if (entree.Snapshot is not { } s)
            {
                // Revue n° 9 : arrêté pendant l'écriture, l'origine n'a pas été gardée. Le journal ne sait rien
                // rendre (il est levé, sinon il bloquerait tout lancement), mais le bilan ne dit pas « tout est remis ».
                journal.Lever();
                return new($"{entree.TweakId} : l'essai s'est arrêté pendant l'écriture, avant que l'origine soit gardée. À vérifier à la main.", false);
            }
            if (trouver(entree.TweakId) is not { } levier) return new($"{entree.TweakId} : levier inconnu de ce banc, journal gardé.", false);
            if (!rejouable(entree.TweakId, s.OriginalValueJson))
                return new($"{entree.TweakId} : le journal ne demande pas une remise en état de ce levier, rien n'est rejoué. À vérifier à la main.", false);
            var r = await levier.RevertAsync(s).ConfigureAwait(false);
            if (!r.IsOk) return new($"{entree.TweakId} : PAS REMIS ({r.Error?.Code} {r.Error?.Message}), journal gardé.", false);
            journal.Lever();
            return new($"{entree.TweakId} : remis d'origine.", true);
        }
        catch (Exception ex) { return new($"remise impossible : {ex.Message}", false); }
    }

    private static Lecture Relire(Func<TweakSnapshot, Lecture> relire, TweakSnapshot s)
    {
        try { return relire(s); }
        catch (Exception ex) { return new Lecture(null, $"relecture impossible : {ex.Message}"); }
    }

    /// <summary>La carte qui fait tourner les jeux : celle qui a le plus de mémoire vidéo dédiée (un iGPU n'en a presque
    /// pas). <c>null</c> sans carte lue.</summary>
    public static GpuInfo? CartePrincipale(HardwareInventory? materiel)
        => materiel?.Gpus.OrderByDescending(g => g.VramBytes).FirstOrDefault();

    /// <summary>« processeur | carte (pilote x) | autres cartes » : les modèles seulement, aucun numéro de série.</summary>
    public static string Materiel(HardwareInventory? materiel)
    {
        if (materiel is null) return "non lu";
        var principale = CartePrincipale(materiel);
        var cartes = principale is null ? "carte non lue"
            : $"{principale.Model} (pilote {(principale.DriverVersion.Length > 0 ? principale.DriverVersion : "non lu")})";
        var autres = materiel.Gpus.Where(g => !ReferenceEquals(g, principale)).Select(g => g.Model).ToList();
        return $"{materiel.Cpu.Model} | {cartes}{(autres.Count > 0 ? " | aussi : " + string.Join(", ", autres) : "")}";
    }

    private static string Mot(bool? v, string oui, string non) => v switch { true => oui, false => non, null => "non lu" };
}
