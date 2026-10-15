namespace DrDxt.Core;

// --- Profils ---

public record HardwareMatch(string? CpuPattern, string? GpuPattern);

public record Profile(
    string Id,
    HardwareMatch Match,
    IReadOnlyList<string> RecommendedTweakIds,
    bool Tested);

public interface IProfileMatcher
{
    Result<Profile> Match(HardwareInventory hw);
}
