using DrDxt.Core;

namespace DrDxt.Profiles;

/// <summary>Le pourquoi d'une recommandation : les familles détectées et une raison lisible
/// pour chaque décision (pour l'interface et le rapport, jamais une supposition).</summary>
public sealed record ChipClassification(
    bool IsAmdX3dDualCcd,
    bool HasTunableNvidiaGpu,
    IReadOnlyList<string> Reasons);

/// <summary>Ce que le matcher recommande pour une machine : la base générique sûre, les
/// profils propres à la puce, et le classement qui les a produits.</summary>
public sealed record ProfileRecommendation(
    IReadOnlyList<ChipProfile> Profiles,
    ChipClassification Classification);

/// <summary>Service d'appariement : puce détectée (inventaire du scan) → profils recommandés.
/// Prudent et fermé par défaut : ne propose que ce qui est sûr et sourcé pour la famille
/// détectée ; une puce inconnue reçoit la base générique, jamais une supposition.</summary>
public interface IChipProfileMatcher
{
    Result<ProfileRecommendation> Recommend(HardwareInventory hw);
}

// -----------------------------------------------------------------------
// ChipProfileMatcher.
//
// Composition prudente (« jamais inventer ») :
//   * la base générique sûre est toujours recommandée (le plancher) ;
//   * le profil AMD X3D à deux CCD s'ajoute si, et seulement si, le processeur est de cette
//     famille avec certitude ;
//   * le profil NVIDIA option A s'ajoute si, et seulement si, une GeForce de bureau réglable
//     est présente.
// Une puce inconnue n'ajoute rien de spécifique : générique seulement. Un inventaire absent
// est refusé (PROFILE.INPUT_INVALID) : jamais de profil fabriqué.
// L'AppliesTo de chaque levier (état courant des réglages) reste la dernière garde au moment
// de poser ; le matcher ne juge que l'éligibilité au niveau de la puce, rien de plus.
// Pur sur le catalogue ; ne lève jamais.
// -----------------------------------------------------------------------
public sealed class ChipProfileMatcher : IChipProfileMatcher
{
    private readonly IChipProfileCatalog _catalog;

    public ChipProfileMatcher(IChipProfileCatalog catalog) => _catalog = catalog;

    public Result<ProfileRecommendation> Recommend(HardwareInventory hw)
    {
        // Fermé par défaut : un inventaire inutilisable ne donne jamais un profil deviné.
        if (hw is null || hw.Cpu is null)
            return Result<ProfileRecommendation>.Fail(ErrorCodes.ProfileInputInvalid,
                "HardwareInventory (and its Cpu) is required to recommend a profile.");

        var profiles = new List<ChipProfile> { _catalog.Generic };
        var reasons = new List<string>();

        var isX3d = ChipClassifier.IsAmdX3dDualCcd(hw.Cpu);
        if (isX3d)
        {
            var p = _catalog.ByFamily(ChipFamily.AmdX3dDualCcd);
            if (p.IsOk && p.Value is not null)
            {
                profiles.Add(p.Value);
                reasons.Add($"AMD dual-CCD X3D detected ('{hw.Cpu.Model}', {hw.Cpu.CcdCount} CCD) → CCD-routing repair profile.");
            }
            else
            {
                // Famille détectée mais aucun profil établi : rester générique et le dire.
                reasons.Add("AMD dual-CCD X3D detected but no curated profile available → generic only.");
            }
        }
        else
        {
            reasons.Add("No dual-CCD AMD X3D CPU → CCD-routing profile not applicable.");
        }

        var hasNvidia = ChipClassifier.HasTunableNvidiaDesktopGpu(hw);
        if (hasNvidia)
        {
            var p = _catalog.ByFamily(ChipFamily.NvidiaOptionA);
            if (p.IsOk && p.Value is not null)
            {
                profiles.Add(p.Value);
                reasons.Add("Tunable discrete desktop GeForce GPU detected → NVIDIA Option-A profile.");
            }
            else
            {
                reasons.Add("NVIDIA GPU detected but no curated profile available → generic only.");
            }
        }
        else
        {
            reasons.Add("No tunable discrete desktop GeForce GPU → NVIDIA Option-A profile not applicable.");
        }

        var classification = new ChipClassification(isX3d, hasNvidia, reasons);
        return Result<ProfileRecommendation>.Ok(new ProfileRecommendation(profiles, classification));
    }
}
