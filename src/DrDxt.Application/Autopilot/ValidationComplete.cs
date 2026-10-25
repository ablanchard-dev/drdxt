using System.Text;
using DrDxt.Core;

namespace DrDxt.Application.Autopilot;

/// <summary>
/// Les leviers de la liste ci-dessous sont posés, relus, rendus, puis relus encore, par le code du produit (ses leviers,
/// son canal élevé). Un levier déjà réglé n'est touché qu'en mode profond, un levier tenu par un essai jamais. Rien n'est
/// gardé : la machine finit comme elle a commencé, et le rapport le prouve levier par levier.
///
/// Le filet : avant chaque écriture, l'essai est inscrit dans un journal (le même que celui du produit) posé à côté du
/// rapport. Si la fenêtre meurt pendant un essai, le lancement suivant rend d'abord ce qui y reste (<see cref="RemettreAsync"/>).
/// Un levier qui ne revient pas est écrit en tête du rapport : c'est la seule ligne que le joueur doit voir.
/// </summary>
public static class ValidationComplete
{
    /// <summary>Les leviers essayés. Sur la carte, seule la limite de puissance (règle de la carte : jamais tension,
    /// fréquence ni ventilateur) ; `latency.render_queue_cap` est Anti-Lag sur AMD, un réglage du pilote.</summary>
    public static readonly IReadOnlyList<string> Leviers =
    [
        "latency.gameModeOn", "latency.gameDvrBackgroundOff", "latency.vrrFlipModelOn", "latency.hagsOn", "latency.mouse_acceleration_off",
        "fixit.power_plan", "fixit.mode_boost", "fixit.cpu_power_limit", "gpu.power_limit", "latency.render_queue_cap", "latency.vrr_vsync_cap",
        "fixit.wifi_performances", "fixit.heures_actives",
    ];

    /// <summary>Ce que la relecture a vu. <paramref name="Pose"/> : vrai = la valeur du levier est en place, faux = la
    /// valeur d'origine est en place, <c>null</c> = pas su lire.</summary>
    /// <param name="Lu">La valeur relue seule, pour le rapport d'un exercice ; <c>null</c> quand elle n'a pas été lue.</param>
    public sealed record Lecture(bool? Pose, string Texte, string? Lu = null);

    /// <summary>Pourquoi la boucle s'est arrêtée avant la fin.</summary>
    public enum Arret { Aucun, LevierNonRendu, RefusDeWindows, JeuOuvert, ParLeJoueur }

    /// <param name="NonRemis">Les leviers à vérifier à la main : pas rendus, ou rendus sans relecture qui le confirme.</param>
    /// <param name="Valides">Posés, relus, rendus, relus. <paramref name="NonTouches"/> : déjà réglés, sans objet ici ou
    /// illisibles. <paramref name="Refuses"/> : l'écriture a été refusée et rien n'a changé.</param>
    public sealed record Resultat(string Texte, IReadOnlyList<string> NonRemis, int Valides = 0, int NonTouches = 0,
        int Refuses = 0, Arret ArretPar = Arret.Aucun)
    {
        /// <summary>Les leviers validés, par identifiant (« vérifié ici » dans la liste de l'App).</summary>
        public IReadOnlyList<string> ValidesIds { get; init; } = [];

        /// <summary>Les leviers tentés (validés ou non) ; ils perdent leur « vérifié ici » d'avant.</summary>
        public IReadOnlyList<string> EssayesIds { get; init; } = [];

        /// <summary>L'empreinte du matériel vérifié (<see cref="DrDxt.Application.Tweaks.LeviersVerifies.Empreinte"/>).</summary>
        public string Empreinte { get; init; } = "";
    }

    private enum Issue { NonTouche, Refuse, Valide, AVerifier, RefusDeWindows }

