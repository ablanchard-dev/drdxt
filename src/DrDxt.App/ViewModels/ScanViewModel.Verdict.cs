using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using DrDxt.App.Profiles;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>Le verdict : santé, couverture, registre, et ce qu'Optimiser peut poser.</summary>
public sealed partial class ScanViewModel
{
    /// <summary>Optimiser est offert des qu'un scan a produit des resultats (Done / AlreadyOptimal /
    /// Degraded). Il juge d'abord ce qu'il y a a tenter, et le dit quand il n'y a rien.</summary>
    public bool CanApply => ScanPretAAgir && !OperationLongueEnCours;

    /// <summary>Ce que l'infobulle du bouton « Appliquer » dit, selon qu'il peut agir ou non : l'action principale,
    /// grisée sans raison dite, passerait pour cassée au lieu d'attendre un geste. Elle suit l'état à chaque
    /// changement, et dit aussi quand il n'y a rien à poser.</summary>
    public string ApplyHintKey => !ScanPretAAgir ? "scan.apply.needs_scan" : OperationLongueEnCours ? "UI.OperationEnCours"
        : RienAPoserDuTout ? "Apply.RienAPoserSeul" : "scan.apply.ready";

    /// <summary>Le texte deja traduit. `ScanView` ne declare pas le convertisseur `Loc`
    /// (il n'est declare que dans `MainWindow.xaml`) : lier une cle ici aurait leve au rendu, et
    /// un test qui se contente de relire le XAML ne l'aurait jamais vu. Cette vue localise
    /// par ses proprietes, comme `LabelOptimize` juste a cote.</summary>
    public string ApplyHint => _localize(ApplyHintKey);

    /// <summary>L'application du réglage GPU est explicite, sur demande : proposée seulement quand
    /// un scan a rendu des résultats et que le profil de la puce recommande une étape GPU applicable
    /// à cette machine. Un GPU non pris en charge (inconnu, iGPU, portable, console portable) → pas
    /// de plan GPU → l'action reste désactivée (jamais d'OC posé en silence au scan).</summary>
    public bool CanApplyGpuTuning => CanApply && _consumption is { HasGpuPlan: true };

    /// <summary>Vrai quand la machine scannée a une recommandation de réglage GPU applicable
    /// (pilote la visibilité de l'action GPU sur demande ; binding de l'UI à venir).</summary>
    public bool HasGpuTuningRecommendation => _consumption is { HasGpuPlan: true };

    /// <summary>Faux quand les lignes de réglage GPU recommandées ne sont pas encore validées
    /// sur du vrai matériel (Tested=false) : l'UI marque l'action GPU « non vérifié ».</summary>
    public bool GpuTuningVerified =>
        _consumption is { HasGpuPlan: true } c
        && c.Items.Where(i => c.GpuPlan.TweakIds.Contains(i.TweakId)).All(i => i.IsVerified);

    /// <summary>
    /// Le verdict : trois états, fonction de deux entiers, jamais d'une somme de pénalités (les doubler toutes ne change
    /// pas ce que le client lit, test d'invariance). Dans l'ordre de priorité :
    ///   K > 0            -> la lecture est incomplete. Aucun verdict de sante n'est permis :
    ///                       ce qu'on n'a pas lu peut etre exactement le probleme.
    ///   K = 0 et N = 0   -> rien a corriger dans ce qui a ete lu. La nuance est la promesse.
    ///   K = 0 et N > 0   -> N reglages a corriger. Aucun plancher : douze mauvais leviers
    ///                       affichent douze.
    /// </summary>
    public string VerdictText =>
        !IsCoverageComplete || _santeNonLue ? _localize("UI.Verdict.Partial")
        : ProvenProblemCount == 0 && !SanteACorriger ? _localize("UI.Verdict.Clean")
        // Vu sur la photo, « réglages à corriger » au-dessus de « 0 leviers mal réglés » : les
        // points venaient de la santé (mises à jour, incidents, failles), pas des réglages. Le titre le dit.
        : ProvenProblemCount == 0 ? _localize("UI.Verdict.SanteACorriger")
        : _localize("UI.Verdict.Problems");

