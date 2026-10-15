using DrDxt.Core;

namespace DrDxt.Application.Tweaks;

// -----------------------------------------------------------------------
// TweakCatalog.
//
// Rassemble chaque ITweak enregistré (injecté en IEnumerable<ITweak> par la DI) et
// implémente ITweakCatalog : Get(id) / All(). C'est le seul endroit où le reste de l'app
// découvre les leviers, et celui où les tests imposent la barrière anti-liste noire.
//
// La garde stricte anti-liste noire (TweakCatalogTests) vérifie qu'AllTweakIds ne contient
// aucun des ids placebo ou nuisibles interdits : une barrière permanente, un levier placebo
// ne peut pas entrer sans faire échouer les tests.
//
// Résoluble par la DI : enregistrer chaque ITweak + ce catalogue, et IEnumerable<ITweak>
// arrive tout seul dans le constructeur.
// -----------------------------------------------------------------------
public sealed class TweakCatalog : ITweakCatalog
{
    private readonly IReadOnlyList<ITweak> _tweaks;

    public TweakCatalog(IEnumerable<ITweak> tweaks) =>
        _tweaks = tweaks.ToList();

    /// <summary>Les ids de chaque levier enregistré. La garde anti-liste noire les compare à
    /// la liste des interdits.</summary>
    public IReadOnlyList<string> AllTweakIds => _tweaks.Select(t => t.Id).ToList();

    /// <summary>Trouve un levier par son id. Id inconnu → Fail(TWEAK.NOT_APPLICABLE).</summary>
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
