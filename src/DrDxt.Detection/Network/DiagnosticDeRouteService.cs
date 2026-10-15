using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using DrDxt.Core;
using DrDxt.Measurement.Network;

namespace DrDxt.Detection.Network;

/// <summary>Sonde un routeur du chemin : un ping à durée de vie limitée (TTL), comme tracert.</summary>
public interface ISondeDeSaut
{
    /// <summary>Le routeur qui a répondu à ce rang et son aller-retour, ou (null, null) sans réponse.</summary>
    Task<(IPAddress? Adresse, double? Ms)> SonderAsync(IPAddress cible, int ttl, TimeSpan delai, CancellationToken ct);
}

/// <summary>La vraie sonde. Relevé sur la machine le 03/10/2026 : Windows rend <c>RoundtripTime = 0</c> pour un TTL expiré ;
/// l'aller-retour est donc chronométré autour de l'envoi.</summary>
public sealed class SondeDeSautWindows : ISondeDeSaut
{
    private static readonly byte[] Charge = new byte[32];

    public async Task<(IPAddress? Adresse, double? Ms)> SonderAsync(IPAddress cible, int ttl, TimeSpan delai, CancellationToken ct)
    {
        using var ping = new Ping();
        var chrono = Stopwatch.StartNew();
        var r = await ping.SendPingAsync(cible, delai, Charge, new PingOptions(ttl, dontFragment: true), ct).ConfigureAwait(false);
        chrono.Stop();
        return r.Status is IPStatus.TtlExpired or IPStatus.Success ? (r.Address, chrono.Elapsed.TotalMilliseconds) : (null, null);
    }
}

/// <summary>
/// Pendant la fenêtre de mesure en jeu, trouve le serveur de la partie (les envois UDP du
/// jeu), le sonde, sonde la box en même temps, et rend la phrase du domaine Réseau (<see cref="DiagnosticDeRoute"/>).
/// Le serveur de Call of Duty ignore le ping. Dix sondes rapides le disent ;
/// alors le chemin est remonté routeur par routeur et la route est jugée jusqu'au dernier qui répond.
/// Rien n'est écrit ; seuls des pings partent, vers le serveur que le jeu contacte déjà, les routeurs du chemin et la box.
/// <c>null</c> = pas pu regarder (pas de droits, pas d'UDP vers une adresse publique) : rien n'est affirmé.
/// </summary>
public sealed class DiagnosticDeRouteService
{
    /// <summary>Le temps d'écoute pour repérer le serveur, pris sur la fenêtre.</summary>
    public static readonly TimeSpan Ecoute = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan Intervalle = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Delai = TimeSpan.FromSeconds(1);
    private const int SondesDEssai = 10;

    /// <summary>Au-delà, la route est plus longue que tout chemin d'Internet ; trois rangs muets de suite arrêtent la remontée.</summary>
    internal const int TtlMaximum = 30, MuetsDeSuiteAvantArret = 3;
    private static readonly TimeSpan DelaiParSaut = TimeSpan.FromMilliseconds(800);

    private readonly Func<int, TimeSpan, IReadOnlyList<EnvoiUdp>?> _ecouter;
    private readonly INetworkLatencyMeter _compteur;
    private readonly Func<string?> _passerelle;
    private readonly ISondeDeSaut _sonde;

    public DiagnosticDeRouteService(Func<int, TimeSpan, IReadOnlyList<EnvoiUdp>?> ecouter, INetworkLatencyMeter compteur,
        Func<string?> passerelle, ISondeDeSaut? sonde = null)
        => (_ecouter, _compteur, _passerelle, _sonde) = (ecouter, compteur, passerelle, sonde ?? new SondeDeSautWindows());

