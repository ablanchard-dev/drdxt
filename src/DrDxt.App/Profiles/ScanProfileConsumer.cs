using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using DrDxt.Profiles;

namespace DrDxt.App.Profiles;

// -----------------------------------------------------------------------
// ScanProfileConsumer : transforme une recommandation de profil par puce (DrDxt.Profiles)
// en quelque chose sur quoi le flux scan→application peut agir, honnêtement.
//
// À partir du HardwareInventory et du SettingsState scannés, il :
//   1. demande à l'IChipProfileMatcher quels profils vont à cette puce (prudent, refuse
//      au moindre doute : puce inconnue → base générique seule, jamais une supposition) ;
//   2. compose le TweakPlan exécuté à partir de l'union des choix des profils recommandés,
//      erreurs de réglage d'abord, sans doublon, restreint aux leviers qui s'appliquent
//      vraiment à cette machine (AppliesTo). Les choix de réglage GPU sont recommandés et
//      leurs cibles bornées et remplacées, mais ils ne vont pas dans ce lot : le lot du
//      moteur est tout ou rien et le SDK GPU échoue proprement par défaut, donc y mêler le
//      réglage GPU risquerait d'annuler les corrections sûres (le réglage GPU tourne dans son
//      étape à lui, comme le chemin d'application le documente déjà) ;
//   3. remplace les GpuTuningTarget / GpuPowerLimitTarget en cours (IGpuTuningTargetSource)
//      à partir des paramètres du profil, chaque paramètre borné à GpuSafeBounds dès l'entrée
//      (on ne fait jamais confiance aveuglément à la valeur du profil), avec une note lisible
//      quand une valeur a dû être ramenée dans la plage ;
//   4. affiche la GainEvidence et le drapeau Tested de chaque choix, pour que l'UI montre le
//      « pourquoi » honnête et marque « non vérifié » un profil pas encore validé.
//
// Pure orchestration au-dessus du matcher, du catalogue et de la source de cibles : pas
// d'E/S, pas de privilège. Le gain réel est prouvé en aval par la remesure A/B du moteur ;
// ce code n'affirme jamais un chiffre que le profil ne cite pas.
// -----------------------------------------------------------------------

/// <summary>Une ligne de recommandation de profil pour l'UI du scan, avec la provenance
/// honnête du choix. <see cref="IsVerified"/> reste faux tant que le profil n'est pas validé
/// sur du vrai matériel par l'équipe ; l'UI marque ces lignes « non vérifié ».</summary>
public sealed record ProfileRecommendationItem(
    string TweakId,
    ProfileIntent Intent,
    string Claim,
    string Source,
    GainConfidence Confidence,
    SafetyLevel Safety,
    bool Tested,
    bool InExecutedPlan,
    string? AdjustmentNote,
    // Le catalogue dit si le levier s'applique a cette machine. L'ecran s'en sert pour
    // ne pas recommander ce qui ne s'applique pas.
    bool Applicable = true)
{
    /// <summary>Un profil pas encore validé sur du vrai matériel est « non vérifié ».</summary>
    public bool IsVerified => Tested;
}

/// <summary>Le résultat d'une recommandation de profil par puce pour une machine scannée :
/// le plan exécuté (sûr, hors GPU), le plan de réglage GPU à part, les cibles GPU remplacées
/// et bornées, et les lignes de recommandation affichées.</summary>
public sealed record ScanProfileConsumption(
    TweakPlan Plan,
    GpuTuningTarget GpuTuning,
    GpuPowerLimitTarget GpuPowerLimit,
    IReadOnlyList<ProfileRecommendationItem> Items)
{
    /// <summary>Les choix de réglage GPU applicables du profil, en plan à part exécuté par l'étape
    /// GPU dédiée, séparé du <see cref="Plan"/> sûr hors GPU : un échec GPU n'annule jamais le
    /// lot sûr. Vide si aucun réglage GPU n'est recommandé ou si le GPU n'est pas pris en charge
    /// (inconnu, iGPU, portable, console portable).</summary>
    public TweakPlan GpuPlan { get; init; } = new(Array.Empty<string>());

    /// <summary>Vrai quand il y a au moins un levier applicable à exécuter dans ce lot.</summary>
    public bool HasPlan => Plan.TweakIds.Count > 0;

    /// <summary>Vrai quand le profil recommande une étape de réglage GPU applicable à cette
    /// machine : le seul cas où l'action GPU sur demande s'exécute (sinon elle ne fait rien).</summary>
    public bool HasGpuPlan => GpuPlan.TweakIds.Count > 0;
}

