using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Application.Parties;
using DrDxt.Core;
using Pose = DrDxt.Application.Tweaks.PoseRelue;

namespace DrDxt.App.ViewModels;

/// <summary>Ce que DrDXT a posé : la liste, la relecture et l'annulation depuis l'écran.</summary>
public partial class ScanViewModel
{
    /// <summary>Un réglage posé par DrDXT et gardé, tel que le journal des leviers le connaît.</summary>
    public sealed record LevierPose(string Id, string Titre, string NomAccessible)
    {
        /// <summary>Le nom que le lecteur d'écran annonce pour l'élément de liste : WPF lit ToString quand l'élément n'a pas de nom.</summary>
        public override string ToString() => NomAccessible;
    }

    public bool ALeviersPoses => LeviersPoses.Count > 0;

    [ObservableProperty] private string? _messageLeviers;

    internal void ChargerLeviersPoses()
    {
        LeviersPoses.Clear();
        var poses = _journalLeviers?.Lire() ?? [];
        foreach (var s in poses.Where(EncorePoseParDrDxt))
        {
            var titre = TitreDuLevier(s.TweakId);
            // Le lecteur d'écran annonce l'action et le réglage, pas le seul réglage.
            LeviersPoses.Add(new LevierPose(s.TweakId, titre, $"{_localize("Leviers.BoutonAnnuler")}{Docteur.DeuxPoints}{titre}"));
        }
        foreach (var o in _orphelinsDuManifeste.Where(o => LeviersPoses.All(l => l.Id != o.Id)))
        {
            var titre = string.Format(CultureInfo.CurrentCulture, _localize("Leviers.ListeSigneeSeulement"), _localize(TweakDisplayKeys.TitleKey(o.Id)));
            LeviersPoses.Add(new LevierPose(o.Id, titre, $"{_localize("Leviers.BoutonAnnuler")}{Docteur.DeuxPoints}{titre}"));
        }
        OnPropertyChanged(nameof(ALeviersPoses));
    }

    /// <summary>Dans la liste signée (ce que le service repose, ce que la désinstallation rend) mais plus dans le journal des
    /// leviers (App arrêtée entre les deux retraits, journal mis de côté) : relus à chaque scan, montrés avec leur
    /// « Annuler ». Origine : celle de la liste pour un levier gardé (RevertOnly) ; aucune pour une entrée que le service
    /// réaffirme, qui ne fait que sortir de la liste.</summary>
    private IReadOnlyList<(string Id, TweakSnapshot? Snapshot)> _orphelinsDuManifeste = [];

    /// <summary>Relit la liste signée ; rend les titres des leviers qui n'y sont plus qu'elle. Liste ou journal illisibles :
    /// rien n'est affirmé.</summary>
    internal async Task<IReadOnlyList<string>> RelireOrphelinsDuManifesteAsync()
    {
        if (_journalLeviers is not { } journal || !journal.Lisible()) return [];
        Result<DesiredState> lu;
        try { lu = await _desiredStateStore.LoadAsync().ConfigureAwait(true); }
        catch (Exception) { return []; }
        if (!lu.IsOk || lu.Value is null) return [];
        var poses = journal.Lire().Select(s => s.TweakId).ToHashSet(StringComparer.Ordinal);
        _orphelinsDuManifeste = (lu.Value.RevertOnly ?? [])
            .Select(t => (Id: t.TweakId, Snapshot: (TweakSnapshot?)new TweakSnapshot(t.TweakId, t.OriginalValueJson, t.DesiredValueJson)))
            // Une entrée que le service réaffirme porte l'origine du levier, copiée de son snapshot : seule dans la liste
            // (Optimiser interrompu avant le journal), elle est le dernier endroit qui la garde, et Annuler la rend.
            .Concat(lu.Value.Tweaks.Select(t => (Id: AppliedTweakManifestMapper.IdDeLApp(t.TweakId),
                Snapshot: AppliedTweakManifestMapper.OrigineRendable(t.OriginalValueJson)
                    ? new TweakSnapshot(AppliedTweakManifestMapper.IdDeLApp(t.TweakId), t.OriginalValueJson, t.PoseesJson)
                    : (TweakSnapshot?)null)))
            .Where(o => !poses.Contains(o.Id))
            .GroupBy(o => o.Id, StringComparer.Ordinal)
            .Select(g => g.FirstOrDefault(o => o.Snapshot is not null) is { Snapshot: not null } avecOrigine ? avecOrigine : g.First())
            .ToList();
        ChargerLeviersPoses();
        return _orphelinsDuManifeste.Select(o => _localize(TweakDisplayKeys.TitleKey(o.Id))).ToList();
    }