    public async Task<RepairFinding?> MesurerAsync(int pid, TimeSpan fenetre, CancellationToken ct = default)
    {
        try
        {
            var envois = await Task.Run(() => _ecouter(pid, Ecoute), ct).ConfigureAwait(false);
            if (envois is null || EnvoisUdp.ServeurDeLaPartie(envois, pid) is not { } serveur) return null;
            var adresse = serveur.Address.ToString();

            var essai = await _compteur.MeasureAsync(adresse, SondesDEssai, Intervalle, Delai, ct).ConfigureAwait(false);
            if (!essai.IsOk) return null;

            SautDeRoute? jusqua = null;
            if (!essai.Value!.HasLatency)
            {
                jusqua = DiagnosticDeRoute.DernierSautQuiRepond(await RemonterLeCheminAsync(serveur.Address, ct).ConfigureAwait(false));
                if (jusqua?.Adresse is null) return DiagnosticDeRoute.SansReponse(adresse);
            }

            var sondes = Math.Max(DiagnosticDeRoute.EchantillonsMinimum, (int)((fenetre - Ecoute) / Intervalle));
            var passerelle = _passerelle();
            var versBox = passerelle is null ? null : _compteur.MeasureAsync(passerelle, sondes, Intervalle, Delai, ct);

            NetworkLatencyReport r;
            if (jusqua is null)
            {
                var mesure = await _compteur.MeasureAsync(adresse, sondes, Intervalle, Delai, ct).ConfigureAwait(false);
                if (!mesure.IsOk) return null;
                r = mesure.Value!;
            }
            else
            {
                // Mesuré sur la machine le 03/10 en partie : le dernier routeur qui répond (rang 9) renvoie « TTL expiré »
                // mais ignore un ping direct. Il est donc mesuré comme pendant la remontée : à son rang, vers le serveur.
                r = await MesurerAuRangAsync(serveur.Address, jusqua, sondes, ct).ConfigureAwait(false);
            }
            if (!r.HasLatency) return DiagnosticDeRoute.SansReponse(adresse);

            var attribution = NetworkPathVerdict.Unknown;
            if (versBox is not null && await versBox.ConfigureAwait(false) is { IsOk: true } box)
                attribution = NetworkPathAnalyzer.Compare(box.Value!, r).Verdict;

            return DiagnosticDeRoute.Constat(
                new MesureDeRoute(r.Received, r.AvgMs ?? 0, r.LossPercent, r.JitterMs ?? 0, attribution), adresse, jusqua);
        }
        catch
        {
            // Mesurer la route ne doit jamais être la raison pour laquelle un scan s'arrête.
            return null;
        }
    }

    /// <summary>Des sondes répétées au rang du dernier routeur qui répond, espacées comme les pings du compteur ; le même
    /// calcul de latence, pertes et gigue que pour un ping direct.</summary>
    internal async Task<NetworkLatencyReport> MesurerAuRangAsync(IPAddress serveur, SautDeRoute saut, int sondes, CancellationToken ct)
    {
        var mesures = new List<NetworkProbeSample>(sondes);
        for (var i = 0; i < sondes && !ct.IsCancellationRequested; i++)
        {
            var chrono = Stopwatch.StartNew();
            var (a, ms) = await _sonde.SonderAsync(serveur, saut.Ttl, Delai, ct).ConfigureAwait(false);
            mesures.Add(new NetworkProbeSample(a is null ? null : ms));
            var reste = Intervalle - chrono.Elapsed;
            if (reste > TimeSpan.Zero) await Task.Delay(reste, ct).ConfigureAwait(false);
        }
        return NetworkLatencyStatistics.Summarize(saut.Adresse!.ToString(), mesures);
    }

    /// <summary>Comme tracert : un rang après l'autre, jusqu'au serveur, au TTL maximum ou à trois rangs muets de suite.</summary>
    internal async Task<IReadOnlyList<SautDeRoute>> RemonterLeCheminAsync(IPAddress serveur, CancellationToken ct)
    {
        var sauts = new List<SautDeRoute>();
        var muets = 0;
        for (var ttl = 1; ttl <= TtlMaximum && muets < MuetsDeSuiteAvantArret; ttl++)
        {
            var (a, ms) = await _sonde.SonderAsync(serveur, ttl, DelaiParSaut, ct).ConfigureAwait(false);
            sauts.Add(new SautDeRoute(ttl, a, ms));
            if (a is null) { muets++; continue; }
            muets = 0;
            if (a.Equals(serveur)) break;
        }
        return sauts;
    }
}
