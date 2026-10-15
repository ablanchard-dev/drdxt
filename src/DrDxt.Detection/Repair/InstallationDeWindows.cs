namespace DrDxt.Detection.Repair;

/// <summary>
/// La date de la dernière installation de Windows, que « Réinstaller
/// maintenant » (réinstallation en place, fichiers et applications gardés) réécrit. Une mise à niveau
/// vers une nouvelle version (23H2 ⇒ 24H2) la réécrit aussi ; elle se dit donc « installation ou mise à niveau ». Valeur InstallDate de
/// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion, en secondes depuis 1970 (UTC). Relevé sur la machine de
/// référence : 10/04/2025 01:24, la même heure que la dernière clé « Source OS (Updated on 4/10/2025 01:08:33) » de
/// HKLM\SYSTEM\Setup. Lecture seule ; <c>null</c> = non lu.
/// </summary>
public static class InstallationDeWindows
{
    internal static DateTimeOffset? Depuis(object? valeur)
        => valeur is int s && s > 0 ? DateTimeOffset.FromUnixTimeSeconds((uint)s)
            : valeur is long l && l > 0 ? DateTimeOffset.FromUnixTimeSeconds(l) : null;

    public static DateTimeOffset? Lire()
    {
        try { return Depuis(Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "InstallDate", null)); }
        catch (Exception) { return null; }
    }

    public static IReadOnlyList<FaitDate> Fait()
        => Lire() is { } quand ? [new FaitDate(quand, DrDxt.Core.Docteur.T("IW.Fait"))] : [];
}