    /// <summary>Seulement ce qui porte encore la valeur posée par DrDXT, la règle de la désinstallation. Revenu à la valeur
    /// d'avant : rien à annuler. Modifié depuis par le joueur : pas montré (« Annuler » écraserait son choix). Valeur posée
    /// inconnue ou non relisable : montré s'il n'est pas revenu à l'origine.</summary>
    private bool EncorePoseParDrDxt(TweakSnapshot s) => EtatDeLaPose(s) == Pose.DeDrDxt;

    /// <summary>Tout rendu passe par là (<see cref="DrDxt.Application.Tweaks.TweakReadback.EtatDeLaPose"/>).</summary>
    private Pose EtatDeLaPose(TweakSnapshot s) => DrDxt.Application.Tweaks.TweakReadback.EtatDeLaPose(s, _registreGains, VerifierLaPose);

    /// <summary>Le même chemin que le retrait automatique : la valeur d'avant par le levier lui-même, puis le journal et le
    /// manifeste signé. Un refus ne retire rien et le dit. Le clic, et lui seul, peut remettre la carte AMD à l'usine.
    /// « Annulé » ne se dit que relu à l'origine (<see cref="Relu"/>).</summary>
    [RelayCommand(CanExecute = nameof(AucuneEcritureEnCours))]
    private Task AnnulerLevierAsync(string id) => AnnulerLevierAsync(id, parClic: true);

