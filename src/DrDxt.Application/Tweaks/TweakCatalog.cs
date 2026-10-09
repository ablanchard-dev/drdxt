using DrDxt.Core;

namespace DrDxt.Application.Tweaks;

// -----------------------------------------------------------------------
// F4 — TweakCatalog.
//
// Aggregates every registered ITweak (injected as IEnumerable<ITweak> by DI) and
// implements the reconciled ITweakCatalog (master §10): Get(id) / All(). It is the
// single place the rest of the app discovers tweaks — and the place the §6
// anti-blacklist barrier is enforced by tests.
//
// The HARD anti-blacklist gate (TweakCatalogTests) asserts that AllTweakIds
// contains none of the banned placebo / harmful ids — a permanent barrier: a
// placebo tweak cannot be introduced without failing the build's tests.
//
// Resolvable via DI: register each ITweak + this catalog, then
// IEnumerable<ITweak> flows into the constructor automatically.
// -----------------------------------------------------------------------
public sealed class TweakCatalog : ITweakCatalog
{
    private readonly IReadOnlyList<ITweak> _tweaks;

    public TweakCatalog(IEnumerable<ITweak> tweaks) =>
        _tweaks = tweaks.ToList();

    /// <summary>The ids of every registered tweak. The anti-blacklist gate checks
    /// this against the §6 banned list.</summary>
    public IReadOnlyList<string> AllTweakIds => _tweaks.Select(t => t.Id).ToList();

    /// <summary>Resolves a tweak by id. Unknown id → Fail(TWEAK.NOT_APPLICABLE).</summary>
    public Result<ITweak> Get(string tweakId)
    {
        foreach (var tweak in _tweaks)
        {
            if (string.Equals(tweak.Id, tweakId, StringComparison.Ordinal))
                return Result<ITweak>.Ok(tweak);
        }
        return Result<ITweak>.Fail(ErrorCodes.TweakNotApplicable, $"No tweak with id '{tweakId}'.");
    }

    public IReadOnlyList<ITweak> All() => _tweaks;
}
