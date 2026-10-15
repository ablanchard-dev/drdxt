using System.Globalization;

namespace DrDxt.Core;

// --- Traduction ---

/// <summary>
/// Traduit une clé de ressource en chaîne pour une culture donnée.
/// </summary>
/// <remarks>
/// Les implémentations doivent être thread-safe (durée de vie singleton dans la DI).
/// Sur une clé manquante, la clé elle-même est rendue : jamais null, jamais d'exception.
/// Ce contrat vit dans Core pour que les types du domaine citent des clés sans
/// dépendre de l'infrastructure.
/// </remarks>
public interface ILocalizer
{
    /// <summary>
    /// Rend la chaîne traduite de <paramref name="key"/> dans
    /// <paramref name="culture"/> (null = <see cref="CultureInfo.CurrentUICulture"/>).
    /// Sur une clé manquante, rend la clé elle-même.
    /// </summary>
    string Get(string key, CultureInfo? culture = null);
}
