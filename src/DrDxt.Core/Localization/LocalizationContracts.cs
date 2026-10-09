using System.Globalization;

namespace DrDxt.Core;

// --- Localization ---

/// <summary>
/// Resolves a resource key to a localized string for a given culture.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe (singleton lifetime in DI).
/// On a missing key the key itself is returned — never null, never an exception.
/// This contract lives in Core so domain types can reference keys without
/// taking an infrastructure dependency.
/// </remarks>
public interface ILocalizer
{
    /// <summary>
    /// Returns the localized string for <paramref name="key"/> in
    /// <paramref name="culture"/> (null = <see cref="CultureInfo.CurrentUICulture"/>).
    /// On a missing key, returns the key itself.
    /// </summary>
    string Get(string key, CultureInfo? culture = null);
}
