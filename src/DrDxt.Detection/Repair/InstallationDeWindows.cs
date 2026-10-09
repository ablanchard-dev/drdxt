namespace DrDxt.Detection.Repair;

/// <summary>
/// 07/10/2026 (chantier D8, enquête n° 18) : la date de la dernière installation de Windows, que « Réinstaller
/// maintenant » (réinstallation en place, fichiers et applications gardés) réécrit. Revue F-49 : une mise à niveau
/// vers une nouvelle version (23H2 ⇒ 24H2) la réécrit aussi ; elle se dit donc « installation ou mise à niveau ». Valeur InstallDate de
/// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion, en secondes depuis 1970 (UTC). Relevé le 07/10 sur le PC du
/// mainteneur : 10/04/2025 01:24, la même heure que la dernière clé « Source OS (Updated on 4/10/2025 01:08:33) » de
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
        => Lire() is { } quand ? [new FaitDate(quand, "installation ou mise à niveau de Windows")] : [];
}
