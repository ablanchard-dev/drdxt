using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;
using DrDxt.Profiles;

namespace DrDxt.App.Profiles;

// -----------------------------------------------------------------------
// ScanProfileConsumer : d'une recommandation de profil par puce (DrDxt.Profiles), ce sur quoi le flux scan→application
// peut agir. Depuis le HardwareInventory et le SettingsState :
//   1. les profils de cette puce (IChipProfileMatcher, prudent : puce inconnue → base générique seule) ;
//   2. le TweakPlan : union des choix, erreurs de réglage d'abord, sans doublon, restreint à AppliesTo. Le réglage GPU
//      a son lot à part : le lot du moteur est tout ou rien et le SDK GPU échoue par défaut, il annulerait les
//      corrections sûres ;
//   3. les cibles GPU en cours (IGpuTuningTargetSource), chaque paramètre du profil borné à GpuSafeBounds dès l'entrée,
//      avec une note quand une valeur a été ramenée ;
//   4. la GainEvidence et le drapeau Tested de chaque choix, « non vérifié » à l'écran tant qu'il n'est pas validé.
// Pure orchestration, sans E/S ni privilège ; le gain se prouve en aval par la remesure A/B, aucun chiffre que le
// profil ne cite pas.
// -----------------------------------------------------------------------

/// <summary>Une ligne de recommandation de profil pour l'écran du scan, avec sa provenance. <see cref="IsVerified"/>
/// reste faux tant que le profil n'est pas validé sur du vrai matériel (« non vérifié » à l'écran).</summary>
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
    // Le levier s'applique-t-il a cette machine (le catalogue) ? L'ecran ne recommande que ce qui s'applique.
    bool Applicable = true)
{
    /// <summary>Un profil pas encore validé sur du vrai matériel est « non vérifié ».</summary>
    public bool IsVerified => Tested;
}

/// <summary>La recommandation pour une machine scannée : le plan sûr hors GPU, le plan GPU à part, les cibles GPU
/// bornées, les lignes affichées.</summary>
public sealed record ScanProfileConsumption(
    TweakPlan Plan,
    GpuTuningTarget GpuTuning,
    GpuPowerLimitTarget GpuPowerLimit,
    IReadOnlyList<ProfileRecommendationItem> Items)
{
    /// <summary>Les choix de réglage GPU applicables, exécutés par l'étape GPU dédiée, à part du <see cref="Plan"/> sûr :
    /// un échec GPU n'annule jamais le lot sûr. Vide sans réglage GPU recommandé ou sur un GPU non pris en charge
    /// (inconnu, iGPU, portable, console portable).</summary>
    public TweakPlan GpuPlan { get; init; } = new(Array.Empty<string>());

    /// <summary>Vrai quand il y a au moins un levier applicable à exécuter dans ce lot.</summary>
    public bool HasPlan => Plan.TweakIds.Count > 0;

    /// <summary>Le seul cas où l'action GPU sur demande s'exécute : une étape GPU applicable recommandée.</summary>
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

    // La langue du moteur (les tests la lisent) ; la vue les traduit (ScanViewModel.TraduireNote) d'après ces constantes.
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
        // « GPU AMD présent », fait matériel de l'inventaire (indépendant d'ADLX) : un levier GPU dit « détecté, réglage à
        // venir » plutôt qu'un saut silencieux. Posé même sur le refus ci-dessous.
        _gpuTargets.SetAmdGpuPresenceFromHardware(hw);
        var tuning = _gpuTargets.Tuning;
        var powerLimit = _gpuTargets.PowerLimit;

        var rec = _matcher.Recommend(hw);
        if (!rec.IsOk || rec.Value is null)
        {
            // Inventaire inutilisable : plan vide, cibles par défaut, rien d'affirmé.
            return new ScanProfileConsumption(
                new TweakPlan(Array.Empty<string>()), tuning, powerLimit,
                Array.Empty<ProfileRecommendationItem>());
        }

        // Union des choix, erreurs de réglage d'abord, sans doublon (la première occurrence gagne).
        var ordered = rec.Value.Profiles
            .SelectMany(p => p.Selections.Select(s => (p.Tested, Selection: s)))
            .OrderBy(x => (int)x.Selection.Intent)
            .ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<ProfileRecommendationItem>();
        var planIds = new List<string>();
        // Les choix GPU applicables : leur propre lot, jamais mêlés au plan sûr.
        var gpuPlanIds = new List<string>();

        foreach (var (tested, sel) in ordered)
        {
            if (!seen.Add(sel.TweakId))
                continue;

            var got = _catalog.Get(sel.TweakId);
            if (got is null || !got.IsOk || got.Value is null)
            {
                // Le test de provenance garantit des ids du catalogue ; refusé quand même s'il dérive.
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
            // Le lot GPU à part ; un GPU non pris en charge (AppliesTo=false) n'en reçoit jamais.
            else if (isGpu && applicable)
                gpuPlanIds.Add(sel.TweakId);

            string? note = clampNote;
            // « pas lu » et « rien a faire » ont chacun leur note : un etat non lu ne passe pas pour un bilan de sante.
            if (!applicable && tweak.EtatNonLu(state))
                note = Join(note, NoteNonLu);
            else if (!applicable)
                note = Join(note, NoteNonApplicable);
            // La carte est ouverte machine par machine ; le veto des arrets se dit a l'ecran.
            else if (isGpu)
                note = Join(note, NoteReglageCarte);

            items.Add(Item(sel, tested, inPlan, note, applicable));
        }

        // Les cibles bornées, que les leviers GPU liront à leur exécution.
        _gpuTargets.Set(tuning, powerLimit);

        return new ScanProfileConsumption(new TweakPlan(planIds), tuning, powerLimit, items)
        {
            GpuPlan = new TweakPlan(gpuPlanIds),
        };
    }

    // Borne chaque paramètre GPU à GpuSafeBounds ; une note quand une valeur a été ramenée.
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
                // Clé de paramètre inconnue : ignorée, jamais écrite.
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
