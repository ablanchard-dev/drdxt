using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using DrDxt.Core;

namespace DrDxt.Measurement.Network;

/// <summary>Une phase du test : les pings vers internet et vers la box (null = pas de réponse), et le débit tenu pendant
/// la charge (null au repos).</summary>
public sealed record PhaseSousCharge(IReadOnlyList<double?> Internet, IReadOnlyList<double?> Passerelle, double? DebitMbps, long Octets);

public sealed record MesureSousCharge(PhaseSousCharge Repos, PhaseSousCharge Descente, PhaseSousCharge Montee);

/// <param name="Mesurable">Assez de réponses au ping, au repos comme en charge, pour comparer deux médianes.</param>
/// <param name="HausseInternetMs">Médiane en charge moins médiane au repos, vers internet.</param>
/// <param name="HausseBoxMs">La même chose vers la box, le témoin ; null sans box lue.</param>
/// <param name="PartDuLien">Le débit tenu rapporté à la vitesse annoncée de la carte réseau ; null si elle n'est pas lue.</param>
public sealed record VerdictDeLigne(bool Mesurable, double? ReposMs, double? ChargeMs, double? HausseInternetMs, double? HausseBoxMs,
    double? DebitMbps, double? PartDuLien)
{
    public bool Monte => HausseInternetMs >= Bufferbloat.SeuilMs;

    /// <summary>La ligne tourne à au moins <see cref="TestDeLigneSousCharge.FractionDuLien"/> de la vitesse de la carte :
    /// la file d'attente qui fait monter le ping est alors probablement dans la carte du PC, avant la box. Quelle que soit
    /// la vitesse du lien : sur un lien de 1 Gbit/s saturé, accuser la box serait faux.</summary>
    public bool FileDansLaCarte => Monte && PartDuLien >= TestDeLigneSousCharge.FractionDuLien;
}

/// <summary>
/// Le test de bufferbloat actif, sur un clic du joueur seulement : le ping au repos, puis pendant que la ligne descend à
/// plein, puis pendant qu'elle monte à plein, vers internet et vers la box comme témoin. Le bufferbloat passif
/// (<see cref="Bufferbloat"/>) attend que la machine envoie d'elle-même ; ce test fabrique la charge, contre le serveur
/// public de mesure de Cloudflare (<see cref="Hote"/>, celui de speed.cloudflare.com, dont la bibliothèque libre
/// github.com/cloudflare/speedtest documente les adresses), dit dans le README et la politique de confidentialité.
/// Durée et volume bornés ; rien d'autre n'est envoyé que des octets nuls.
/// </summary>
public static class TestDeLigneSousCharge
{
    public const string Hote = "speed.cloudflare.com";
    public const string AdresseDescente = "https://speed.cloudflare.com/__down?bytes=";
    public const string AdresseMontee = "https://speed.cloudflare.com/__up";

    public static readonly TimeSpan DureeRepos = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan DureeCharge = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan Intervalle = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan DelaiPing = TimeSpan.FromSeconds(1);

    /// <summary>Quatre requêtes à la fois : une seule ne remplit pas une ligne rapide.</summary>
    public const int Flux = 4;

    /// <summary>Au plus ce volume par sens : sur une fibre à 1 Gbit/s, la phase s'arrête avant ses huit secondes.</summary>
    public const long OctetsMaxParSens = 400_000_000;

    public const int OctetsParRequete = 25_000_000;

    /// <summary>Au-dessous, une médiane est un tirage.</summary>
    public const int MinReponses = 10;

    /// <summary>Choix de produit : à 85 % de la vitesse annoncée de la carte, la carte est le goulot probable.</summary>
    public const double FractionDuLien = 0.85;

