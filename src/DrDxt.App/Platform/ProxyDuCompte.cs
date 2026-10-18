using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace DrDxt.App.Platform;

/// <summary>
/// Les identifiants Windows du compte ne partent que vers un proxy que quelqu'un a configuré : une variable HTTP(S)_PROXY,
/// ou un proxy manuel quand ni la détection automatique ni un script ne sont actifs. Un proxy découvert par WPAD (Wi-Fi
/// public, réseau d'événement) répond 407 et recevait l'échange NTLM du compte à chaque lancement, sans clic : de quoi le
/// relayer ou le casser hors ligne. Avec la détection ou un script, le proxy se choisit hôte par hôte (le téléchargement
/// est redirigé vers un autre hôte que le manifeste) : la décision porte sur la configuration, jamais sur une adresse.
/// Limite dite dans SECURITE : derrière un proxy d'entreprise par script ou WPAD qui exige l'authentification, la mise à
/// jour reste injoignable.
/// </summary>
internal static class ProxyDuCompte
{
    /// <summary>La configuration proxy du compte : détection automatique, script PAC, proxy manuel, et la variable
    /// d'environnement valide que .NET suivrait (elle remplace alors tout le reste).</summary>
    internal sealed record Configuration(bool DetectionAuto, string? Script, string? Manuel, string? Environnement);

    internal static bool IdentifiantsAutorises(Configuration c)
        => !string.IsNullOrWhiteSpace(c.Environnement)
           || (!c.DetectionAuto && string.IsNullOrWhiteSpace(c.Script) && !string.IsNullOrWhiteSpace(c.Manuel));

    /// <summary>Le transport des clients HTTP de l'App. <paramref name="lire"/> : pour les tests.</summary>
    internal static HttpClientHandler Transport(bool redirections = true, Func<Configuration>? lire = null)
    {
        var autorises = false;
        try { autorises = IdentifiantsAutorises((lire ?? Lire)()); }
        catch (Exception) { } // configuration illisible : rien ne part
        return new HttpClientHandler
        {
            AllowAutoRedirect = redirections,
            DefaultProxyCredentials = autorises ? CredentialCache.DefaultCredentials : null,
        };
    }

    // .NET prend la première de ces variables qui se lit comme une adresse, et n'utilise alors plus le proxy du système.
    private static readonly string[] Variables = ["HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY"];

    private static bool Adresse(string? v)
        => !string.IsNullOrWhiteSpace(v) && Uri.TryCreate(v.Contains("://", StringComparison.Ordinal) ? v : "http://" + v, UriKind.Absolute, out _);

    private static Configuration Lire()
    {
        var environnement = Variables.Select(Environment.GetEnvironmentVariable).FirstOrDefault(Adresse);
        // Illisible : traité comme la détection automatique, rien ne part.
        if (!WinHttpGetIEProxyConfigForCurrentUser(out var ie)) return new(true, null, null, environnement);
        try
        {
            return new(ie.fAutoDetect, Marshal.PtrToStringUni(ie.lpszAutoConfigUrl), Marshal.PtrToStringUni(ie.lpszProxy), environnement);
        }
        finally
        {
            foreach (var p in new[] { ie.lpszAutoConfigUrl, ie.lpszProxy, ie.lpszProxyBypass })
                if (p != IntPtr.Zero) GlobalFree(p);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINHTTP_CURRENT_USER_IE_PROXY_CONFIG
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fAutoDetect;
        public IntPtr lpszAutoConfigUrl;
        public IntPtr lpszProxy;
        public IntPtr lpszProxyBypass;
    }

    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetIEProxyConfigForCurrentUser(out WINHTTP_CURRENT_USER_IE_PROXY_CONFIG pProxyConfig);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