public interface IScanProfileConsumer
{
    ScanProfileConsumption Consume(HardwareInventory hw, SettingsState state);
}

public sealed class ScanProfileConsumer : IScanProfileConsumer
{
    // Clés de paramètre des profils (calquées sur DrDxt.Profiles.ChipProfileCatalog).
    private const string KeyCoreOffsetMhz = "CoreOffsetMhz";
    private const string KeyMemoryOffsetMhz = "MemoryOffsetMhz";
    private const string KeyPowerLimitPct = "PowerLimitPct";

    // Ces notes s'affichaient telles quelles, en anglais, dans l'App en français. Elles restent la
    // langue du moteur (les tests la lisent) ; la vue les traduit (ScanViewModel.TraduireNote) d'après ces constantes.
    public const string NoteInconnu = "Unknown catalog tweak: skipped.";
    public const string NoteNonLu = "State could not be read on this machine: skipped: "
                                    + "this is not a clean bill of health, nothing was verified here.";
    public const string NoteNonApplicable = "Not applicable to this machine: skipped.";
    public const string NoteReglageCarte = "GPU tuning: tried in game by Optimize, kept only with a measured gain, "
                                           + "otherwise undone; recent crashes that involve the card close it.";

    private readonly IChipProfileMatcher _matcher;
    private readonly ITweakCatalog _catalog;
    private readonly IGpuTuningTargetSource _gpuTargets;

    public ScanProfileConsumer(
        IChipProfileMatcher matcher, ITweakCatalog catalog, IGpuTuningTargetSource gpuTargets)
    {
        _matcher = matcher;
        _catalog = catalog;
        _gpuTargets = gpuTargets;
    }

    public ScanProfileConsumption Consume(HardwareInventory hw, SettingsState state)
    {
        // Partir des valeurs prudentes par défaut ; ne jamais hériter du remplacement d'un scan précédent.
        _gpuTargets.Reset();
        // Note le fait matériel « GPU AMD présent » d'après l'inventaire de ce scan (indépendant du
        // SDK ADLX) : un levier GPU peut ainsi afficher l'état honnête « GPU AMD détecté, réglage à
        // venir » au lieu d'un saut silencieux quand ADLX manque. Posé même sur le chemin de refus
        // ci-dessous : la présence est un fait du matériel, pas du profil trouvé.
        _gpuTargets.SetAmdGpuPresenceFromHardware(hw);
        var tuning = _gpuTargets.Tuning;
        var powerLimit = _gpuTargets.PowerLimit;

        var rec = _matcher.Recommend(hw);
        if (!rec.IsOk || rec.Value is null)
        {
            // Refus au moindre doute : un inventaire inutilisable donne un résultat sûr qui ne fait rien
            // (plan vide, cibles par défaut, rien d'affirmé). On n'invente jamais un profil.
            return new ScanProfileConsumption(
                new TweakPlan(Array.Empty<string>()), tuning, powerLimit,
                Array.Empty<ProfileRecommendationItem>());
        }

        // Union des choix de chaque profil recommandé, erreurs de réglage d'abord, sans doublon par
        // id de levier (la première occurrence, la plus prioritaire, gagne).
        var ordered = rec.Value.Profiles
            .SelectMany(p => p.Selections.Select(s => (p.Tested, Selection: s)))
            .OrderBy(x => (int)x.Selection.Intent)
            .ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<ProfileRecommendationItem>();
        var planIds = new List<string>();
        // Les choix de réglage GPU qui s'appliquent ici : exécutés dans leur propre lot (l'étape GPU
        // dédiée), jamais mêlés au plan sûr tout ou rien ci-dessus.
        var gpuPlanIds = new List<string>();

        foreach (var (tested, sel) in ordered)
        {
            if (!seen.Add(sel.TweakId))
                continue;

            var got = _catalog.Get(sel.TweakId);
            if (got is null || !got.IsOk || got.Value is null)
            {
                // Le test de provenance de base garantit que chaque id choisi est un vrai levier du
                // catalogue ; on refuse quand même si ça dérive un jour.
                items.Add(Item(sel, tested, inPlan: false, NoteInconnu));
                continue;
            }

            var tweak = got.Value;
            var applicable = tweak.AppliesTo(hw, state);
            var isGpu = tweak.Category == TweakCategory.GpuTuning;

            // Paramètres GPU → bornés et posés sur les cibles en cours dès l'entrée.
            var clampNote = isGpu ? ApplyGpuOverride(sel, ref tuning, ref powerLimit) : null;

            // Lot exécuté ici = les choix applicables hors GPU seulement.
            var inPlan = applicable && !isGpu;
            if (inPlan)
                planIds.Add(sel.TweakId);
            // Le lot GPU à part = les choix de réglage GPU applicables. Un GPU non pris en charge
            // (AppliesTo=false pour inconnu, iGPU, portable, console portable) sort ici → aucun lot GPU
            // ne lui est jamais proposé.
            else if (isGpu && applicable)
                gpuPlanIds.Add(sel.TweakId);

            string? note = clampNote;
            // « pas lu » et « rien a faire » ont chacun leur note : sous la meme phrase, un etat
            // qui n'a pas pu etre lu passerait pour un bilan de sante. On n'ecrit rien sur ce qu'on
            // n'a pas lu, mais on ne le fait pas passer pour une bonne nouvelle.
            if (!applicable && tweak.EtatNonLu(state))
                note = Join(note, NoteNonLu);
            else if (!applicable)
                note = Join(note, NoteNonApplicable);
            // La carte est ouverte machine par machine ; le veto des arrets se dit a l'ecran.
            else if (isGpu)
                note = Join(note, NoteReglageCarte);

            items.Add(Item(sel, tested, inPlan, note, applicable));
        }

        // Pose les cibles bornées issues du profil (le remplacement) pour que les leviers GPU les
        // lisent à leur exécution. Chaque valeur ci-dessus est déjà bornée à GpuSafeBounds.
        _gpuTargets.Set(tuning, powerLimit);

        return new ScanProfileConsumption(new TweakPlan(planIds), tuning, powerLimit, items)
        {
            GpuPlan = new TweakPlan(gpuPlanIds),
        };
    }