    /// <summary>Le test entier : repos, descente, montée. L'annulation arrête tout trafic sur-le-champ.</summary>
    /// <param name="duree">Pour les tests : la durée de chaque phase ; null = <see cref="DureeRepos"/> et <see cref="DureeCharge"/>.</param>
    /// <param name="plafond">Pour les tests : le volume par sens ; null = <see cref="OctetsMaxParSens"/>.</param>
    /// <param name="repos">Pour les tests : la durée du repos seul, quand il doit être plus court que la charge.</param>
    public static async Task<MesureSousCharge> MesurerAsync(HttpClient client, INetworkProbe sonde, string internet, string? passerelle,
        CancellationToken ct, TimeSpan? duree = null, long? plafond = null, TimeSpan? repos = null)
    {
        var max = plafond ?? OctetsMaxParSens;
        var auRepos = await PhaseAsync(sonde, internet, passerelle, repos ?? duree ?? DureeRepos, null, max, ct).ConfigureAwait(false);
        var descente = await PhaseAsync(sonde, internet, passerelle, duree ?? DureeCharge, (compte, c) => DescendreAsync(client, compte, max, c), max, ct).ConfigureAwait(false);
        var montee = await PhaseAsync(sonde, internet, passerelle, duree ?? DureeCharge, (compte, c) => MonterAsync(client, compte, max, c), max, ct).ConfigureAwait(false);
        return new MesureSousCharge(auRepos, descente, montee);
    }

    private sealed class Compteur { public long Octets; }

    private static async Task<PhaseSousCharge> PhaseAsync(INetworkProbe sonde, string internet, string? passerelle, TimeSpan duree,
        Func<Compteur, CancellationToken, Task>? charge, long max, CancellationToken ct)
    {
        using var fin = CancellationTokenSource.CreateLinkedTokenSource(ct);
        fin.CancelAfter(duree);
        var compte = new Compteur();
        var chrono = Stopwatch.StartNew();
        var flux = charge is null ? [] : Enumerable.Range(0, Flux).Select(_ => Task.Run(() => charge(compte, fin.Token))).ToArray();
        var versInternet = new List<double?>();
        var versBox = new List<double?>();
        try
        {
            while (!fin.IsCancellationRequested)
            {
                if (charge is not null && Interlocked.Read(ref compte.Octets) >= max) break;
                var i = sonde.ProbeAsync(internet, DelaiPing, fin.Token);
                var b = passerelle is null ? null : sonde.ProbeAsync(passerelle, DelaiPing, fin.Token);
                versInternet.Add((await i.ConfigureAwait(false)).RoundTripMs);
                if (b is not null) versBox.Add((await b.ConfigureAwait(false)).RoundTripMs);
                await Task.Delay(Intervalle, fin.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* fin de la phase */ }
        finally
        {
            await fin.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(flux).ConfigureAwait(false); }
            catch (Exception) { /* une requête coupée à la fin de la phase n'est pas une panne */ }
        }
        ct.ThrowIfCancellationRequested();
        var secondes = chrono.Elapsed.TotalSeconds;
        var octets = Interlocked.Read(ref compte.Octets);
        return new PhaseSousCharge(versInternet, versBox,
            charge is null || secondes <= 0 ? null : octets * 8 / secondes / 1_000_000, octets);
    }

    private static async Task DescendreAsync(HttpClient client, Compteur compte, long max, CancellationToken ct)
    {
        var tampon = new byte[64 * 1024];
        while (!ct.IsCancellationRequested && Interlocked.Read(ref compte.Octets) < max)
        {
            using var reponse = await client.GetAsync(AdresseDescente + OctetsParRequete.ToString(CultureInfo.InvariantCulture),
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode) return;
            await using var flux = await reponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            int lus;
            while ((lus = await flux.ReadAsync(tampon, ct).ConfigureAwait(false)) > 0)
                if (Interlocked.Add(ref compte.Octets, lus) >= max) return;
        }
    }

    private static async Task MonterAsync(HttpClient client, Compteur compte, long max, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Interlocked.Read(ref compte.Octets) < max)
        {
            using var contenu = new OctetsNuls(OctetsParRequete, compte, max);
            using var reponse = await client.PostAsync(AdresseMontee, contenu, ct).ConfigureAwait(false);
            if (!reponse.IsSuccessStatusCode) return;
        }
    }

    /// <summary>Des octets nuls, comptés au fil de l'envoi : rien de la machine ne part. L'envoi s'arrête au plafond du sens,
    /// à un tampon près, au lieu de finir la requête en cours.</summary>
    private sealed class OctetsNuls(int taille, Compteur compte, long max) : HttpContent
    {
        private static readonly byte[] Zeros = new byte[64 * 1024];

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            for (var reste = taille; reste > 0;)
            {
                if (Interlocked.Read(ref compte.Octets) >= max) throw new OperationCanceledException("volume du sens atteint");
                var n = Math.Min(reste, Zeros.Length);
                await stream.WriteAsync(Zeros.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref compte.Octets, n);
                reste -= n;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = taille;
            return true;
        }
    }