    /// <param name="parClic">Faux pour la garde d'incident, qui rend sans clic : un refus du helper (fréquence au-delà de sa
    /// bande) s'y dit, il ne devient jamais une remise à l'usine.</param>
    /// <returns>Ce que la relecture a dit après un rendu accepté ; <c>null</c> quand rien n'a été rendu.</returns>
    internal async Task<DrDxt.Application.Tweaks.EtatApplique?> AnnulerLevierAsync(string id, bool parClic)
    {
        var titre = TitreDuLevier(id);
        var orphelin = _orphelinsDuManifeste.FirstOrDefault(o => o.Id == id);
        // Dans la liste signée seulement, sans origine que l'App sache rendre : il en sort, le réglage reste tel quel.
        if (_journalLeviers?.Lire().FirstOrDefault(s => s.TweakId == id) is null && orphelin is { Id: not null, Snapshot: null })
        {
            var sortiSeul = await RetirerDuManifesteAsync(id);
            if (sortiSeul.IsOk) _orphelinsDuManifeste = _orphelinsDuManifeste.Where(o => o.Id != id).ToList();
            MessageLeviers = sortiSeul.IsOk
                ? string.Format(CultureInfo.CurrentCulture, _localize("Leviers.SortiDeLaListe"), titre)
                : string.Format(CultureInfo.CurrentCulture, _localize("Changement.PasSortiDuManifeste"), titre, RaisonLisible.Dire(sortiSeul, _localize));
            ChargerLeviersPoses();
            return null;
        }
        // Retiré entre-temps ou levier inconnu : dit, pas muet. Hors du journal mais dans la liste signée : l'origine de la
        // liste. Le repos de la carte et les gestes du docteur se trouvent par leur propre levier, hors du catalogue.
        if ((_journalLeviers?.Lire().FirstOrDefault(s => s.TweakId == id) ?? orphelin.Snapshot) is not { } snapshot
            || (LevierParId?.Invoke(id) ?? (LevierReposCarte is { } repos && repos.Id == id ? repos : null)
                ?? (DrDxt.Application.Tweaks.LevierDeGeste.EstUnGeste(id) && _helper is { } eleve ? new DrDxt.Application.Tweaks.LevierDeGeste(id, eleve) : null))
                is not { } levier)
        {
            MessageLeviers = string.Format(CultureInfo.CurrentCulture, _localize("Leviers.Introuvable"), titre);
            ChargerLeviersPoses();
            return null;
        }
        // L'export d'un paquet de pilote retiré n'est plus sous la racine du helper : il sort de la liste, et c'est dit.
        if (DrDxt.Application.Tweaks.LevierDeGeste.PaquetDe(id) is { } paquet && !ExportDuPiloteExiste(paquet))
        {
            await OublierAsync(id).ConfigureAwait(true);
            MessageLeviers = string.Format(CultureInfo.CurrentCulture, _localize("Geste.Pilote.ExportAbsent"), titre);
            return null;
        }
        // Seule la valeur de DrDXT se rend (la garde d'incident et la liste signée contournent le filtre de la liste).
        // Revenu à l'origine ou changé depuis : rien n'est écrit, il sort du manifeste et du journal.
        var etat = EtatDeLaPose(snapshot);
        var issue = etat != Pose.DeDrDxt ? new DrDxt.Application.Autopilot.AnnulationParClic.Issue(Result.Ok(), null)
            : parClic ? await DrDxt.Application.Autopilot.AnnulationParClic.AnnulerAsync(levier, snapshot, _helper)
            : new DrDxt.Application.Autopilot.AnnulationParClic.Issue(await levier.RevertAsync(snapshot), null);
        var r = issue.Resultat;
        if (!r.IsOk)
        {
            MessageLeviers = RaisonLisible.OuIncertain(r,
                string.Format(CultureInfo.CurrentCulture, _localize("Leviers.PasAnnule"), titre, RaisonLisible.Dire(r, _localize)), _localize, titre);
            return null;
        }
        // La remise à l'usine (issue.Message) est relue par la carte elle-même ; l'usine n'est pas forcément l'origine.
        var relu = issue.Message is null && etat == Pose.DeDrDxt ? Relu(snapshot) : DrDxt.Application.Tweaks.EtatApplique.NotApplied;
        if (relu == DrDxt.Application.Tweaks.EtatApplique.Applied)
        {
            MessageLeviers = string.Format(CultureInfo.CurrentCulture, _localize("Leviers.PasAnnule"), titre, _localize("Leviers.EncorePose"));
            return relu;
        }
        // Le manifeste (ce que le service repose) sort avant tout ce qui cache le levier à l'écran.
        var sorti = await RetirerDuManifesteAsync(id);
        relu = await RelireApresLaSortie(levier, snapshot, relu);
        if (relu == DrDxt.Application.Tweaks.EtatApplique.Applied)
        {
            MessageLeviers = string.Format(CultureInfo.CurrentCulture, _localize("Leviers.PasAnnule"), titre, _localize("Leviers.EncorePose"));
            return relu;
        }
        var journalReecrit = _journalLeviers?.Retirer(id) ?? true;
        // Annulé, le repos de la carte n'est plus suivi en jeu : la surveillance le croirait sinon changé depuis par le joueur.
        if (LevierReposCarte?.Id == id) ReglagesDeLaCarte?.Effacer();
        await ArreterEssaiSurAnnulationAsync(id);
        _orphelinsDuManifeste = _orphelinsDuManifeste.Where(o => o.Id != id).ToList();
        // Annulé, il n'est plus une pose de ce scan : Optimiser peut le reproposer.
        if (_posesDepuisLeScan.Remove(id)) ResultatOptimisation = null;
        await JugerCeQuiResteAPoserAsync().ConfigureAwait(true);
        MessageLeviers = string.Format(CultureInfo.CurrentCulture, _localize(etat == Pose.ChangeDepuis ? "Leviers.ChangeDepuis"
                : relu != DrDxt.Application.Tweaks.EtatApplique.NotApplied
                ? "Leviers.AnnuleNonRelu" : levier.TakesEffectAfterReboot
                    ? Differe(levier, "Leviers.AnnuleAuRedemarrage", "Leviers.AnnuleAuLancementDuJeu") : "Leviers.Annule"), titre)
            + (journalReecrit ? "" : " " + _localize("Leviers.JournalPasMisAJour"))
            + (sorti.IsOk ? "" : " " + string.Format(CultureInfo.CurrentCulture, _localize("Changement.PasSortiDuManifeste"),
                titre, RaisonLisible.Dire(sorti, _localize)))
            + (issue.Message is { } m ? " " + m : "");
        ChargerLeviersPoses();
        return relu;
    }