    // Borne chaque paramètre GPU à GpuSafeBounds avant qu'il n'atteigne une cible, et rend une
    // note lisible quand une valeur a dû être ramenée dans la plage (affichage honnête).
    private static string? ApplyGpuOverride(
        ProfileTweakSelection sel, ref GpuTuningTarget tuning, ref GpuPowerLimitTarget powerLimit)
    {
        var notes = new List<string>();
        foreach (var p in sel.Parameters)
        {
            switch (p.Key)
            {
                case KeyCoreOffsetMhz:
                {
                    var safe = GpuSafeBounds.ClampCoreOffsetMhz(p.Value);
                    if (safe != p.Value)
                        notes.Add($"core offset clamped {p.Value}→{safe} MHz (GpuSafeBounds)");
                    tuning = tuning with { CoreOffsetMhz = safe };
                    break;
                }
                case KeyMemoryOffsetMhz:
                {
                    var safe = GpuSafeBounds.ClampMemoryOffsetMhz(p.Value);
                    if (safe != p.Value)
                        notes.Add($"memory offset clamped {p.Value}→{safe} MHz (GpuSafeBounds)");
                    tuning = tuning with { MemoryOffsetMhz = safe };
                    break;
                }
                case KeyPowerLimitPct:
                {
                    var safe = GpuSafeBounds.ClampPowerLimitPct(p.Value);
                    if (safe != p.Value)
                        notes.Add($"power limit clamped {p.Value}→{safe}% (GpuSafeBounds)");
                    powerLimit = powerLimit with { PowerLimitPct = safe };
                    break;
                }
                // Les clés de paramètre inconnues sont ignorées, par prudence : on n'écrit jamais une valeur
                // qu'on ne comprend pas.
            }
        }
        return notes.Count == 0 ? null : string.Join("; ", notes);
    }

    private static ProfileRecommendationItem Item(
        ProfileTweakSelection sel, bool tested, bool inPlan, string? note, bool applicable = true) =>
        new(sel.TweakId, sel.Intent, sel.Evidence.Claim, sel.Evidence.Source,
            sel.Evidence.Confidence, sel.Evidence.Safety, tested, inPlan, note, applicable);

    private static string Join(string? a, string b) =>
        string.IsNullOrEmpty(a) ? b : $"{a}; {b}";
}
