using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>La reprise après une session ou un essai interrompus, et la remise à l'usine de la carte.</summary>
public sealed partial class ScanViewModel
{
    // L'essai interrompu dont le rendu a été refusé (fréquence de la carte AMD au-delà de la bande du helper) ; null sinon.
    private DrDxt.Application.Autopilot.IRevertEnAttente? _essaiARemettreALUsine;

    /// <summary>Le constat de démarrage propose la remise à l'usine de la carte : elle ne part que sur ce clic.</summary>
    public bool RemiseUsineProposee => _essaiARemettreALUsine is not null;
    public string LabelRemiseUsine => _localize("Demarrage.RemiseUsine");

    // L'essai interrompu que le lancement n'a pas pu rendre (le helper ne tournait pas, ou un refus) ; null sinon.
    private (DrDxt.Application.Autopilot.IRevertEnAttente Journal, ITweakCatalog Catalogue)? _essaiARendre;

    /// <summary>Le constat de démarrage propose de rendre l'essai interrompu : sur ce clic, le helper peut être lancé.</summary>
    public bool RenduEssaiPropose => _essaiARendre is not null;
    public string LabelRendreEssai => _localize("Demarrage.RendreEssai");

    /// <summary>Au lancement, sans clic : l'essai d'une session interrompue est rendu si le helper tourne déjà ; sinon
    /// aucune invite Windows ne s'ouvre, le refus se dit, et le bouton le rend sur un clic.</summary>
    internal async Task RendreSessionInterrompueAsync(DrDxt.Application.Autopilot.IRevertEnAttente journal, ITweakCatalog catalogue)
    {
        using var sansGeste = SansGesteDuJoueur.Ouvrir();
        await RendreEssaiAsync(journal, catalogue).ConfigureAwait(true);
        // Ce qu'une App morte a posé dans la session sans en noter l'origine, rendu par celle-ci.
        (string? Texte, IReadOnlyList<DrDxt.ElevatedHelper.Journal.RapportDuRejeu.Ligne> Lignes) session;
        try { session = RejeuDuCompte is { } rc ? await Task.Run(rc).ConfigureAwait(true) : (null, []); }
        catch (Exception) { session = (null, []); }
        if (session.Texte is { } texteSession)
            MessageDemarrage = string.IsNullOrEmpty(MessageDemarrage) ? texteSession : MessageDemarrage + Environment.NewLine + texteSession;
        await SortirLesLeviersRendusAsync(session.Lignes).ConfigureAwait(true);
        // Ce que le helper a rendu d'une session interrompue, dit une fois, après le constat de l'App.
        string? rejeu;
        try { rejeu = RejeuLu is { } r ? await Task.Run(r.TexteNouveau).ConfigureAwait(true) : null; }
        catch (Exception) { rejeu = null; }
        if (rejeu is not null)
            MessageDemarrage = string.IsNullOrEmpty(MessageDemarrage) ? rejeu : MessageDemarrage + Environment.NewLine + rejeu;
        // Un compte standard se dit dès le lancement : sinon il ne se découvrait qu'au premier clic refusé.
        if (CompteStandard())
        {
            var compte = _localize("Demarrage.CompteStandard");
            MessageDemarrage = string.IsNullOrEmpty(MessageDemarrage) ? compte : MessageDemarrage + Environment.NewLine + compte;
        }
    }

    /// <summary>Le compte Windows du joueur est-il standard ? Une couture pour les tests.</summary>
    internal Func<bool> CompteStandard { get; set; } = DrDxt.App.Platform.CompteDuJoueur.Standard;

    /// <summary>Le rejeu du journal du compte (valeurs de la session) ; null dans les tests qui ne le portent pas.</summary>
    internal Func<Task<(string? Texte, IReadOnlyList<DrDxt.ElevatedHelper.Journal.RapportDuRejeu.Ligne> Lignes)>>? RejeuDuCompte { get; set; }

    /// <summary>Les rapports du rejeu du helper et du service ; null dans les tests qui ne les portent pas.</summary>
    internal Func<IReadOnlyList<DrDxt.ElevatedHelper.Journal.RapportDuRejeu>>? RapportsDuRejeu { get; set; }

    /// <summary>Un levier gardé qu'un rejeu a rendu (l'App arrêtée avant d'acquitter son origine) n'est plus gardé : il sort de
    /// la liste signée puis du journal, et c'est dit. Sans relecture branchée, rien n'est supposé.</summary>
    private async Task SortirLesLeviersRendusAsync(IReadOnlyList<DrDxt.ElevatedHelper.Journal.RapportDuRejeu.Ligne> duCompte)
    {
        if (_journalLeviers is not { } journal || VerifierLaPose is not { } relu) return;
        IReadOnlyList<DrDxt.ElevatedHelper.Journal.RapportDuRejeu.Ligne> lignes;
        try { lignes = [.. duCompte, .. (RapportsDuRejeu?.Invoke() ?? []).SelectMany(r => r.Lignes)]; }
        catch (Exception) { lignes = duCompte; }
        string sid;
        using (var moi = System.Security.Principal.WindowsIdentity.GetCurrent()) sid = moi.User?.Value ?? string.Empty;
        var ids = DrDxt.App.Platform.LeviersRendusParLeRejeu.Ids(journal.Lire(), lignes, relu, sid);
        foreach (var id in ids)
        {
            await RetirerDuManifesteAsync(id).ConfigureAwait(true);
            journal.Retirer(id);
        }
        if (ids.Count == 0) return;
        var texte = string.Format(CultureInfo.CurrentCulture, _localize("Leviers.RendusParLeRejeu"),
            string.Join(", ", ids.Select(id => _localize(TweakDisplayKeys.TitleKey(id)))));
        MessageDemarrage = string.IsNullOrEmpty(MessageDemarrage) ? texte : MessageDemarrage + Environment.NewLine + texte;
        ChargerLeviersPoses();
    }