    /// <summary>Le titre d'un levier posé ; un geste du docteur gardé se nomme par ce qu'il a fait.</summary>
    private string TitreDuLevier(string id)
        => DrDxt.Application.Tweaks.LevierDeGeste.PaquetDe(id) is { } paquet
            ? string.Format(CultureInfo.CurrentCulture, _localize("Leviers.Geste.Pilote"), paquet)
            : DrDxt.Application.Tweaks.LevierDeGeste.JeuDe(id) is { } jeu
                ? string.Format(CultureInfo.CurrentCulture, _localize("Leviers.Geste.Dlss"), Path.GetFileNameWithoutExtension(jeu))
                : DrDxt.Application.Tweaks.LevierDeGeste.ServiceDe(id) is { } service
                    ? string.Format(CultureInfo.CurrentCulture, _localize("Leviers.Geste.Droits"), service)
                    : _localize(TweakDisplayKeys.TitleKey(id));

    /// <summary>Le levier est rendu avant de sortir de la liste signée (rendu raté : la liste garde l'origine). Le service
    /// peut le reposer entre les deux (sa passe d'après une mise à jour) : reposé après la sortie, il est rendu une fois de
    /// plus.</summary>
    private async Task<DrDxt.Application.Tweaks.EtatApplique> RelireApresLaSortie(ITweak levier, TweakSnapshot snapshot,
        DrDxt.Application.Tweaks.EtatApplique relu)
    {
        if (relu != DrDxt.Application.Tweaks.EtatApplique.NotApplied || Relu(snapshot) != DrDxt.Application.Tweaks.EtatApplique.Applied)
            return relu;
        return (await levier.RevertAsync(snapshot)).IsOk ? Relu(snapshot) : DrDxt.Application.Tweaks.EtatApplique.Applied;
    }

    /// <summary>Une annulation acceptée, relue : « annulé » ne se dit que sur NotApplied (la valeur d'avant). Applied : rien
    /// n'est rendu, journal et manifeste la gardent (« Annuler » se reclique). Illisible ou autre : rendu mais non relu, et
    /// c'est dit. Lecteur non branché : la réussite du levier fait foi.</summary>
    private DrDxt.Application.Tweaks.EtatApplique Relu(TweakSnapshot s)
        => VerifierLaPose?.Invoke(s) ?? DrDxt.Application.Tweaks.EtatApplique.NotApplied;

    private IReadOnlyList<(DateTimeOffset Quand, int Code)>? LireVidagesCarteSur()
    {
        try { return LireVidagesCarte?.Invoke(); }
        catch (Exception) { return null; }
    }

    /// <summary>Une limite de puissance posée par DrDXT disparaît au redémarrage (le BIOS remet la sienne) : lue absente du
    /// registre, elle sort du journal, et son retour n'est jamais rejoué par-dessus le BIOS. Jamais pendant son propre
    /// essai. Un levier volatil (<see cref="DrDxt.Application.Tweaks.LeviersVolatils"/>) sans lecture de registre, posé avant
    /// le dernier démarrage, n'est plus en place ; sans date de pose, rien n'est conclu.</summary>
    internal void RetirerLeviersVolatilsRendus()
    {
        if (_journalLeviers is null || LevierParId is null) return;
        var enEssai = MemoireEssai?.Lire()?.LevierId;
        var journal = _journalLeviers.Lire();
        var poses = JournalChangements?.Lire() ?? [];
        foreach (var s in journal)
        {
            if (s.TweakId == enEssai || DrDxt.Application.Tweaks.LevierDeGeste.EstUnGeste(s.TweakId) || LevierParId(s.TweakId) is not { } levier) continue;
            var encore = levier is DrDxt.Application.Tweaks.FixIt.LevierPl1TweakBase pl1 ? pl1.EncorePose(s, journal) : null;
            if (encore is null && DrDxt.Application.Tweaks.LeviersVolatils.Contient(s.TweakId)
                && poses.Where(c => c.Cle == "pose:" + s.TweakId).Select(c => (DateTimeOffset?)c.Quand).Max() is { } posele)
                encore = posele >= DemarrageDuPc();
            if (encore == false) _journalLeviers.Retirer(s.TweakId); // raté : le prochain scan retente, rien à dire au joueur
        }
    }