    /// <summary>Pur : un sens jugé contre le repos. <paramref name="lienMbps"/> : la vitesse annoncée de la carte réseau.</summary>
    public static VerdictDeLigne Juger(PhaseSousCharge repos, PhaseSousCharge charge, double? lienMbps)
    {
        var r = Mediane(repos.Internet);
        var c = Mediane(charge.Internet);
        var part = charge.DebitMbps is { } d && lienMbps is > 0 ? d / lienMbps : null;
        // Une charge qui n'a rien fait passer (serveur injoignable, requêtes refusées) laisse des pings pris au repos : les
        // juger dirait « calme » d'une ligne qu'on n'a pas chargée.
        if (charge.DebitMbps is not > 0)
            return new VerdictDeLigne(false, r, c, null, null, charge.DebitMbps, part);
        if (Reponses(repos.Internet) < MinReponses || Reponses(charge.Internet) < MinReponses || r is null || c is null)
            return new VerdictDeLigne(false, r, c, null, null, charge.DebitMbps, part);
        var rb = Reponses(repos.Passerelle) >= MinReponses ? Mediane(repos.Passerelle) : null;
        var cb = Reponses(charge.Passerelle) >= MinReponses ? Mediane(charge.Passerelle) : null;
        return new VerdictDeLigne(true, r, c, c - r, rb is { } x && cb is { } y ? y - x : null, charge.DebitMbps, part);
    }

    /// <summary>Le compte rendu, une phrase par sens, tel que l'écran l'affiche.</summary>
    public static string Phrase(MesureSousCharge m, double? lienMbps, DateTimeOffset quand)
    {
        var mo = (m.Descente.Octets + m.Montee.Octets) / 1_000_000;
        return Docteur.T("TL.Entete", quand.ToLocalTime().ToString("g", Docteur.Langue), Hote, mo)
            + " " + Sens("TL.Descente", Juger(m.Repos, m.Descente, lienMbps), lienMbps)
            + " " + Sens("TL.Montee", Juger(m.Repos, m.Montee, lienMbps), lienMbps);
    }

    private static string Sens(string cle, VerdictDeLigne v, double? lienMbps)
    {
        var debut = Docteur.T(cle, v.DebitMbps ?? 0);
        if (!v.Mesurable) return debut + (v.DebitMbps is > 0 ? Docteur.T("TL.NonMesurable", MinReponses) : Docteur.T("TL.SansCharge", Hote));
        var temoin = v.HausseBoxMs is { } b ? Docteur.T("TL.Box", b) : "";
        var ping = Docteur.T("TL.Ping", v.ReposMs, v.ChargeMs, v.HausseInternetMs) + temoin;
        return debut + ping + (v.FileDansLaCarte ? Docteur.T("TL.Carte", v.PartDuLien!.Value * 100, lienMbps)
            : v.Monte ? Docteur.T("TL.Monte", v.HausseInternetMs) : Docteur.T("TL.Calme", Bufferbloat.SeuilMs));
    }

    /// <summary>La vitesse annoncée de la carte qui porte la passerelle (NetworkInterface.Speed, en bits par seconde) ; null
    /// si aucune carte active ne la porte.</summary>
    public static double? VitesseDuLienMbps(string? passerelle)
    {
        if (passerelle is null || !IPAddress.TryParse(passerelle, out var adresse)) return null;
        try
        {
            var carte = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.Equals(adresse)));
            return carte is { Speed: > 0 } ? carte.Speed / 1_000_000.0 : null;
        }
        catch (Exception) { return null; }
    }

    private static int Reponses(IReadOnlyList<double?> pings) => pings.Count(p => p is not null);

    private static double? Mediane(IReadOnlyList<double?> pings)
    {
        var v = pings.Where(p => p is not null).Select(p => p!.Value).Order().ToList();
        if (v.Count == 0) return null;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }
}