    /// <summary>
    /// L'audit de santé a trouvé au moins un état suspect ou nuisible. Le titre et
    /// le résumé ne regardaient que le scanner : « rien à corriger » s'affichait au-dessus de
    /// « Windows et stabilité : à corriger ». Un état seulement à surveiller (information) ne compte pas.
    /// </summary>
    private bool SanteACorriger => RepairFindings.Any(r => r.Severity > Severity.Info);

    /// <summary>Vrai seulement si chaque levier applicable a été lu. Une couverture absente compte comme faux.</summary>
    public bool IsCoverageComplete => Coverage is { UnknownLevers: 0, ApplicableLevers: > 0 };

    /// <summary>« lus/applicables » : le compte de lecture derrière le score. Un tiret cadratin quand il manque.</summary>
    public string CoverageText => Coverage is { } c
        ? $"{c.KnownLevers}/{c.ApplicableLevers}"
        : "—";

    /// <summary>Vrai quand le scan a constate au moins un probleme.</summary>
    public bool HasProvenProblems => ProvenProblemCount > 0;

    /// <summary>
    /// Le registre, en une ligne : « 3 problemes prouves · 12 leviers lus sur 14 ». C'est tout ce
    /// que le produit affirme au premier coup d'oeil.
    ///
    /// Sans couverture on n'affiche que le compte de problemes : « 3 problemes prouves · 12
    /// leviers lus sur 0 » serait une ligne fausse, et un tiret la rendrait illisible.
    /// </summary>
    public string RegisterText
    {
        get
        {
            var problems = string.Format(
                CultureInfo.CurrentCulture,
                // « 0 à changer » sous « 13 points à corriger » passerait pour un
                // démenti ; à zéro, la phrase le dit en mots, et la ligne précise qu'elle parle de ce que DrDXT pose lui-même.
                _localize(ProvenProblemCount == 0 ? "UI.Register.None" : ProvenProblemCount == 1 ? "UI.Register.Problem" : "UI.Register.Problems"),
                ProvenProblemCount);

            // Sans couverture, on n'affiche pas le compte seul. Mesure du 05/09 : « 3 problemes
            // prouves » sans savoir combien de leviers ont ete lus laisse croire que la machine a
            // ete examinee. Un scan qui n'a pas pu mesurer sa propre couverture n'a rien examine
            // de facon affirmable, et il doit le dire.
            if (Coverage is not { } c)
                return _localize("UI.Register.NoCoverage");

            // Tout a été lu et rien du catalogue ne s'applique à cette machine. Ce n'est
            // pas « lecture impossible » : c'est une lecture complète au résultat vide.
            if (c.ApplicableLevers == 0)
                return _localize("UI.Register.NothingApplies");

            var read = string.Format(
                CultureInfo.CurrentCulture,
                _localize("UI.Register.LeversRead"), c.KnownLevers, c.ApplicableLevers);

            // D'abord de quoi on parle (les réglages que DrDXT sait poser), puis
            // combien sont à changer ; lu dans l'autre sens, il contredisait « 12 points à corriger » au-dessus.
            return $"{read}, {problems}";
        }
    }

    // Bandeau principal, côté résultats.
    public string LabelCoverageCaption => _localize("UI.Coverage.Caption");
    /// <summary>Sous-titre du registre : dit ce que les deux nombres sont, des lectures.</summary>
    public string LabelRegisterCaption => _localize("UI.Register.Caption");
    public string LabelOptimize => _localize("UI.Cta.Optimize");
    public string LabelRapportDescription => _localize("Rapport.Description");
    public string LabelEssaiExplication => _localize("Essai.Explication");
    public string LabelRescan => _localize("UI.Cta.Rescan");
}