    /// <param name="jeuOuvert">Relu avant chaque levier ; un jeu lancé en cours arrête la vérification
    /// (un réglage global posé quelques secondes toucherait la partie).</param>
    /// <param name="arretDemande">Le joueur a demandé l'arrêt : relu avant chaque levier, jamais au milieu d'un levier, qui
    /// est posé, relu et rendu d'un seul tenant. Rien ne reste donc à moitié fait.</param>
    /// <param name="tenuParUnEssai">Vrai pour le levier qu'un essai en alternance tient : posé puis rendu ici, il fausserait la
    /// partie suivante de l'essai. Il n'est pas touché, et le rapport dit comment le vérifier.</param>
    public static async Task<Resultat> ValiderAsync(IReadOnlyList<ITweak> leviers, HardwareInventory materiel,
        SettingsState etat, IRevertEnAttente journal, Func<TweakSnapshot, Lecture> relire, Action<string>? ecrireBloc = null,
        Func<bool>? jeuOuvert = null, Func<bool>? arretDemande = null, Func<string, bool>? tenuParUnEssai = null,
        bool profond = false)
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
        // Un rapport reçu d'un inconnu doit dire de quelle machine il vient (processeur, carte, pilote),
        // sinon il ne prouve rien pour un matériel précis.
        Ecrire(Docteur.T("VC.Materiel", Materiel(materiel)) + Environment.NewLine + Environment.NewLine);
        foreach (var levier in leviers)
        {
            if (arretDemande?.Invoke() == true)
            {
                arret = Arret.ParLeJoueur;
                Ecrire(Docteur.T("VC.Arret.Joueur") + Environment.NewLine);
                break;
            }
            if (jeuOuvert?.Invoke() == true)
            {
                arret = Arret.JeuOuvert;
                Ecrire(Docteur.T("VC.Arret.Jeu") + Environment.NewLine);
                break;
            }
            string bloc;
            Issue issue;
            try
            {
                (bloc, issue) = tenuParUnEssai?.Invoke(levier.Id) == true
                    ? (Docteur.T("VC.PasTouche.Essai"), Issue.NonTouche)
                    : await UnLevierAsync(levier, materiel, etat, journal, relire, nonRemis, profond).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                (bloc, issue) = (Docteur.T("VC.ErreurInattendue", ex.Message), Issue.AVerifier);
                nonRemis.Add(Docteur.T("VC.EtatInconnu", levier.Id));
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

            // L'invite Windows refusée ne se repropose pas sept fois de suite.
            if (issue == Issue.RefusDeWindows)
            {
                arret = Arret.RefusDeWindows;
                Ecrire(Docteur.T("VC.Arret.Uac") + Environment.NewLine);
                break;
            }
            // Un levier non rendu reste au journal. Le levier suivant l'écraserait (un seul essai y
            // tient) : on s'arrête là, et le journal le garde pour la remise au prochain lancement.
            if (journal.Lire() is not { IsOk: true, Value: null })
            {
                arret = Arret.LevierNonRendu;
                Ecrire(Docteur.T("VC.Arret.NonRendu", levier.Id) + Environment.NewLine);
                break;
            }
        }
        return new Resultat(sb.ToString(), nonRemis, valides, nonTouches, refuses, arret) { ValidesIds = validesIds, EssayesIds = essayesIds, Empreinte = DrDxt.Application.Tweaks.LeviersVerifies.Empreinte(materiel) };
    }