    /// <summary>Les arrêts passés ne refusent pas Optimiser : un
    /// réglage posé par DrDXT et suivi d'un arrêt brutal hors veille dans la semaine se rend seul (<see cref="DrDxt.Application.Autopilot.GardeDIncident"/>).
    /// Sans le helper joignable sans autorisation Windows, il se dit à rendre, jamais d'UAC sans clic.</summary>
    private async Task<IReadOnlyList<string>> RendreApresUnArretAsync()
    {
        if (JournalChangements is null || _journalLeviers is null || LevierParId is null) return [];
        var (_, arrets) = await Task.Run(LireStabiliteSure).ConfigureAwait(true);
        var vidages = await Task.Run(LireVidagesCarteSur).ConfigureAwait(true);
        var journal = JournalChangements.Lire();
        // Un geste du docteur gardé est un choix du joueur, posé sur son clic : jamais rendu par la garde.
        var poses = _journalLeviers.Lire().Select(s => s.TweakId).Where(id => !DrDxt.Application.Tweaks.LevierDeGeste.EstUnGeste(id)).Distinct()
            .Select(id => (Id: id, Pose: journal.Where(c => c.Cle == "pose:" + id).OrderByDescending(c => c.Quand).FirstOrDefault()))
            .Where(x => x.Pose is not null).Select(x => (x.Id, x.Pose!.Quand)).ToList();
        var dits = new List<string>();
        foreach (var (id, arret, vidage) in DrDxt.Application.Autopilot.GardeDIncident.ARendre(poses, arrets, vidages, DateTimeOffset.Now))
        {
            var titre = _localize(TweakDisplayKeys.TitleKey(id));
            var quand = arret.Quand.ToLocalTime().ToString(_localize("Format.JourHeure"), CultureInfo.CurrentCulture);
            var code = string.Format(CultureInfo.InvariantCulture, "0x{0:X}", arret.CodeArret);
            if (LevierParId(id) is { RequiresElevation: true } && HelperJoignableSansUac is { } joignable && !await joignable())
            {
                dits.Add(string.Format(CultureInfo.CurrentCulture, _localize("Garde.ARendre"), titre, quand));
                continue;
            }
            var changeDepuis = _journalLeviers.Lire().FirstOrDefault(s => s.TweakId == id) is { } pose && EtatDeLaPose(pose) == Pose.ChangeDepuis;
            var relu = await AnnulerLevierAsync(id, parClic: false);
            // Changé depuis par le joueur : rien n'est rendu par-dessus, et la ligne ne dit pas « rendu ».
            if (changeDepuis && !_journalLeviers.Lire().Any(s => s.TweakId == id))
                dits.Add(string.Format(CultureInfo.CurrentCulture, _localize("Garde.ChangeDepuis"), titre, quand));
            // GARDE1 : un rendu raté se dit sur la carte (il reste au journal et se retente au prochain affichage).
            else if (_journalLeviers.Lire().Any(s => s.TweakId == id))
                dits.Add(string.Format(CultureInfo.CurrentCulture, _localize("Garde.Rate"), titre, quand, MessageLeviers));
            else
            {
                // GARDE1 : le rendu se note (Optimiser ne le repose pas avant une semaine) ; au redémarrage, il le dit.
                JournalChangements.Ajouter(new Changement(Horloge(), id, null, ClePoseGarde + id));
                var auRedemarrage = LevierParId(id) is { TakesEffectAfterReboot: true };
                // GARDE2 : un vidage à chaud du pilote se dit comme tel, pas comme un arrêt brutal.
                // « La valeur d'avant est remise » ne se dit que relu à l'origine.
                dits.Add(string.Format(CultureInfo.CurrentCulture, _localize(relu is not (null or DrDxt.Application.Tweaks.EtatApplique.NotApplied)
                        ? "Garde.RenduNonRelu"
                        : vidage
                        ? (auRedemarrage ? "Garde.RenduIncidentCarteAuRedemarrage" : "Garde.RenduIncidentCarte")
                        : (auRedemarrage ? "Garde.RenduAuRedemarrage" : "Garde.Rendu")), titre, quand, code));
            }
        }
        return dits;
    }
}
