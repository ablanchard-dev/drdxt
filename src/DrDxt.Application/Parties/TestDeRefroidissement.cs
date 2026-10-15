using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Application.Parties;

/// <summary>
/// Le test d'abord fait à la main sur la machine de référence (boucles PowerShell sur 28 fils, sonde en
/// administrateur, arrêt à la main) : tous les cœurs chargés, l'état du processeur lu chaque seconde, arrêt de sécurité
/// tout seul. Ce soir-là, il s'est arrêté à 55 s : 100 °C au cœur le plus chaud dès 35 s, à 160 W. Une charge pleine est
/// plus dure qu'un jeu (le jeu tirait 100 à 110 W) : le verdict le dit, il ne se lit pas comme une partie.
/// Toutes les coutures sont injectées : le test se prouve sans charger la machine.
/// </summary>
public sealed class TestDeRefroidissement
{
    public static readonly TimeSpan DureeParDefaut = TimeSpan.FromMinutes(2);
    /// <summary>Arrêt de sécurité : le cœur le plus chaud à 99 °C ou plus, dix secondes d'affilée.</summary>
    public const int SeuilSecuriteC = 99;
    public const int SecondesAuSeuil = 10;
    /// <summary>Sans lecture, pas de sécurité. Cinq secondes de suite sans état lu arrêtent la charge (plus
    /// que la pause de 2 s du client après un raté isolé du service, qui fait 3 secondes nulles sans perte réelle).</summary>
    public const int SecondesSansLectureMax = 5;

    public sealed record Resultat(int Secondes, FreinageCpu? Freinage, bool ArretSecurite, int? SecondesAvantSeuil, bool LectureePerdue = false);

    private readonly Func<CancellationToken, IDisposable> _charger;
    private readonly Func<EtatCpu?> _lire;
    private readonly Func<TimeSpan, CancellationToken, Task> _attendre;

    /// <param name="charger">Démarre la charge ; la libérer l'arrête.</param>
    public TestDeRefroidissement(Func<CancellationToken, IDisposable> charger, Func<EtatCpu?> lire, Func<TimeSpan, CancellationToken, Task> attendre)
        => (_charger, _lire, _attendre) = (charger, lire, attendre);

