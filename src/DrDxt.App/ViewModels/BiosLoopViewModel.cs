using System;
using System.Collections.Generic;
using System.Linq;
using DrDxt.Bios;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>
/// Le verdict du retour de BIOS, rendu lisible : « ton changement a-t-il pris ? », reglage par
/// reglage, plus la carte de recuperation quand quelque chose n'a pas pris.
///
/// C'est la moitie visible du differenciateur « on te dit quoi changer, et on prouve que ca a
/// pris ». Le verifieur existait et n'etait resolu par personne : le produit ne
/// prononcait donc ce verdict nulle part.
///
/// Ce qu'il ne fait jamais :
///   • annoncer un succes sur un reglage illisible : <c>Unreadable</c> reste « je n'ai pas pu
///     verifier », qui n'est pas « ca n'a pas marche » et encore moins « c'est bon » ;
///   • cacher la carte de recuperation quand le verifieur la demande. Les cles viennent de
///     <see cref="BiosLoopResult.RecoveryCardKeys"/>, dans l'ordre qu'il a fixe.
/// </summary>
public sealed class BiosLoopViewModel
{
    private BiosLoopViewModel(
        bool allTook, string headline, IReadOnlyList<BiosLoopOutcomeViewModel> outcomes,
        IReadOnlyList<string> recoveryCard)
    {
        AllTook = allTook;
        Headline = headline;
        Outcomes = outcomes;
        RecoveryCard = recoveryCard;
    }

    /// <summary>
    /// Rend le VM du resultat, ou <c>null</c> quand il n'y avait rien en attente, le cas
    /// courant. Une section vide ne doit pas s'afficher : la plupart des ouvertures de l'app
    /// ne suivent aucun voyage dans le BIOS.
    /// </summary>
    public static BiosLoopViewModel? From(BiosLoopResult result, Func<string, string> localize)
    {
        if (result is null || !result.HadPendingChange)
            return null;

        var outcomes = result.Outcomes
            .Select(o => new BiosLoopOutcomeViewModel(
                localize(BiosLoopStringKeys.SettingName(o.Setting)),
                localize(VerdictKey(o.Verdict)),
                o.Verdict))
            .ToList();

        // L'entete n'est affirmative que sur un succes complet. Sinon on ne titre rien : les
        // lignes par reglage disent chacune la verite, et un titre generique par-dessus
        // ferait passer un « je n'ai pas pu verifier » pour un echec, ou l'inverse.
        var headline = result.AllTook ? localize(BiosLoopStringKeys.AllTook) : string.Empty;

        var recovery = result.ShowRecoveryCard
            ? result.RecoveryCardKeys.Select(localize).ToList()
            : (IReadOnlyList<string>)Array.Empty<string>();

        return new BiosLoopViewModel(result.AllTook, headline, outcomes, recovery);
    }

    /// <summary>Vrai uniquement quand chaque reglage attendu a ete confirme.</summary>
    public bool AllTook { get; }

    /// <summary>Le titre de succes, vide tant que tout n'a pas ete confirme.</summary>
    public string Headline { get; }

    /// <summary>Un verdict par reglage attendu, dans l'ordre du marqueur.</summary>
    public IReadOnlyList<BiosLoopOutcomeViewModel> Outcomes { get; }

    /// <summary>La carte de recuperation, vide sur un succes propre.</summary>
    public IReadOnlyList<string> RecoveryCard { get; }

    /// <summary>Vrai quand la carte de recuperation doit etre montree.</summary>
    public bool ShowRecoveryCard => RecoveryCard.Count > 0;

    private static string VerdictKey(BiosSettingVerdict verdict) => verdict switch
    {
        BiosSettingVerdict.Took => BiosLoopStringKeys.VerdictTook,
        BiosSettingVerdict.DidNotTake => BiosLoopStringKeys.VerdictDidNotTake,
        BiosSettingVerdict.Unreadable => BiosLoopStringKeys.VerdictUnreadable,
        // Ensemble ferme : un verdict inconnu ne doit surtout pas retomber sur « Took ».
        _ => BiosLoopStringKeys.VerdictUnreadable,
    };
}

/// <summary>Une ligne du verdict : le nom du reglage, ce qu'on en dit, et le verdict brut.</summary>
public sealed class BiosLoopOutcomeViewModel
{
    public BiosLoopOutcomeViewModel(string settingName, string verdictLabel, BiosSettingVerdict verdict)
    {
        SettingName = settingName;
        VerdictLabel = verdictLabel;
        Verdict = verdict;
    }

    public string SettingName { get; }
    public string VerdictLabel { get; }

    /// <summary>Le verdict brut, pour que le rendu distingue confirme / pas pris / non verifiable
    /// sans relire le libelle traduit.</summary>
    public BiosSettingVerdict Verdict { get; }
}