    /// <summary>Le banc profond : origine au journal, état non cible posé et relu, levier posé et relu, origine exacte rendue
    /// et relue. L'origine est rendue quoi qu'il arrive après la première écriture ; le journal n'est levé que si elle est relue.</summary>
    private static async Task<(string, Issue)> ExercerAsync(ITweak levier, ExerciceProfond exercice, IRevertEnAttente journal,
        Func<TweakSnapshot, Lecture> relire, List<string> nonRemis)
    {
        var origine = new TweakSnapshot(levier.Id, exercice.OrigineJson);
        var note = journal.Poser(levier.Id, origine);
        if (!note.IsOk) return (Docteur.T("VC.PasTouche.Journal", note.Error?.Message), Issue.NonTouche);

        Lecture? nonCible = null, prise = null;
        Result rendu;
        Lecture remis;
        try
        {
            var horsCible = await levier.RevertAsync(new TweakSnapshot(levier.Id, exercice.HorsCibleJson)).ConfigureAwait(false);
            nonCible = Relire(relire, new TweakSnapshot(levier.Id, exercice.OrigineJson, exercice.HorsCibleJson));
            if (horsCible.IsOk && nonCible.Pose == true && await levier.ApplyAsync().ConfigureAwait(false) is { IsOk: true, Value: { } pose })
                prise = Relire(relire, pose);
        }
        catch (Exception) { /* l'origine est rendue ci-dessous, et l'exercice dit non conclu */ }
        finally
        {
            rendu = await RendreAsync(levier, origine).ConfigureAwait(false);
            if (!rendu.IsOk) rendu = await RendreAsync(levier, origine).ConfigureAwait(false);   // une seconde chance
            remis = Relire(relire, new TweakSnapshot(levier.Id, exercice.HorsCibleJson, exercice.OrigineJson));
            if (rendu.IsOk && remis.Pose == true) journal.Lever();
            else nonRemis.Add(Docteur.T("VC.PasRemis.Levier", levier.Id, remis.Texte));
        }

        // Les étapes nommées, avec ce qui a été lu à chacune : l'« origine » d'une relecture, aux étapes 2 et 3, était l'état
        // intermédiaire, pas l'origine du joueur.
        static string Etape(Lecture? l) => l?.Lu is { } v ? Docteur.T("VC.Exercice.Lu", v) : l?.Texte ?? "-";
        var detail = Docteur.T("VC.Exercice.Etapes", Etape(nonCible), Etape(prise), Etape(remis));
        if (rendu.IsOk && remis.Pose == true && prise?.Pose == true)
            return (Docteur.T("VC.Exerce", detail) + (levier.TakesEffectAfterReboot ? " " + Docteur.T("VC.Exercice.AuRedemarrage") : ""),
                Issue.Valide);
        return rendu.IsOk && remis.Pose == true
            ? (Docteur.T("VC.Exercice.NonConclu", detail), Issue.AVerifier)
            : (Docteur.T("VC.Verdict.PasRemis"), Issue.AVerifier);
    }

    private static async Task<Result> RendreAsync(ITweak levier, TweakSnapshot origine)
    {
        try { return await levier.RevertAsync(origine).ConfigureAwait(false); }
        catch (Exception ex) { return Result.Fail(ErrorCodes.HelperOpFailed, ex.Message); }
    }

