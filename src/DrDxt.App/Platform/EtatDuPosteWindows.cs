using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DrDxt.App.Platform;

/// <summary>
/// 27/09/2026 : ce que la remesure au repos lit du poste, en lecture seule.
///   * temps sans saisie : GetLastInputInfo (dernier clavier ou souris de la session) ;
///   * charge du processeur : deux GetSystemTimes a une seconde d'ecart ((noyau + utilisateur - repos) / total ;
///     le temps noyau compte deja le repos, d'ou la soustraction, documentee par Microsoft pour GetSystemTimes).
/// Un appel qui echoue rend une charge inconnue (<c>null</c>) : la regle la traite comme « pas au repos ».
/// </summary>
[SupportedOSPlatform("windows")]
public static class EtatDuPosteWindows
{
    public static async Task<(TimeSpan SansSaisie, double? ChargeCpu)> LireAsync(CancellationToken ct)
    {
        var sansSaisie = SansSaisie();
        if (!GetSystemTimes(out var repos1, out var noyau1, out var user1)) return (sansSaisie, null);
        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        if (!GetSystemTimes(out var repos2, out var noyau2, out var user2)) return (sansSaisie, null);
        return (sansSaisie, Charge(repos2 - repos1, noyau2 - noyau1, user2 - user1));
    }

    /// <summary>La charge en %, depuis les ecarts de GetSystemTimes. <c>null</c> sur un total nul.</summary>
    internal static double? Charge(long repos, long noyau, long utilisateur)
    {
        var total = noyau + utilisateur;
        return total <= 0 ? null : 100.0 * (total - repos) / total;
    }

    private static TimeSpan SansSaisie()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero; // inconnu : traite comme « quelqu'un est la »
        // Les deux horloges sont en millisecondes depuis le demarrage ; dwTime boucle apres 49,7 jours,
        // d'ou la soustraction sur 32 bits non signes.
        var ecart = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(ecart);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
