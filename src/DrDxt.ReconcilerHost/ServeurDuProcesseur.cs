using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DrDxt.Core;
using Microsoft.Extensions.Hosting;

namespace DrDxt.ReconcilerHost;

/// <summary>
/// L'App DrDXT tourne sans droits ; les registres du processeur ne se lisent qu'en administrateur. Le
/// service, seul processus élevé permanent, les sert par un tube nommé en lecture seule : une connexion = une lecture de
/// l'ensemble fermé (<see cref="EtatCpuServi"/>), écrite puis le tube se ferme. Rien n'est lu du client (aucune entrée :
/// le principe « aucun point d'entrée privilégié » du service tient, il n'y a rien à lui demander d'autre).
///
/// Sécurité : DACL protégée (SYSTEM plein, Utilisateurs authentifiés en lecture, réseau refusé), première instance
/// exigée à la création du nom, et l'instance suivante créée avant de rendre la courante (sinon le nom
/// serait libre un instant, et un squatteur pourrait le garder) ; le client vérifie en plus que le PID du serveur est celui du
/// service. Aucune attente sans borne sur le client.
///
/// PawnIO est fermé après <see cref="Inactivite"/> sans connexion. Limite dite : pendant une partie, le relevé demande
/// chaque seconde, la poignée reste donc ouverte tout le temps de la partie ; fermer la poignée ne décharge pas le pilote.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServeurDuProcesseur : BackgroundService
{
    public static readonly TimeSpan Inactivite = TimeSpan.FromSeconds(10);
    /// <summary>L'écriture d'une ligne vers un client ne dure jamais plus que ça.</summary>
    public static readonly TimeSpan DelaiEcriture = TimeSpan.FromSeconds(1);
    /// <summary>Chaque connexion est servie à part (un client muet ne retarde plus les suivants au-delà
    /// de leur propre borne). Quatre instances se saturaient avec quatre clients muets, et le tube
    /// tombait 30 s. Sans plafond : 20 connexions par seconde au plus, chacune bornée (1 s d'écriture, 10 s de PawnIO au
    /// pire), donc ~220 instances dans le pire cas, quelques Ko chacune.</summary>
    private const int Instances = NamedPipeServerStream.MaxAllowedServerInstances;

    private readonly Func<EtatCpu?> _lire;
    private readonly Func<(RegistresCpuLus? Limites, ulong? CopieMmio)> _lireLimites;
    private readonly Func<IReadOnlyList<VentilateurLu>?> _lireVentilateurs;
    private readonly Action _fermer;
    private readonly Func<bool> _ouvert;
    private readonly Func<DateTimeOffset> _horloge;
    private readonly string _nom;
    private long _derniereDemandeTicks = DateTimeOffset.MinValue.UtcTicks;

    public ServeurDuProcesseur()
        : this(DrDxt.Detection.Cpu.TemperaturePawnIo.LireEtatDirect, DrDxt.GpuNative.PawnIo.PawnIoIntel.FermerTout,
            DrDxt.GpuNative.PawnIo.PawnIoIntel.Ouvert, () => DateTimeOffset.Now, EtatCpuServi.NomDuTube, LimitesDirectes,
            VentilateursDirects) { }

    /// <summary>Coutures de test : la lecture, la fermeture de PawnIO, l'horloge, le nom du tube, la lecture des limites, celle
    /// des ventilateurs.</summary>
    internal ServeurDuProcesseur(Func<EtatCpu?> lire, Action fermer, Func<bool> ouvert, Func<DateTimeOffset> horloge, string nom,
        Func<(RegistresCpuLus? Limites, ulong? CopieMmio)>? lireLimites = null, Func<IReadOnlyList<VentilateurLu>?>? lireVentilateurs = null)
        => (_lire, _fermer, _ouvert, _horloge, _nom, _lireLimites, _lireVentilateurs)
            = (lire, fermer, ouvert, horloge, nom, lireLimites ?? (() => (null, null)), lireVentilateurs ?? (() => null));

    /// <summary>Les ventilateurs de la carte mère, lus ici (le service a les droits) ; null sans PawnIO ou sur une puce non lue.
    /// Une lecture coûte ~79 ms de processeur : la dernière sert pendant PawnIoSuperIo.IntervalleLecture (5 s).</summary>
    private static IReadOnlyList<VentilateurLu>? VentilateursDirects()
        => DrDxt.GpuNative.PawnIo.PawnIoSuperIo.LireRecent()?.Ventilateurs.Select(v => new VentilateurLu(v.Index, v.TrMin, v.PwmPourcent)).ToList();

    /// <summary>Les registres bruts des limites, lus ici (le service a les droits) ; rien sans PawnIO ou hors Intel.</summary>
    private static (RegistresCpuLus?, ulong?) LimitesDirectes()
        => DrDxt.GpuNative.PawnIo.PawnIoIntel.LireRegistres() is { } r
            ? (new RegistresCpuLus(r.RaplPowerUnit, r.PkgPowerLimit, r.PkgPowerInfo), DrDxt.GpuNative.PawnIo.PawnIoIntel.LireCopieMmioLimite())
            : (null, null);

    /// <summary>SYSTEM et le compte qui crée le tube (SYSTEM lui-même dans le service) pleins : créer l'instance suivante
    /// le demande ; Utilisateurs authentifiés en lecture seule ; réseau refusé (un refus passe avant tout).</summary>
    internal static PipeSecurity Securite()
    {
        var s = new PipeSecurity();
        s.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        if (WindowsIdentity.GetCurrent().User is { } createur)
            s.AddAccessRule(new PipeAccessRule(createur, PipeAccessRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.Read | PipeAccessRights.Synchronize, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        return s;
    }

    // InOut côté serveur pour savoir quand le client a raccroché (une lecture qui rend 0) ; le client, lui, n'a que la
    // lecture (DACL) : il ne peut rien écrire.
    private NamedPipeServerStream Creer(bool premiere) => NamedPipeServerStreamAcl.Create(_nom, PipeDirection.InOut, Instances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | (premiere ? PipeOptions.FirstPipeInstance : PipeOptions.None), 0, 0, Securite());

    /// <summary>Sert jusqu'à l'annulation (ou <paramref name="connexionsMax"/> connexions, pour les tests). Faux si le nom
    /// n'a pas pu être pris (squatteur, autre instance) : rien n'est servi.</summary>
    internal async Task<bool> ServirAsync(CancellationToken ct, int connexionsMax = int.MaxValue)
    {
        NamedPipeServerStream courante;
        try { courante = Creer(premiere: true); }
        catch (Exception) { return false; }
        var enCours = new List<Task>();
        try
        {
            for (var n = 0; n < connexionsMax && !ct.IsCancellationRequested; n++)
            {
                var connecte = true;
                // Un client qui se connecte et raccroche aussitôt fait lever l'attente (« canal en cours de fermeture ») :
                // ce n'est pas une panne du service, on passe à l'instance suivante.
                try { await courante.WaitForConnectionAsync(ct).ConfigureAwait(false); }
                catch (IOException) { connecte = false; }
                // L'instance suivante existe avant que la courante ne soit rendue : le nom n'est jamais libre.
                // Un échec ici se retente (la courante tient le nom) au lieu d'abandonner le tube 30 s.
                NamedPipeServerStream suivante;
                while (true)
                {
                    try { suivante = Creer(premiere: false); break; }
                    catch (Exception) when (!ct.IsCancellationRequested) { await Task.Delay(100, ct).ConfigureAwait(false); }
                }
                var servie = courante;
                enCours.RemoveAll(t => t.IsCompleted);
                enCours.Add(Task.Run(async () =>
                {
                    try { if (connecte) await RepondreAsync(servie, ct).ConfigureAwait(false); }
                    catch (Exception) { }
                    finally { await servie.DisposeAsync().ConfigureAwait(false); }
                }, CancellationToken.None));
                courante = suivante;
                // Au plus 20 lectures par seconde, quel que soit le client qui insiste.
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await courante.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(enCours).ConfigureAwait(false);
        }
        return true;
    }

    private async Task RepondreAsync(NamedPipeServerStream tube, CancellationToken ct)
    {
        Interlocked.Exchange(ref _derniereDemandeTicks, _horloge().UtcTicks);
        EtatCpu? etat = null;
        (RegistresCpuLus? Limites, ulong? CopieMmio) limites = (null, null);
        try { etat = _lire(); }
        catch (Exception) { }
        try { limites = _lireLimites(); }
        catch (Exception) { }
        IReadOnlyList<VentilateurLu>? ventilateurs = null;
        try { ventilateurs = _lireVentilateurs(); }
        catch (Exception) { } // une panne des ventilateurs ne prive jamais de l'état du processeur
        var octets = Encoding.UTF8.GetBytes(new EtatCpuServi(_horloge(), etat, limites.Limites, limites.CopieMmio, ventilateurs).VersJson() + "\n");
        using var borne = CancellationTokenSource.CreateLinkedTokenSource(ct);
        borne.CancelAfter(DelaiEcriture);
        try
        {
            await tube.WriteAsync(octets, borne.Token).ConfigureAwait(false);
            // Fermer tout de suite perdrait la ligne non lue ; vidanger (FlushFileBuffers) bloque sans borne si le client ne
            // lit jamais. On attend qu'il raccroche, borné et asynchrone.
            await tube.ReadAsync(new byte[1], borne.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { } // client parti ou qui ne lit pas : on passe au suivant
    }

    /// <summary>Ferme PawnIO s'il est ouvert et que personne n'a rien demandé depuis <see cref="Inactivite"/>.</summary>
    internal void FermerSiInactif()
    {
        var derniere = new DateTimeOffset(Interlocked.Read(ref _derniereDemandeTicks), TimeSpan.Zero);
        if (_ouvert() && _horloge() - derniere >= Inactivite) _fermer();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var surveillance = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var veille = Task.Run(async () =>
        {
            try { while (await surveillance.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) FermerSiInactif(); }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
                if (!await ServirAsync(stoppingToken).ConfigureAwait(false))
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await veille.ConfigureAwait(false);
            _fermer();
        }
    }
}
