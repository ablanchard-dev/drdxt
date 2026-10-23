using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Application.Parties;
using DrDxt.Application.Tweaks;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>
/// Un levier à chaud essayé en blocs A-B-B-A, sans geste à chaque partie. Le clic
/// « Essayer en alternance » est le consentement (et porte l'invite Windows éventuelle) ; ensuite DrDXT pose ou rend le
/// levier entre deux parties, seulement si son helper tourne déjà : jamais d'invite Windows pendant que tu joues. Helper
/// absent : l'essai se met en pause et la carte le dit. Tranché (<see cref="AlternanceDesEssais"/>), il s'arrête sur le
/// meilleur état ; « Arrêter l'essai » rend l'état d'origine à tout moment.
/// </summary>
public partial class ScanViewModel
{
    /// <summary><c>null</c> = pas d'essai possible (tests qui ne le vérifient pas).</summary>
    internal MemoireDeLEssai? MemoireEssai { get; set; }

    /// <summary>Le levier à chaud que le scan propose d'essayer, ou <c>null</c>.</summary>
    [ObservableProperty]
    private string? _levierAEssayer;

    /// <summary>La phrase de l'essai en cours, ou de son verdict ; <c>null</c> sans essai.</summary>
    [ObservableProperty]
    private string? _ligneEssai;

    /// <summary>L'avancée lancée par la dernière partie enregistrée, pour que les tests l'attendent.</summary>
    internal Task DerniereAvanceeEssai { get; private set; } = Task.CompletedTask;

    /// <summary>Seuls les leviers à chaud s'alternent : ils agissent tout de suite, sans redémarrage.</summary>
    internal static bool PeutAlterner(string id) => CatalogueDeCandidats.Decrire(id) is { Kind: CandidateKind.AutoHot };

    public bool EssaiEnCours => MemoireEssai?.Lire() is not null;

    /// <summary>Le levier n'est pas dans l'état que les blocs prévoient pour la prochaine partie : le relevé de fond ne pose
    /// rien, et un réveil rend un levier volatil. Seule l'App ouverte le rattrape, par « Reprendre l'essai ».</summary>
    public bool EssaiEnAttente => MemoireEssai?.Lire() is { } lu && !SelonLePlan(Effectif(lu));

    public string LabelEssaiReprendre => _localize("Essai.Reprendre");

    /// <summary>Le bouton « Essayer en alternance » : un levier à essayer, et pas déjà un essai en cours.</summary>
    public bool EssaiProposable => LevierAEssayer is not null && MemoireEssai is not null && !EssaiEnCours;
    public string LabelEssaiBouton => _localize("Essai.Bouton");
    public string LabelEssaiArreter => _localize("Essai.Arreter");

    partial void OnLevierAEssayerChanged(string? value) => OnPropertyChanged(nameof(EssaiProposable));