    public async Task<Resultat> LancerAsync(TimeSpan duree, CancellationToken ct)
    {
        var etats = new List<EtatCpu>();
        var auSeuil = 0;
        int? premierAuSeuil = null;
        var arret = false;
        var perdue = false;
        // Pas de charge sans une première lecture (sinon l'arrêt de sécurité ne pourrait jamais jouer).
        if (_lire() is null) return new Resultat(0, null, false, null, LectureePerdue: true);
        var sansLecture = 0;
        var ecoulees = 0; // Le temps de charge, pas le nombre de secondes lues
        using (_charger(ct))
        {
            for (var s = 0; s < (int)duree.TotalSeconds && !ct.IsCancellationRequested; s++)
            {
                try { await _attendre(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                ecoulees = s + 1;
                if (_lire() is not { } e)
                {
                    if (++sansLecture >= SecondesSansLectureMax) { perdue = true; break; }
                    continue;
                }
                sansLecture = 0;
                etats.Add(e);
                if (e.ChaudC >= SeuilSecuriteC)
                {
                    premierAuSeuil ??= s + 1;
                    if (++auSeuil >= SecondesAuSeuil) { arret = true; break; }
                }
                else auSeuil = 0;
            }
        }
        return new Resultat(ecoulees, FreinageCpu.Depuis(etats), arret, premierAuSeuil, perdue);
    }

    /// <summary>La charge : un fil par processeur logique, en priorité basse (le reste de la machine répond), qui calcule en
    /// virgule flottante vectorisée, jusqu'à la
    /// libération. Elle reste plus douce qu'un outil de torture (Prime95, OCCT) : le verdict le dit.</summary>
    public static IDisposable ChargePleine(CancellationToken ct) => new Charge(ct);

    /// <summary>Libérer annule (disposer un jeton ne l'annule pas : les fils tourneraient sans fin) puis attend les fils.</summary>
    public sealed class Charge : IDisposable
    {
        private readonly CancellationTokenSource _fin;
        private readonly List<Thread> _fils = [];

        public Charge(CancellationToken ct)
        {
            _fin = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var jeton = _fin.Token;
            for (var i = 0; i < Environment.ProcessorCount; i++)
            {
                var fil = new Thread(() => Calculer(jeton)) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
                _fils.Add(fil);
                fil.Start();
            }
        }

        public bool Tourne => _fils.Any(f => f.IsAlive);

        public void Dispose()
        {
            _fin.Cancel();
            // Une seule échéance pour tous les fils.
            var echeance = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            foreach (var f in _fils) f.Join(Max(TimeSpan.Zero, echeance - DateTime.UtcNow));
            _fin.Dispose();
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

        private static void Calculer(CancellationToken jeton)
        {
            var x = System.Numerics.Vector<float>.One;
            while (!jeton.IsCancellationRequested)
                for (var i = 0; i < 4096; i++) x = Pas(x);
            GC.KeepAlive(x);
        }

        private static readonly System.Numerics.Vector<float> A = new(0.9999f), B = new(0.0001f);

        /// <summary>Un pas de la charge (couture de test). X·a + b avec a &lt; 1 converge vers b/(1-a) = 1
        /// et y reste : une multiplication-addition vectorielle par pas, jamais l'infini ni NaN (l'ancienne forme débordait en
        /// quelques millisecondes et calculait sur des NaN).</summary>
        public static System.Numerics.Vector<float> Pas(System.Numerics.Vector<float> x) => x * A + B;
    }

    /// <summary>Pur : le verdict, en disant qu'une charge pleine est plus dure qu'un jeu.</summary>
    public static RepairFinding Juger(Resultat r, LimiteDePuissanceLue? limite)
    {
        var inv = CultureInfo.InvariantCulture;
        if (r.LectureePerdue && !r.ArretSecurite)
            return new RepairFinding("CPU", "test de refroidissement", null, null, RepairStatus.Unknown,
                Docteur.T("TR.Perdue"));
        if (r.Freinage is not { } f)
            return new RepairFinding("CPU", "test de refroidissement", null, null, RepairStatus.Unknown,
                Docteur.T("TR.NonLu"));
        var watts = limite is { SansLimite: false } l ? Docteur.T("TR.Watts", l.Pl1W.ToString("0", inv), l.Pl2W.ToString("0", inv)) : "";
        var avertissement = Docteur.T("TR.Avertissement");
        if (r.ArretSecurite)
            return new RepairFinding("CPU", "test de refroidissement", Docteur.T("TR.ArreteA", r.Secondes), Docteur.T("TR.TenuBout"), RepairStatus.Suspicious,
                Docteur.T("TR.Seuil", SeuilSecuriteC, r.SecondesAvantSeuil, watts) + avertissement,
                RepairAction.Recommandation);
        var chaleur = Math.Round(100 * f.Part(f.Chaleur)).ToString("0", inv);
        return f.Part(f.Chaleur) >= 0.05
            ? new RepairFinding("CPU", "test de refroidissement", Docteur.T("TR.Freine", chaleur), Docteur.T("TR.Sous5"), RepairStatus.Suspicious,
                Docteur.T("TR.FreineExplication", r.Secondes, watts, chaleur, f.ChaudMoyenC.ToString("0", inv))
                + avertissement, RepairAction.Recommandation)
            : new RepairFinding("CPU", "test de refroidissement", Docteur.T("TR.Tenu", r.Secondes), null, RepairStatus.Observed,
                Docteur.T("TR.TenuExplication", r.Secondes, watts, f.ChaudMaxC));
    }
}