    private static async Task<(string, Issue)> UnLevierAsync(ITweak levier, HardwareInventory materiel, SettingsState etat,
        IRevertEnAttente journal, Func<TweakSnapshot, Lecture> relire, List<string> nonRemis, bool profond = false)
    {
        if (!levier.AppliesTo(materiel, etat))
        {
            if (levier.EtatNonLu(etat)) return (Docteur.T("VC.PasTouche.NonLu"), Issue.NonTouche);
            if (!profond) return (Docteur.T("VC.PasTouche.DejaRegle"), Issue.NonTouche);
            return levier.PreparerExercice(materiel, etat) is { } exercice
                ? await ExercerAsync(levier, exercice, journal, relire, nonRemis).ConfigureAwait(false)
                : (Docteur.T("VC.PasTouche.PasDInverse"), Issue.NonTouche);
        }

        // Le journal avant la machine : sans filet, rien n'est écrit.
        var pose = journal.Poser(levier.Id, null);
        if (!pose.IsOk) return (Docteur.T("VC.PasTouche.Journal", pose.Error?.Message), Issue.NonTouche);

        // Le journal ci-dessus ne connaît pas encore l'origine : le helper la garde, jusqu'au rendu.
        using var portee = PorteeDePose.Ouvrir();
        var ecrit = await levier.ApplyAsync().ConfigureAwait(false);
        if (!ecrit.IsOk || ecrit.Value is not { } snapshot)
        {
            // Posé en partie, retour arrière raté. Le journal reste (sans origine) : le lancement
            // suivant dit de vérifier ce réglage, et la boucle s'arrête ici.
            if (ecrit.Error?.Code == ErrorCodes.TweakPartiallyApplied)
            {
                nonRemis.Add(Docteur.T("VC.PoseEnPartie", levier.Id, ecrit.Error?.Message));
                return (Docteur.T("VC.PasRemis.EnPartie", ecrit.Error?.Message), Issue.AVerifier);
            }
            journal.Lever();
            return (Docteur.T("VC.EcritureRefusee", ecrit.Error?.Code, ecrit.Error?.Message),
                ecrit.Error?.Code == ErrorCodes.ElevationDeclined ? Issue.RefusDeWindows : Issue.Refuse);
        }
        // La machine portait déjà la valeur du levier (un essai en alternance l'avait posée, par exemple) : la pose a réécrit la
        // même valeur, et aucune relecture ne peut la distinguer de l'origine. Rien n'est à prouver ni à rendre.
        if (snapshot.AppliedValueJson is { } valeurPosee && valeurPosee == snapshot.OriginalValueJson)
        {
            journal.Lever();
            return (Docteur.T("VC.PasTouche.DejaRegle"), Issue.NonTouche);
        }
        var complete = journal.Poser(levier.Id, snapshot);
        // L'acquit attend le rendu : l'App tuée entre les deux laissait l'origine dans le seul fichier du compte, que la
        // désinstallation élevée ne lit pas. Le rendu part hors de la portée, jamais journalisé comme une pose.
        portee.Detacher();

        var sb = new StringBuilder();
        var posee = Relire(relire, snapshot);
        sb.AppendLine(Docteur.T("VC.Ligne.Pose", Mot(posee.Pose, Docteur.T("VC.EnPlace"), Docteur.T("VC.PasPris")), posee.Texte));

        var rendu = await levier.RevertAsync(snapshot).ConfigureAwait(false);
        if (!rendu.IsOk) rendu = await levier.RevertAsync(snapshot).ConfigureAwait(false);   // une seconde chance
        var remis = Relire(relire, snapshot);
        sb.AppendLine(Docteur.T("VC.Ligne.Rendu", rendu.IsOk ? Docteur.T("Rapport.Oui") : Docteur.T("VC.Non", rendu.Error?.Code, rendu.Error?.Message)));
        sb.AppendLine(Docteur.T("VC.Ligne.RenduRelu", Mot(remis.Pose is { } p ? !p : null, Docteur.T("VC.OrigineEnPlace"), Docteur.T("VC.PasALOrigine")), remis.Texte));

        if (rendu.IsOk && remis.Pose != true) journal.Lever();
        if (complete.IsOk && rendu.IsOk && remis.Pose != true) await portee.FermerAsync().ConfigureAwait(false);
        if (!rendu.IsOk || remis.Pose == true) nonRemis.Add(Docteur.T("VC.PasRemis.Levier", levier.Id, remis.Texte));
        else if (remis.Pose is null) nonRemis.Add(Docteur.T("VC.RenduNonConfirme", levier.Id, remis.Texte));
        if (!complete.IsOk) sb.AppendLine(Docteur.T("VC.JournalIncomplet", complete.Error?.Message));

        var (verdict, issue) = (posee.Pose, rendu.IsOk, remis.Pose) switch
        {
            (true, true, false) => (Docteur.T("VC.Verdict.Valide"), Issue.Valide),
            (_, true, not true) => (Docteur.T("VC.Verdict.NonVu"), Issue.AVerifier),
            _ => (Docteur.T("VC.Verdict.PasRemis"), Issue.AVerifier),
        };
        sb.AppendLine(Docteur.T("VC.Ligne.Verdict", verdict));
        return (sb.ToString(), issue);
    }

    /// <param name="Propre">Vrai seulement quand rien ne restait, ou que ce qui restait est rendu. Faux = à vérifier à la main.</param>
    public sealed record Remise(string Texte, bool Propre);