    /// <summary>Le rapport du rejeu du journal d'écriture anticipée ; null dans les tests qui ne le portent pas.</summary>
    internal DrDxt.App.Platform.RejeuDuJournalLu? RejeuLu { get; set; }

    [RelayCommand]
    private Task RendreEssaiInterrompuAsync()
        => _essaiARendre is { } e ? RendreEssaiAsync(e.Journal, e.Catalogue) : Task.CompletedTask;

    private async Task RendreEssaiAsync(DrDxt.Application.Autopilot.IRevertEnAttente journal, ITweakCatalog catalogue)
    {
        string? phrase;
        var remiseUsine = false;
        // Hors du fil d'interface : le revert passe par le canal privilégié. Un refus de rendre la fréquence de la carte
        // AMD se dit ; la remise à l'usine attend le clic du joueur.
        try { phrase = await Task.Run(() => DrDxt.Application.Autopilot.RevertAuDemarrage.Executer(journal, catalogue, out remiseUsine,
            id => _localize(TweakDisplayKeys.TitleKey(id)), r => RaisonLisible.Dire(r, _localize), _registreGains, VerifierLaPose)).ConfigureAwait(true); }
        catch (Exception ex) { phrase = Docteur.T("App.VerificationSessionEchec", RaisonLisible.Dire(ex, _localize)); }
        if (remiseUsine) ProposerRemiseUsine(journal);
        // Pas rendu : l'essai reste au journal (le lancement suivant le retente), et le bouton le rend sur un clic.
        _essaiARendre = !remiseUsine && journal.Lire() is { IsOk: true, Value: not null } ? (journal, catalogue) : null;
        OnPropertyChanged(nameof(RenduEssaiPropose));
        if (phrase is not null) MessageDemarrage = phrase;
    }

    internal void ProposerRemiseUsine(DrDxt.Application.Autopilot.IRevertEnAttente journal)
    {
        _essaiARemettreALUsine = journal;
        OnPropertyChanged(nameof(RemiseUsineProposee));
    }

    [RelayCommand]
    private async Task RemettreLaCarteALUsineAsync()
    {
        if (_essaiARemettreALUsine is not { } journal) return;
        _essaiARemettreALUsine = null;
        OnPropertyChanged(nameof(RemiseUsineProposee));
        // Relu au clic, et repassé au même contrôle que le démarrage : ce fichier vit sous %LOCALAPPDATA%, écrivable par
        // tout programme du compte, et le helper rejoue son inverse.
        if (journal.Lire() is not { IsOk: true, Value: { Snapshot: { } snapshot } entree }
            || !DrDxt.Application.Autopilot.RevertAuDemarrage.InverseRejouable(entree.TweakId, snapshot.OriginalValueJson)
            || LevierParId?.Invoke(entree.TweakId) is not { } levier)
        {
            MessageDemarrage = _localize("Demarrage.RemiseUsineIntrouvable");
            return;
        }
        var titre = _localize(TweakDisplayKeys.TitleKey(entree.TweakId));
        var issue = await DrDxt.Application.Autopilot.AnnulationParClic.AnnulerAsync(levier, snapshot, _helper);
        if (!issue.Resultat.IsOk)
        {
            MessageDemarrage = RaisonLisible.OuIncertain(issue.Resultat,
                string.Format(CultureInfo.CurrentCulture, _localize("Leviers.PasAnnule"), titre, RaisonLisible.Dire(issue.Resultat, _localize)), _localize, titre);
            _essaiARemettreALUsine = journal;
            OnPropertyChanged(nameof(RemiseUsineProposee));
            return;
        }
        // Rendu sans remise à l'usine : « annulé » seulement relu à l'origine ; encore posé, le bouton reste.
        var relu = issue.Message is null ? Relu(snapshot) : DrDxt.Application.Tweaks.EtatApplique.NotApplied;
        if (relu == DrDxt.Application.Tweaks.EtatApplique.Applied)
        {
            MessageDemarrage = string.Format(CultureInfo.CurrentCulture, _localize("Leviers.PasAnnule"), titre, _localize("Leviers.EncorePose"));
            _essaiARemettreALUsine = journal;
            OnPropertyChanged(nameof(RemiseUsineProposee));
            return;
        }
        // Rendue, l'entrée quitte le journal : le démarrage suivant ne la retente pas.
        journal.Lever();
        MessageDemarrage = issue.Message ?? string.Format(CultureInfo.CurrentCulture,
            _localize(relu == DrDxt.Application.Tweaks.EtatApplique.NotApplied ? "Leviers.Annule" : "Leviers.AnnuleNonRelu"), titre);
    }
}
