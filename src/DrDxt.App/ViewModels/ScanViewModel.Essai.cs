using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Application.Parties;
using DrDxt.Application.Tweaks;
using DrDxt.Core;
using Pose = DrDxt.Application.Tweaks.PoseRelue;

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

    /// <summary>Seuls les leviers à chaud s'alternent : ils agissent tout de suite, sans redémarrage. Jamais un réglage de la
    /// carte que DrDXT n'écrit pas (fréquence, sous-tension, ventilateur) : Optimiser ne les propose pas, et le fichier de
    /// l'essai, écrivable par tout programme du compte, ne doit pas pouvoir les faire poser.</summary>
    internal static bool PeutAlterner(string id) => CatalogueDeCandidats.Decrire(id) is { Kind: CandidateKind.AutoHot }
        && !CandidateSelector.ReglagesCarteInterdits.Contains(id);

    public bool EssaiEnCours => MemoireEssai?.Lire() is not null;

    /// <summary>Le levier n'est pas dans l'état que les blocs prévoient pour la prochaine partie : un redémarrage efface un
    /// levier volatil (un réveil aussi, quand la relecture le dit), et sans le helper déjà lancé rien ne le repose sans clic.
    /// L'App ouverte le rattrape, par « Reprendre l'essai ».</summary>
    public bool EssaiEnAttente => MemoireEssai?.Lire() is { } lu && !SelonLePlan(Effectif(lu));

    public string LabelEssaiReprendre => _localize("Essai.Reprendre");

    /// <summary>Le bouton « Essayer en alternance » : un levier à essayer, et pas déjà un essai en cours.</summary>
    public bool EssaiProposable => LevierAEssayer is not null && MemoireEssai is not null && !EssaiEnCours;
    public string LabelEssaiBouton => _localize("Essai.Bouton");
    public string LabelEssaiArreter => _localize("Essai.Arreter");

    partial void OnLevierAEssayerChanged(string? value) => OnPropertyChanged(nameof(EssaiProposable));

    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private async Task EssayerEnAlternance()
    {
        if (LevierAEssayer is { } id) await DemarrerEssaiAsync(id).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ArreterEssai() => await ArreterEssaiAsync().ConfigureAwait(true);

    /// <summary>Le clic : l'essai rattrape son plan tout de suite. C'est un geste du joueur, il porte l'invite Windows
    /// éventuelle, comme le clic qui a lancé l'essai.</summary>
    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
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
        // « DrDXT ne change rien sans point de restauration » : la première pose d'un essai passe par la même porte
        // qu'Optimiser, quel que soit le bouton qui le lance. Les bascules suivantes de l'essai restent sous ce point. Le point
        // se crée hors du verrou de l'essai : il peut durer, et le relevé de fond écrit l'essai sous ce verrou.
        if (!dejaPose && await _safety.EnsureRestorePointAsync().ConfigureAwait(true) is { IsOk: false } point)
        {
            LigneEssai = MessageDuPoint(point);
            return false;
        }
        using var tenu = await TenirLEssaiAsync(AttenteDuVerrouEssai).ConfigureAwait(true);
        if (tenu is null) { LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.Occupe"), Titre(id)); return false; }
        // Un autre essai a pu démarrer pendant la création du point : relu sous le verrou, et une mémoire qui ne se lit pas
        // n'est pas « aucun essai ». Le clic le dit.
        if (!MemoireEssai.Libre())
        {
            LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.DejaEnCours"), Titre(id));
            return false;
        }
        if (!dejaPose && !await PoserPourLEssaiAsync(levier).ConfigureAwait(true)) return false;
        // Un essai que sa mémoire ne porte pas ne s'arrêterait ni ne s'avancerait : il ne part pas, et ce qui vient d'être posé
        // repart.
        if (MemoireEssai.Ecrire(new EssaiEnAlternance(id, dejaPose, true, Horloge(), DerniereBascule: Horloge())) is { IsOk: false })
        {
            if (!dejaPose) await RendrePourLEssaiAsync(levier).ConfigureAwait(true);
            LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.NonNote"), Titre(id));
            return false;
        }
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
        try
        {
            using var tenu = await TenirLEssaiAsync(AttenteDuVerrouEssai).ConfigureAwait(true);
            if (tenu is null)
            {
                if (MemoireEssai?.Lire() is { } occupe)
                    LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.Occupe"), Titre(occupe.LevierId));
                return;
            }
            await ArreterEssaiSousVerrouAsync().ConfigureAwait(true);
        }
        finally { _verrouEssai.Release(); }
    }

    private readonly SemaphoreSlim _verrouEssai = new(1, 1);

    /// <summary>L'App et le relevé de fond tournent ensemble et changent le même essai : chacun le lit, le change et l'écrit
    /// sous ce verrou, commun aux deux processus (<see cref="MemoireDeLEssai.CheminDuVerrou"/>). <c>null</c> = pas de verrou
    /// (tests).</summary>
    internal Func<IDisposable?>? PrendreVerrouEssai { get; set; }

    /// <summary>Ce qu'un clic attend que l'autre processus finisse : le relevé de fond ne tient l'essai que le temps d'une
    /// écriture.</summary>
    internal TimeSpan AttenteDuVerrouEssai { get; set; } = TimeSpan.FromSeconds(3);

    private sealed class SansVerrou : IDisposable { public void Dispose() { } }

    /// <summary>Une avancée de l'essai tient son verrou dans ce processus (sur le fil de l'écran, comme l'annulation).</summary>
    private bool _avanceeEnCours;

    /// <summary>Le verrou de l'essai, ou <c>null</c> si l'autre processus le tient encore après <paramref name="attente"/>.</summary>
    private async Task<IDisposable?> TenirLEssaiAsync(TimeSpan attente)
    {
        if (PrendreVerrouEssai is not { } prendre) return new SansVerrou();
        var fin = DateTime.UtcNow + attente;
        while (true)
        {
            if (prendre() is { } tenu) return tenu;
            if (DateTime.UtcNow >= fin) return null;
            await Task.Delay(100).ConfigureAwait(true);
        }
    }

    private string JeuDe(IReadOnlyList<PartieRelevee> parties)
        => parties.OrderBy(p => p.Debut).LastOrDefault()?.Jeu is { Length: > 0 } jeu ? NomDuJeu(jeu) : _localize("Essai.TonJeu");

    // Le fichier de l'essai vit sous le profil du joueur, que tout programme du compte peut écrire : forgé avec une origine
    // « posé », il faisait poser n'importe quel levier du catalogue au moment où le joueur croyait arrêter. Un levier qui n'a
    // pas le droit d'alterner n'est ni posé ni rendu ici ; l'essai s'efface, et la ligne ne dit pas « l'état d'avant est revenu ».
    // La mémoire peut avoir manqué une bascule (l'App tuée entre la pose et son écriture, ou l'écriture refusée) : l'arrêt juge
    // sur l'état réel, relu, et sur la mémoire seulement quand rien ne relit le levier.
    private async Task ArreterEssaiSousVerrouAsync()
    {
        if (MemoireEssai?.Lire() is not { } e) return;
        var levier = PeutAlterner(e.LevierId) ? LevierDeLEssai(e.LevierId) : null;
        var reelle = levier is null ? null : PoseReelle(e.LevierId);
        var nonRelu = reelle is null && VerifierLaPose is not null && levier is not null;
        // Rien ne relit l'état : rien n'est écrit à l'aveugle, et la ligne le dit (« Annuler » reste possible).
        var sansRelecture = nonRelu;
        _renduNonRelu = false;
        _changeDepuis = false;
        if (levier is not null && !nonRelu && (reelle ?? e.PoseMaintenant) != e.PoseALOrigine)
        {
            var ok = e.PoseALOrigine ? await PoserPourLEssaiAsync(levier).ConfigureAwait(true) : await RendrePourLEssaiAsync(levier, arret: true).ConfigureAwait(true);
            nonRelu = _renduNonRelu;
            if (!ok) { LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.ArretRate"), Titre(e.LevierId)); return; }
        }
        if (MemoireEssai.Ecrire(null) is { IsOk: false })
        {
            LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.ArretNonNote"), Titre(e.LevierId));
            return;
        }
        LigneEssai = string.Format(CultureInfo.CurrentCulture,
            _localize(levier is null ? "Essai.ArreteSansRien" : _changeDepuis ? "Essai.ArreteChangeDepuis"
                : sansRelecture ? "Essai.ArreteSansRelecture" : nonRelu ? "Essai.ArreteNonRelu" : "Essai.Arrete"),
            Titre(e.LevierId));
        OnPropertyChanged(nameof(EssaiEnCours));
        OnPropertyChanged(nameof(EssaiProposable));
    }

    /// <summary>La partie porte l'état de l'essai qui l'a vue jouer. Seulement si elle a commencé après la
    /// dernière bascule (une partie à cheval ne compte d'aucun côté). En pause, l'état est connu (rien n'a été
    /// touché), la partie compte. Un redémarrage depuis la dernière bascule rend l'état incertain (un levier de
    /// carte ne survit pas au redémarrage) : rien n'est marqué, l'essai s'arrête au prochain passage.</summary>
    internal PartieRelevee MarquerEssai(PartieRelevee p)
    {
        // L'autre processus change l'essai à cet instant : l'état de la partie n'est pas sûr, elle ne compte pas.
        using var tenu = PrendreVerrouEssai is { } prendre ? prendre() : new SansVerrou();
        return tenu is not null && MemoireEssai?.Lire() is { } lu && Effectif(lu) is { } e && p.Debut >= e.Depuis && !RedemarreDepuis(e)
               && !IncertainApresReveil(e) && SelonLePlan(e)
            ? p with { Essai = AlternanceDesEssais.Marque(e.LevierId, e.PoseMaintenant) }
            : p;
    }

    /// <summary>Le levier est dans l'état que les blocs A-B-B-A prévoient pour la prochaine partie. Une partie jouée hors du
    /// plan ne compte d'aucun côté : marquée de l'état courant, elle avancerait les blocs sans que le plan soit suivi.</summary>
    private bool SelonLePlan(EssaiEnAlternance e)
        => AlternanceDesEssais.Suivante(PartiesPourLEssai(MemoireParties?.Lire() ?? []), e.LevierId, e.PoseALOrigine, e.Debut,
            MemoireJeuChoisi?.Lire()?.Exe).Pose == e.PoseMaintenant;

    /// <summary>L'anneau des 20 dernières parties, plus les parties d'essai gardées à part (<see cref="PartiesMemorisees.PartiesDEssai"/>) :
    /// le verdict et le rang A-B-B-A se comptent sur toutes les parties de l'essai, pas sur celles que l'anneau a gardées.</summary>
    private IReadOnlyList<PartieRelevee> PartiesPourLEssai(IReadOnlyList<PartieRelevee> anneau)
    {
        var essais = MemoireParties?.PartiesDEssai() ?? [];
        return essais.Count == 0 ? anneau
            : [.. anneau.Concat(essais).DistinctBy(p => (p.Jeu, p.Debut)).OrderByDescending(p => p.Debut)];
    }

    private bool RedemarreDepuis(EssaiEnAlternance e) => DemarrageDuPc() > e.Depuis;

    /// <summary>L'essai tel qu'il est vraiment. Après un redémarrage, un levier volatil (<see cref="LeviersVolatils"/>)
    /// est « pas posé », depuis le démarrage : l'essai continue de ce côté, sans rien écrire. Après une sortie de veille, rien
    /// n'est supposé : posé, il se relit, et relu rendu, il est « pas posé » depuis le réveil. Sinon l'essai tel quel.</summary>
    private EssaiEnAlternance Effectif(EssaiEnAlternance e)
    {
        if (!LeviersVolatils.Contient(e.LevierId)) return e;
        if (RedemarreDepuis(e)) return e with { PoseMaintenant = false, EnPause = false, DerniereBascule = DemarrageDuPc() };
        return e.PoseMaintenant && ReveilDepuis(e) is { } reveil && PoseApresReveil(e.LevierId, reveil) == false
            ? e with { PoseMaintenant = false, EnPause = false, DerniereBascule = reveil }
            : e;
    }

    /// <summary>La dernière sortie de veille, ou null (non lue) ; l'App branche <see cref="SortieDeVeilleEnCache"/>.</summary>
    internal Func<DateTimeOffset?> DerniereSortieDeVeille { get; set; } = () => null;

    private static (DateTimeOffset Lu, DateTimeOffset? Valeur) _veilleLue;

    // Le journal Système, relu au plus toutes les trente secondes : les propriétés de l'essai se relisent souvent.
    internal static DateTimeOffset? SortieDeVeilleEnCache()
    {
        var maintenant = DateTimeOffset.UtcNow;
        if (maintenant - _veilleLue.Lu > TimeSpan.FromSeconds(30))
            _veilleLue = (maintenant, DrDxt.Detection.Hardware.SortieDeVeille.Derniere());
        return _veilleLue.Valeur;
    }

    private DateTimeOffset? ReveilDepuis(EssaiEnAlternance e) => DerniereSortieDeVeille() is { } r && r > e.Depuis ? r : null;

    private (string Id, DateTimeOffset Reveil, bool? Pose)? _releveApresReveil;

    /// <summary>L'état relu après un réveil, une fois par réveil.</summary>
    private bool? PoseApresReveil(string id, DateTimeOffset reveil)
    {
        if (_releveApresReveil is { } r && r.Id == id && r.Reveil == reveil) return r.Pose;
        var pose = PoseReelle(id);
        _releveApresReveil = (id, reveil, pose);
        return pose;
    }

    /// <summary>Réveillé depuis la dernière bascule, levier volatil posé dont l'état ne se relit pas : la partie ne compte
    /// d'aucun côté.</summary>
    private bool IncertainApresReveil(EssaiEnAlternance e)
        => LeviersVolatils.Contient(e.LevierId) && e.PoseMaintenant && ReveilDepuis(e) is { } reveil && PoseApresReveil(e.LevierId, reveil) is null;

    /// <summary>Après une partie : l'état de la suivante. Poser ou rendre se fait sans invite Windows, ou pas du tout.</summary>
    internal async Task AvancerEssaiAsync(IReadOnlyList<PartieRelevee> parties, bool avecGeste = false)
    {
        parties = PartiesPourLEssai(parties);
        await _verrouEssai.WaitAsync().ConfigureAwait(true);
        IDisposable? tenu = null;
        try
        {
            // Sans geste (fin de partie), l'essai occupé ailleurs avance à la partie suivante ; un clic attend un peu.
            tenu = await TenirLEssaiAsync(avecGeste ? AttenteDuVerrouEssai : TimeSpan.Zero).ConfigureAwait(true);
            if (tenu is null)
            {
                if (avecGeste && MemoireEssai?.Lire() is { } occupe)
                    LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.Occupe"), Titre(occupe.LevierId));
                return;
            }
            _avanceeEnCours = true;
            await AvancerEssaiSansFauteAsync(parties, avecGeste).ConfigureAwait(true);
        }
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
        finally
        {
            _avanceeEnCours = false;
            tenu?.Dispose();
            _verrouEssai.Release();
        }
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
        if (LevierDeLEssai(e.LevierId) is not { } levier) return;
        // Après un redémarrage, l'état réel du levier n'est plus sûr ; l'essai s'arrête honnêtement.
        if (RedemarreDepuis(e))
        {
            // Un plan d'alimentation ou un réglage du pilote survit au redémarrage. L'essai ne s'efface
            // plus en laissant le levier posé (et un essai relancé l'aurait pris pour l'origine) : il rend d'abord l'origine,
            // sans invite Windows ; sinon il attend, et « Arrêter l'essai » la rend.
            if (e.PoseMaintenant != e.PoseALOrigine)
            {
                // Ni pose ni retrait pendant qu'un jeu tourne : la fin de partie suivante rend l'origine. Le clic le dit.
                if (JeuOuvertPourLEssai())
                {
                    if (avecGeste) LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.FermeLeJeu"), Titre(e.LevierId), JeuDe(parties));
                    return;
                }
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
        var (pose, fini, verdict) = AlternanceDesEssais.Suivante(parties, e.LevierId, e.PoseALOrigine, e.Debut,
            MemoireJeuChoisi?.Lire()?.Exe);
        if (pose != e.PoseMaintenant)
        {
            // Rien ne se pose ni ne se retire pendant qu'un jeu tourne (une autre partie lancée aussitôt, un second jeu) : l'essai
            // garde son état, et la partie suivante le refait avancer. Le clic le dit.
            if (JeuOuvertPourLEssai())
            {
                if (avecGeste) LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.FermeLeJeu"), Titre(e.LevierId), JeuDe(parties));
                return;
            }
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
        if (MemoireEssai.Ecrire(e with { PoseMaintenant = pose, EnPause = false, DerniereBascule = pose != e.PoseMaintenant ? Horloge() : e.DerniereBascule })
            is { IsOk: false })
        {
            // La bascule est faite sur la machine, pas dans la mémoire : l'arrêt relira l'état réel.
            LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.BasculeNonNotee"), Titre(e.LevierId));
            return;
        }
        DireEssai(parties);
    }

    private bool JeuOuvertPourLEssai() => (JeuOuvertPourLeTest ?? _gameLocator.JeuOuvert)();

    private ITweak? LevierDeLEssai(string id) => (LevierParId ?? LevierDeLEssaiParId)?.Invoke(id);

    /// <summary>Le joueur annule le levier en essai depuis « Ce que DrDXT a posé » : l'essai s'arrête,
    /// rien ne le repose. La ligne dit l'annulation : la valeur d'avant DrDXT n'est pas l'état d'avant l'essai quand le levier
    /// était déjà posé avant lui.</summary>
    internal async Task ArreterEssaiSurAnnulationAsync(string id)
    {
        // Une avancée de ce processus (bascule de fin de partie) relit l'essai après sa pose et rend ce qu'elle vient de
        // poser : attendre son verrou la laisserait reposer le levier par-dessus l'annulation. Sinon, le verrou s'attend
        // (l'autre processus écrit peut-être l'essai) ; passé l'attente, l'arrêt voulu par le joueur passe quand même.
        using var tenu = _avanceeEnCours ? null : await TenirLEssaiAsync(AttenteDuVerrouEssai).ConfigureAwait(true);
        if (MemoireEssai?.Lire() is not { } e || e.LevierId != id) return;
        MemoireEssai.Ecrire(null);
        LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.ArreteParAnnulation"), Titre(id));
        OnPropertyChanged(nameof(EssaiEnCours));
        OnPropertyChanged(nameof(EssaiProposable));
    }

    private void DireEssai(IReadOnlyList<PartieRelevee> parties)
    {
        parties = PartiesPourLEssai(parties);
        OnPropertyChanged(nameof(EssaiEnAttente));
        if (MemoireEssai?.Lire() is not { } e) return;
        if (!SelonLePlan(Effectif(e)))
        {
            LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.EnAttente"), Titre(e.LevierId), JeuDe(parties));
            return;
        }
        parties = parties.Where(p => p.Debut >= e.Debut).ToList();
        var avec = parties.Count(p => p.Essai == AlternanceDesEssais.Marque(e.LevierId, true));
        var sans = parties.Count(p => p.Essai == AlternanceDesEssais.Marque(e.LevierId, false));
        LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.EnCours"), Titre(e.LevierId), avec, sans,
            _localize(e.PoseMaintenant ? "Reglage.Allume" : "Reglage.Eteint"));
    }

    /// <summary>Le levier est-il posé, relu sur la machine ? Posé : au journal des leviers et relu en place ; rendu : absent du
    /// journal (un rendu l'en retire) ou relu rendu ; null : journal illisible, lecture non branchée ou état non relu.</summary>
    private bool? PoseReelle(string id)
    {
        if (_journalLeviers is null || !_journalLeviers.Lisible()) return null;
        if (_journalLeviers.Lire().FirstOrDefault(s => s.TweakId == id) is not { } s) return false;
        return VerifierLaPose?.Invoke(s) switch
        {
            EtatApplique.Applied => true,
            EtatApplique.NotApplied => false,
            _ => null,
        };
    }

    /// <summary>Ce que la machine porte après une pose, relu par ce qui lit vraiment le réglage ; null = non branché.</summary>
    internal Func<TweakSnapshot, EtatApplique>? VerifierLaPose { get; set; }

    private async Task<bool> PoserPourLEssaiAsync(ITweak levier)
    {
        // L'origine n'est notée qu'au journal des leviers, plus bas : le helper la garde jusque-là.
        using var portee = PorteeDePose.Ouvrir();
        var r = await levier.ApplyAsync().ConfigureAwait(true);
        if (!r.IsOk || r.Value is null)
        {
            // Refusée, ou sans réponse du helper (l'écriture a pu se faire) : dit, pas muet.
            LigneEssai = RaisonLisible.Echec(r, string.Format(CultureInfo.CurrentCulture, _localize("Essai.NonPose"), Titre(levier.Id)), _localize);
            return false;
        }
        // Une pose que la machine ne confirme pas ne lance pas d'essai : les parties « avec » mesureraient autre chose que le
        // levier. Même règle que l'autopilote : seul « en place » autorise la mesure.
        if (VerifierLaPose?.Invoke(r.Value) is { } verdict && verdict != EtatApplique.Applied)
        {
            var rendu = await levier.RevertAsync(r.Value).ConfigureAwait(true);
            LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize(rendu.IsOk ? "Essai.PoseNonRelue" : "Essai.ResteSansJournal"),
                Titre(levier.Id));
            return false;
        }
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

    /// <summary>Le dernier rendu de l'arrêt est passé sans relecture lisible.</summary>
    private bool _renduNonRelu;

    /// <summary>Le dernier rendu de l'arrêt a trouvé le réglage changé depuis par le joueur : rien n'a été réécrit.</summary>
    private bool _changeDepuis;

    /// <param name="arret">Pour l'arrêt : une relecture qui reste illisible (ni en place, ni rendue) ne fait pas échouer l'arrêt,
    /// le rendu du levier a réussi ; il se dit « non relu », comme Annuler. Pour une bascule, seul « rendu » relu autorise la
    /// partie « sans », qui mesurerait sinon peut-être encore le levier.</param>
    private async Task<bool> RendrePourLEssaiAsync(ITweak levier, bool arret = false)
    {
        // Plus de trace au journal = déjà rendu (bouton Annuler) ; rien à rendre, pas un échec.
        // Seulement si le journal se lit ; illisible, rien ne dit que le levier est rendu.
        if (_journalLeviers?.Lire().FirstOrDefault(s => s.TweakId == levier.Id) is not { } snapshot) return _journalLeviers?.Lisible() ?? true;
        // Changé depuis par le joueur : rien n'est réécrit par-dessus. Une bascule se met en pause ; l'arrêt s'achève, la
        // valeur du joueur reste, et plus rien de DrDXT n'y est à garder. Revenu à l'origine : rien à écrire.
        var etat = EtatDeLaPose(snapshot);
        if (etat == Pose.ChangeDepuis && !arret)
        {
            LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.ChangeDepuis"), Titre(levier.Id));
            return false;
        }
        _changeDepuis = etat == Pose.ChangeDepuis;
        if (etat == Pose.DeDrDxt && await levier.RevertAsync(snapshot).ConfigureAwait(true) is { IsOk: false }) return false;
        // Même règle que la pose : seule la valeur d'avant relue autorise la partie « sans », qui mesurerait sinon encore le
        // levier. Il reste au journal et à « Annuler » : rien ne dit qu'il est rendu.
        var relu = etat == Pose.DeDrDxt ? Relu(snapshot) : EtatApplique.NotApplied;
        // Une lecture passagère ratée (WMI, pilote occupé) se relit une fois.
        if (relu is not (EtatApplique.Applied or EtatApplique.NotApplied)) relu = Relu(snapshot);
        if (relu != EtatApplique.NotApplied && !(arret && relu != EtatApplique.Applied))
        {
            LigneEssai = string.Format(CultureInfo.CurrentCulture, _localize("Essai.RenduNonRelu"), Titre(levier.Id));
            return false;
        }
        if (relu != EtatApplique.NotApplied) _renduNonRelu = true;
        // Sa garde sort du manifeste d'abord (ce que le service repose), puis du journal (ce que l'écran montre). Restée, la
        // désinstallation réécrirait une origine déjà en place : rien de faux, c'est dit.
        if (await RetirerDuManifesteAsync(levier.Id).ConfigureAwait(true) is { IsOk: false } sorti)
            MessageLeviers = RaisonLisible.OuIncertain(sorti, string.Format(CultureInfo.CurrentCulture, _localize("Changement.PasSortiDuManifeste"),
                Titre(levier.Id), RaisonLisible.Dire(sorti, _localize)), _localize, Titre(levier.Id));
        // Rendu sur la machine : l'essai continue ; un journal non réécrit se dit (sinon le scan suivant croirait à une régression).
        if (!_journalLeviers.Retirer(levier.Id)) MessageLeviers = _localize("Leviers.JournalPasMisAJour");
        ChargerLeviersPoses();
        return true;
    }
}