    /// <summary>Rend ce qu'un essai interrompu a laissé dans le journal. Ne lève jamais.
    /// Le journal est rejoué en administrateur ; comme au démarrage de l'App, seul un inverse que ce
    /// levier écrit lui-même est rejoué (<paramref name="rejouable"/> = RevertAuDemarrage.InverseRejouable en production).</summary>
    /// <param name="registre">Le lecteur du registre, pour la porte des rendus.</param>
    /// <param name="verdict">Le vérificateur de la pose : changé depuis hors de DrDXT, rien n'est réécrit ; rendu, « remis
    /// d'origine » ne se dit que relu à l'origine. Sans lui, la réussite du levier fait foi.</param>
    public static async Task<Remise> RemettreAsync(IRevertEnAttente journal, Func<string, ITweak?> trouver, Func<string, string?, bool> rejouable,
        Tweaks.Hardware.IRegistryReader? registre = null, Func<TweakSnapshot, Tweaks.EtatApplique>? verdict = null)
    {
        try
        {
            var lu = journal.Lire();
            if (!lu.IsOk) return new(Docteur.T("VC.JournalIllisible", lu.Error?.Message), false);
            if (lu.Value is not { } entree) return new(Docteur.T("VC.RienARemettre"), true);
            if (entree.Snapshot is not { } s)
            {
                // Arrêté pendant l'écriture, l'origine n'a pas été gardée. Le journal ne sait rien
                // rendre (il est levé, sinon il bloquerait tout lancement), mais le bilan ne dit pas « tout est remis ».
                journal.Lever();
                return new(Docteur.T("VC.Remise.ArretEcriture", entree.TweakId), false);
            }
            if (trouver(entree.TweakId) is not { } levier) return new(Docteur.T("VC.Remise.Inconnu", entree.TweakId), false);
            if (!rejouable(entree.TweakId, s.OriginalValueJson))
                return new(Docteur.T("VC.Remise.Refusee", entree.TweakId), false);
            var pose = Tweaks.TweakReadback.EtatDeLaPose(s, registre, verdict);
            if (pose == Tweaks.PoseRelue.ChangeDepuis)
            {
                journal.Lever();
                return new(Docteur.T("VC.Remise.ChangeDepuis", entree.TweakId), false);
            }
            if (pose == Tweaks.PoseRelue.DeDrDxt)
            {
                var r = await levier.RevertAsync(s).ConfigureAwait(false);
                if (!r.IsOk) return new(Docteur.T("VC.Remise.Echec", entree.TweakId, r.Error?.Code, r.Error?.Message), false);
                // Rendu, mais la valeur de DrDXT est encore là : le journal reste pour le lancement suivant.
                if (Tweaks.TweakReadback.EncorePoseRelue(s, registre, verdict))
                    return new(Docteur.T("VC.Remise.EncorePose", entree.TweakId), false);
            }
            journal.Lever();
            return verdict is null || verdict(s) == Tweaks.EtatApplique.NotApplied
                ? new(Docteur.T("VC.Remise.Ok", entree.TweakId), true)
                : new(Docteur.T("VC.Remise.NonRelue", entree.TweakId), false);
        }
        catch (Exception ex) { return new(Docteur.T("VC.Remise.Impossible", ex.Message), false); }
    }

    private static Lecture Relire(Func<TweakSnapshot, Lecture> relire, TweakSnapshot s)
    {
        try { return relire(s); }
        catch (Exception ex) { return new Lecture(null, Docteur.T("VC.RelectureImpossible", ex.Message)); }
    }

    /// <summary>La carte qui fait tourner les jeux : celle qui a le plus de mémoire vidéo dédiée (un iGPU n'en a presque
    /// pas). <c>null</c> sans carte lue.</summary>
    public static GpuInfo? CartePrincipale(HardwareInventory? materiel)
        => materiel?.Gpus.OrderByDescending(g => g.VramBytes).FirstOrDefault();

    /// <summary>« processeur | carte (pilote x) | autres cartes » : les modèles seulement, aucun numéro de série.</summary>
    public static string Materiel(HardwareInventory? materiel)
    {
        if (materiel is null) return Docteur.T("RF.Statut.NonLu");
        var principale = CartePrincipale(materiel);
        var cartes = principale is null ? Docteur.T("VC.CarteNonLue")
            : Docteur.T("VC.CartePilote", principale.Model, principale.DriverVersion.Length > 0 ? principale.DriverVersion : Docteur.T("RF.Statut.NonLu"));
        var autres = materiel.Gpus.Where(g => !ReferenceEquals(g, principale)).Select(g => g.Model).ToList();
        return $"{materiel.Cpu.Model} | {cartes}{(autres.Count > 0 ? Docteur.T("VC.Aussi", string.Join(", ", autres)) : "")}";
    }

    private static string Mot(bool? v, string oui, string non) => v switch { true => oui, false => non, null => Docteur.T("RF.Statut.NonLu") };
}