    [RelayCommand]
    private async Task EssayerEnAlternance()
    {
        if (LevierAEssayer is { } id) await DemarrerEssaiAsync(id).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ArreterEssai() => await ArreterEssaiAsync().ConfigureAwait(true);

    /// <summary>Le clic : l'essai rattrape son plan tout de suite. C'est un geste du joueur, il porte l'invite Windows
    /// éventuelle, comme le clic qui a lancé l'essai.</summary>
    [RelayCommand]
    private async Task ReprendreEssai()
    {
        await AvancerEssaiAsync(MemoireParties?.Lire() ?? [], avecGeste: true).ConfigureAwait(true);
        OnPropertyChanged(nameof(EssaiEnAttente));
    }

    /// <summary>Le clic : la première partie l'a avec. Un levier déjà posé par DrDXT part de « posé ».</summary>
    internal async Task<bool> DemarrerEssaiAsync(string id)
    {
        if (MemoireEssai is null || !PeutAlterner(id) || LevierParId?.Invoke(id) is not { } levier || MemoireEssai.Lire() is not null)
            return false;
        var dejaPose = _journalLeviers?.Lire().Any(s => s.TweakId == id) == true;
        if (!dejaPose && !await PoserPourLEssaiAsync(levier).ConfigureAwait(true)) return false;
        MemoireEssai.Ecrire(new EssaiEnAlternance(id, dejaPose, true, Horloge(), DerniereBascule: Horloge()));
        DireEssai(MemoireParties?.Lire() ?? []);
        OnPropertyChanged(nameof(EssaiEnCours));
        OnPropertyChanged(nameof(EssaiProposable));
        return true;
    }

    /// <summary>Le joueur arrête : l'état d'origine revient (le clic porte l'invite Windows éventuelle).</summary>
    internal async Task ArreterEssaiAsync()
    {
        // Une bascule de fin de partie en cours se termine avant l'arrêt, qui lit alors l'état réel.
        await _verrouEssai.WaitAsync().ConfigureAwait(true);
        try { await ArreterEssaiSousVerrouAsync().ConfigureAwait(true); }
        finally { _verrouEssai.Release(); }
    }

    private readonly SemaphoreSlim _verrouEssai = new(1, 1);

    // Le fichier de l'essai vit sous le profil du joueur, que tout programme du compte peut écrire : forgé avec une origine
    // « posé », il faisait poser n'importe quel levier du catalogue au moment où le joueur croyait arrêter. Un levier qui n'a
    // pas le droit d'alterner n'est ni posé ni rendu ici ; l'essai s'efface.
    private async Task ArreterEssaiSousVerrouAsync()
    {
        if (MemoireEssai?.Lire() is not { } e) return;
        if (PeutAlterner(e.LevierId) && LevierParId?.Invoke(e.LevierId) is { } levier && e.PoseMaintenant != e.PoseALOrigine)
        {
            var ok = e.PoseALOrigine ? await PoserPourLEssaiAsync(levier).ConfigureAwait(true) : await RendrePourLEssaiAsync(levier).ConfigureAwait(true);
            if (!ok) { LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.ArretRate"), Titre(e.LevierId)); return; }
        }
        MemoireEssai.Ecrire(null);
        LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.Arrete"), Titre(e.LevierId));
        OnPropertyChanged(nameof(EssaiEnCours));
        OnPropertyChanged(nameof(EssaiProposable));
    }

    /// <summary>La partie porte l'état de l'essai qui l'a vue jouer. Seulement si elle a commencé après la
    /// dernière bascule (une partie à cheval ne compte d'aucun côté). En pause, l'état est connu (rien n'a été
    /// touché), la partie compte. Un redémarrage depuis la dernière bascule rend l'état incertain (un levier de
    /// carte ne survit pas au redémarrage) : rien n'est marqué, l'essai s'arrête au prochain passage.</summary>
    internal PartieRelevee MarquerEssai(PartieRelevee p)
        => MemoireEssai?.Lire() is { } lu && Effectif(lu) is { } e && p.Debut >= e.Depuis && !RedemarreDepuis(e) && SelonLePlan(e)
            ? p with { Essai = AlternanceDesEssais.Marque(e.LevierId, e.PoseMaintenant) }
            : p;

    /// <summary>Le levier est dans l'état que les blocs A-B-B-A prévoient pour la prochaine partie. Une partie jouée hors du
    /// plan ne compte d'aucun côté : marquée de l'état courant, elle avancerait les blocs sans que le plan soit suivi.</summary>
    private bool SelonLePlan(EssaiEnAlternance e)
        => AlternanceDesEssais.Suivante(MemoireParties?.Lire() ?? [], e.LevierId, e.PoseALOrigine, e.Debut).Pose == e.PoseMaintenant;

    private bool RedemarreDepuis(EssaiEnAlternance e) => DemarrageDuPc() > e.Depuis;

    /// <summary>L'essai tel qu'il est vraiment. Après un redémarrage, un levier volatil (<see cref="LeviersVolatils"/>)
    /// est « pas posé », depuis le démarrage : l'essai continue de ce côté, sans rien écrire. Sinon l'essai tel quel.</summary>
    private EssaiEnAlternance Effectif(EssaiEnAlternance e)
        => RedemarreDepuis(e) && LeviersVolatils.Contient(e.LevierId)
            ? e with { PoseMaintenant = false, EnPause = false, DerniereBascule = DemarrageDuPc() }
            : e;

    /// <summary>Après une partie : l'état de la suivante. Poser ou rendre se fait sans invite Windows, ou pas du tout.</summary>
    internal async Task AvancerEssaiAsync(IReadOnlyList<PartieRelevee> parties, bool avecGeste = false)
    {
        await _verrouEssai.WaitAsync().ConfigureAwait(true);
        try { await AvancerEssaiSansFauteAsync(parties, avecGeste).ConfigureAwait(true); }
        catch (Exception)
        {
            // Une faute ne se perd plus dans une tâche jamais observée. Elle a pu tomber après la
            // bascule, l'état réel n'est plus sûr ; l'essai s'arrête (en pause, ses parties seraient marquées d'un faux état).
            try
            {
                if (MemoireEssai?.Lire() is { } e)
                {
                    MemoireEssai.Ecrire(null);
                    LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.Incertain"), Titre(e.LevierId));
                    OnPropertyChanged(nameof(EssaiEnCours));
                    OnPropertyChanged(nameof(EssaiProposable));
                }
            }
            catch (Exception) { /* mémoire de l'essai illisible : aucun essai n'est repris, rien n'est affirmé */ }
        }
        finally { _verrouEssai.Release(); }
    }

    private async Task AvancerEssaiSansFauteAsync(IReadOnlyList<PartieRelevee> parties, bool avecGeste)
    {
        if (MemoireEssai?.Lire() is not { } lu) return;
        // Le fichier de l'essai vit sous le profil du joueur, que tout programme du compte peut écrire : un levier qui n'a pas
        // le droit d'alterner (AutoHot) n'est jamais posé, quel que soit ce que le fichier dit ; l'essai est effacé.
        if (!PeutAlterner(lu.LevierId)) { MemoireEssai.Ecrire(null); return; }
        // Un levier volatil effacé par le redémarrage se note tout de suite, même là où rien ne peut être posé
        // (le relevé de fond) : l'essai dit l'état vrai au lieu de rester figé sur « posé ».
        var e = Effectif(lu);
        if (e != lu) MemoireEssai.Ecrire(e);
        if (LevierParId?.Invoke(e.LevierId) is not { } levier) return;
        // Après un redémarrage, l'état réel du levier n'est plus sûr ; l'essai s'arrête honnêtement.
        if (RedemarreDepuis(e))
        {
            // Un plan d'alimentation ou un réglage du pilote survit au redémarrage. L'essai ne s'efface
            // plus en laissant le levier posé (et un essai relancé l'aurait pris pour l'origine) : il rend d'abord l'origine,
            // sans invite Windows ; sinon il attend, et « Arrêter l'essai » la rend.
            if (e.PoseMaintenant != e.PoseALOrigine)
            {
                if (e.PoseALOrigine && JeuOuvertPourLEssai()) return;
                if (HelperJoignableSansUac is { } peut && levier.RequiresElevation && !await peut().ConfigureAwait(true))
                {
                    MemoireEssai.Ecrire(e with { EnPause = true });
                    LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.RedemarrageARendre"), Titre(e.LevierId));
                    return;
                }
                await ArreterEssaiSousVerrouAsync().ConfigureAwait(true);
                if (MemoireEssai.Lire() is not null) return;   // rendu raté : « ArretRate » est dit, nouvel essai au passage suivant
            }
            MemoireEssai.Ecrire(null);
            LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.Redemarrage"), Titre(e.LevierId));
            OnPropertyChanged(nameof(EssaiEnCours));
            OnPropertyChanged(nameof(EssaiProposable));
            return;
        }
        var (pose, fini, verdict) = AlternanceDesEssais.Suivante(parties, e.LevierId, e.PoseALOrigine, e.Debut);
        if (pose != e.PoseMaintenant)
        {
            // Rien ne se pose sans clic pendant qu'un jeu tourne (une autre partie lancée aussitôt, un second jeu) : l'essai
            // garde son état, et la partie suivante le refait avancer.
            if (pose && JeuOuvertPourLEssai()) return;
            if (!avecGeste && HelperJoignableSansUac is { } joignable && levier.RequiresElevation && !await joignable().ConfigureAwait(true))
            {
                MemoireEssai.Ecrire(e with { EnPause = true });
                LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.Pause"), Titre(e.LevierId));
                return;
            }
            var dit = LigneEssai;
            var ok = pose ? await PoserPourLEssaiAsync(levier).ConfigureAwait(true) : await RendrePourLEssaiAsync(levier).ConfigureAwait(true);
            // « Annuler » pendant la bascule a arrêté l'essai ; ce qui vient d'être posé repart, rien ne revit.
            if (MemoireEssai.Lire() is null)
            {
                if (ok && pose) await RendrePourLEssaiAsync(levier).ConfigureAwait(true);
                return;
            }
            if (!ok)
            {
                MemoireEssai.Ecrire(e with { EnPause = true });
                // La pose a pu dire pourquoi elle n'a rien gardé ; « pause » ne l'efface pas.
                if (LigneEssai == dit) LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.Pause"), Titre(e.LevierId));
                return;
            }
        }
        if (fini)
        {
            MemoireEssai.Ecrire(null);
            LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize(verdict switch
            {
                VerdictEnJeu.Mieux => "Essai.Fini.Mieux",
                VerdictEnJeu.Pire => "Essai.Fini.Pire",
                VerdictEnJeu.NonConcluant => "Essai.Fini.NonConcluant",
                _ => "Essai.Fini.Pareil",
            }), Titre(e.LevierId))
                // La mesure qui a jugé se dit quand ce n'est pas celle du levier.
                + (AlternanceDesEssais.JugeSurLesImagesFauteDeLatence(parties.Where(p => p.Debut >= e.Debut).ToList(), e.LevierId)
                    ? " " + _localize("Essai.JugeSurLesImages") : "");
            OnPropertyChanged(nameof(EssaiEnCours));
        OnPropertyChanged(nameof(EssaiProposable));
            return;
        }
        MemoireEssai.Ecrire(e with { PoseMaintenant = pose, EnPause = false, DerniereBascule = pose != e.PoseMaintenant ? Horloge() : e.DerniereBascule });
        DireEssai(parties);
    }

    private bool JeuOuvertPourLEssai() => (JeuOuvertPourLeTest ?? _gameLocator.JeuOuvert)();

    /// <summary>Le joueur annule le levier en essai depuis « Ce que DrDXT a posé » : l'essai s'arrête,
    /// rien ne le repose.</summary>
    internal void ArreterEssaiSurAnnulation(string id)
    {
        if (MemoireEssai?.Lire() is not { } e || e.LevierId != id) return;
        MemoireEssai.Ecrire(null);
        LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.Arrete"), Titre(id));
        OnPropertyChanged(nameof(EssaiEnCours));
        OnPropertyChanged(nameof(EssaiProposable));
    }

    private void DireEssai(IReadOnlyList<PartieRelevee> parties)
    {
        OnPropertyChanged(nameof(EssaiEnAttente));
        if (MemoireEssai?.Lire() is not { } e) return;
        if (!SelonLePlan(Effectif(e)))
        {
            LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.EnAttente"), Titre(e.LevierId),
                parties.OrderBy(p => p.Debut).LastOrDefault()?.Jeu is { Length: > 0 } jeu ? NomDuJeu(jeu) : _localize("Essai.TonJeu"));
            return;
        }
        parties = parties.Where(p => p.Debut >= e.Debut).ToList();
        var avec = parties.Count(p => p.Essai == AlternanceDesEssais.Marque(e.LevierId, true));
        var sans = parties.Count(p => p.Essai == AlternanceDesEssais.Marque(e.LevierId, false));
        LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.EnCours"), Titre(e.LevierId), avec, sans,
            _localize(e.PoseMaintenant ? "Reglage.Allume" : "Reglage.Eteint"));
    }

    private async Task<bool> PoserPourLEssaiAsync(ITweak levier)
    {
        // L'origine n'est notée qu'au journal des leviers, plus bas : le helper la garde jusque-là.
        using var portee = PorteeDePose.Ouvrir();
        var r = await levier.ApplyAsync().ConfigureAwait(true);
        if (!r.IsOk || r.Value is null) return false;
        var report = new ApplyReport([new ApplyOutcome(levier.Id, true, false, r.Value, null)], null);
        // La désinstallation ne rend que ce que le manifeste signé porte : l'origine y entre avant que la pose ne soit gardée.
        // Sans garde possible, seule une origine que la désinstallation remet d'elle-même à l'automatique est essayée.
        var gardes = _manifestMapper.ToRevertOnly(report, pourUnEssai: true);
        var (cle, raison) = (default(string), default(string));
        if (gardes.Count > 0)
        {
            if (await _desiredStateStore.CommitAsync(new DesiredState(Array.Empty<AppliedTweak>(), gardes)).ConfigureAwait(true) is { IsOk: false } refus)
                (cle, raison) = ("Essai.PasInscrit", RaisonLisible.Dire(refus, _localize));
        }
        else if (!AppliedTweakManifestMapper.RemiseALAutomatiqueALaDesinstallation(r.Value.OriginalValueJson))
            (cle, raison) = ("Essai.PasRendable", _localize("Essai.PasRendable.Raison"));
        if (cle is not null)
        {
            if (await levier.RevertAsync(r.Value).ConfigureAwait(true) is { IsOk: false })
            {
                // Resté posé : au journal, il reste visible et s'annule depuis « Ce que DrDXT a posé » ; hors du journal, aucune
                // ligne ne l'annule, et l'écran ne la promet pas.
                var inscrit = _journalLeviers?.Enregistrer(report) == true;
                if (inscrit) await portee.FermerAsync().ConfigureAwait(true);
                ChargerLeviersPoses();
                cle = inscrit ? "Essai.PasInscritResteJournal" : "Essai.ResteSansJournal";
            }
            LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize(cle), Titre(levier.Id), raison);
            return false;
        }
        _journalLeviers?.Enregistrer(report);
        // Un journal qui n'a rien gardé laisserait un levier posé, invisible et jamais rendu ; il repart.
        if (_journalLeviers is not null && _journalLeviers.Lire().All(s => s.TweakId != levier.Id))
        {
            if (await levier.RevertAsync(r.Value).ConfigureAwait(true) is { IsOk: false })
                LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.ResteSansJournal"), Titre(levier.Id));
            return false;
        }
        if (_journalLeviers is not null) await portee.FermerAsync().ConfigureAwait(true);
        ChargerLeviersPoses();
        // Chaque pose de l'essai se date, comme celles d'Optimiser : la garde d'incident la couvre.
        NoterPoses([levier.Id], Horloge());
        return true;
    }

    private async Task<bool> RendrePourLEssaiAsync(ITweak levier)
    {
        // Plus de trace au journal = déjà rendu (bouton Annuler) ; rien à rendre, pas un échec.
        // Seulement si le journal se lit ; illisible, rien ne dit que le levier est rendu.
        if (_journalLeviers?.Lire().FirstOrDefault(s => s.TweakId == levier.Id) is not { } snapshot) return _journalLeviers?.Lisible() ?? true;
        var r = await levier.RevertAsync(snapshot).ConfigureAwait(true);
        if (!r.IsOk) return false;
        // Rendu sur la machine : l'essai continue ; un journal non réécrit se dit (sinon le scan suivant croirait à une régression).
        if (!_journalLeviers.Retirer(levier.Id)) MessageLeviers = _localize("Leviers.JournalPasMisAJour");
        // Sa garde sort du manifeste. Restée, la désinstallation réécrirait une origine déjà en place : rien de faux, c'est dit.
        if (await RetirerDuManifesteAsync(levier.Id).ConfigureAwait(true) is { IsOk: false } sorti)
            MessageLeviers = RaisonLisible.OuIncertain(sorti, string.Format(CultureInfo.CurrentCulture, _localize("Changement.PasSortiDuManifeste"),
                Titre(levier.Id), RaisonLisible.Dire(sorti, _localize)), _localize, Titre(levier.Id));
        ChargerLeviersPoses();
        return true;
    }
}
