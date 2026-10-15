using System.Globalization;
using System.Resources;
using DrDxt.Core;

namespace DrDxt.Infrastructure.Localization;

/// <summary>
/// <see cref="ILocalizer"/> adossé aux fichiers de ressources embarqués <c>Strings.resx</c> /
/// <c>Strings.fr.resx</c> de cet assemblage.
/// </summary>
/// <remarks>
/// Thread-safe : <see cref="ResourceManager"/> est thread-safe par conception.
/// Une clé manquante rend la clé elle-même : jamais null, jamais d'exception.
/// </remarks>
public sealed class ResxLocalizer : ILocalizer
{
    private static readonly ResourceManager s_manager =
        new ResourceManager(
            "DrDxt.Infrastructure.Resources.Strings",
            typeof(ResxLocalizer).Assembly);

    /// <inheritdoc/>
    public string Get(string key, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        return s_manager.GetString(key, culture) ?? key;
    }
}
