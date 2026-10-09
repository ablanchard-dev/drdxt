using System.Globalization;
using System.Resources;
using DrDxt.Core;

namespace DrDxt.Infrastructure.Localization;

/// <summary>
/// <see cref="ILocalizer"/> backed by the embedded <c>Strings.resx</c> /
/// <c>Strings.fr.resx</c> resource files in this assembly.
/// </summary>
/// <remarks>
/// Thread-safe: <see cref="ResourceManager"/> is thread-safe by design.
/// Missing keys return the key itself — never null, never an exception.
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
