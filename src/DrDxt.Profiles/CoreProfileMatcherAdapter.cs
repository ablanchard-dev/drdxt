using DrDxt.Core;

namespace DrDxt.Profiles;

// -----------------------------------------------------------------------
// CoreProfileMatcherAdapter : l'IProfileMatcher de Core, la recommandation riche projetée sur un Profile composite (le
// contrat de Core ne bouge pas, comme EnginePlan et ITweakEngine).
//   * Profile.Id           = composition des identifiants de familles appariées.
//   * Profile.Match        = descripteurs informatifs du processeur et de la carte appariés.
//   * RecommendedTweakIds  = union de toutes les sélections, ordre FixMisconfig → Latency →
//                            GpuTune, première occurrence gardée (pas de doublon).
//   * Profile.Tested       = « et » logique sur les profils appariés (false sauf si tous sont
//                            validés par vague) : un composite n'est « testé » que si chaque
//                            partie l'est.
//
// Un Recommend en échec (inventaire inutilisable) remonte tel quel : jamais de profil fabriqué.
// -----------------------------------------------------------------------
public sealed class CoreProfileMatcherAdapter : IProfileMatcher
{
    private readonly IChipProfileMatcher _matcher;

    public CoreProfileMatcherAdapter(IChipProfileMatcher matcher) => _matcher = matcher;

    public Result<Profile> Match(HardwareInventory hw)
    {
        var rec = _matcher.Recommend(hw);
        if (!rec.IsOk || rec.Value is null)
            return Result<Profile>.Fail(rec.Error!.Value.Code, rec.Error!.Value.Message);

        var profiles = rec.Value.Profiles;

        var id = "drdxt." + string.Join("+", profiles.Select(p => p.Family.ToString().ToLowerInvariant()));

        // Union des sélections, réparations d'abord, sans doublon (la première gagne).
        var tweakIds = profiles
            .SelectMany(p => p.Selections)
            .OrderBy(s => (int)s.Intent)
            .Select(s => s.TweakId)
            .Distinct()
            .ToList();

        var cpuPattern = rec.Value.Classification.IsAmdX3dDualCcd ? "AMD * X3D (dual-CCD)" : null;
        var gpuPattern = rec.Value.Classification.HasTunableNvidiaGpu ? "NVIDIA GeForce RTX/GTX (desktop)" : null;

        // Le composite n'est « testé » que si chaque profil apparié est validé par vague.
        var tested = profiles.All(p => p.Tested);

        return Result<Profile>.Ok(new Profile(id, new HardwareMatch(cpuPattern, gpuPattern), tweakIds, tested));
    }
}
