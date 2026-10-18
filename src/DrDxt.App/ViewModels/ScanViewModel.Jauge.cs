using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DrDxt.App.ViewModels;

// La jauge du scan : ses étapes nommées, sa progression qui ne recule jamais, et les pôles déjà lus.
public sealed partial class ScanViewModel
{
    /// <summary>Les pôles du réseau déjà lus par CE scan, un bit par composant, dans l'ordre de
    /// <see cref="Components"/> (processeur, carte, mémoire, stockage, Windows), le même que celui des pôles.</summary>
    [ObservableProperty] private int _polesLus;

    /// <summary>L'analyse dure plus de 10 s ; un rond qui tourne sans fin fait croire à un plantage
    /// (NN/g, Progress Indicators). Elle avance par étapes nommées, dans l'ordre réel de <see cref="ScanCoeurAsync"/>.</summary>
    public const int EtapesDuScan = 8;

    /// <summary>La part de l'analyse déjà faite, de 0 à 1 ; jamais en recul pendant un scan.</summary>
    [ObservableProperty] private double _progressionDuScan;

    /// <summary>« Étape k sur 8 · nom » : ce qui tourne en ce moment, écrit sous le rond.</summary>
    [ObservableProperty] private string? _labelEtapeDuScan;

    /// <summary>La fin de l'étape en cours, un peu en deçà : la jauge peut y avancer seule pendant
    /// l'étape, jamais au-delà.</summary>
    [ObservableProperty] private double _plafondDuScan;

    /// <summary>« la jauge pas même synchro que le scanner ». Les huit étapes ne durent pas pareil
    /// (le réseau et le processeur prennent l'essentiel) ; chacune pèse son temps réel. Durées par défaut tirées d'une
    /// analyse réelle au repos de la machine de référence (03/10 : réseau et processeur 8,0 s, audit 3,2 s, réglages 2,7 s,
    /// le reste sous la seconde), remplacées par celles de la dernière analyse de cette machine.</summary>
    private static readonly double[] DureesParDefaut = [0.1, 2.7, 0.05, 8.0, 0.1, 0.6, 0.1, 3.2];
    private double[] _dureesAttendues = (double[])DureesParDefaut.Clone();
    private readonly double[] _dureesMesurees = new double[EtapesDuScan];
    private readonly System.Diagnostics.Stopwatch _chronoEtape = new();
    private int _etapeEnCours;
    private int _numeroDeScan;

    /// <summary>Lit les durées de la dernière analyse ; branché par l'appli, absent des tests (rien n'est écrit).</summary>
    internal Func<double[]?>? LireDureesDesEtapes { get; set; }
    internal Action<double[]>? EnregistrerDureesDesEtapes { get; set; }

    /// <summary>La part de la jauge qui avance par seconde pendant l'étape en cours.</summary>
    [ObservableProperty] private double _vitesseDuScan;

    /// <summary>La jauge reste à l'écran un instant après la fin, le temps qu'on la voie pleine.</summary>
    [ObservableProperty] private bool _jaugeVisible;

    /// <summary>Le début et la fin d'une étape sur la jauge, de 0 à 1, au prorata des durées.</summary>
    internal static (double Debut, double Fin) Bornes(IReadOnlyList<double> durees, int numero)
    {
        var total = durees.Sum();
        var avant = durees.Take(numero - 1).Sum();
        return (avant / total, (avant + durees[numero - 1]) / total);
    }

    internal void Etape(int numero, string cle)
    {
        if (_etapeEnCours > 0) _dureesMesurees[_etapeEnCours - 1] = _chronoEtape.Elapsed.TotalSeconds;
        if (numero == 1)
        {
            var lues = LireDureesDesEtapes?.Invoke();
            _dureesAttendues = lues is { Length: EtapesDuScan } && lues.All(d => d > 0 && double.IsFinite(d)) ? lues : (double[])DureesParDefaut.Clone();
            Array.Clear(_dureesMesurees);
            JaugeVisible = true;
        }
        _etapeEnCours = numero;
        _chronoEtape.Restart();
        var (debut, fin) = Bornes(_dureesAttendues, numero);
        ProgressionDuScan = debut;
        PlafondDuScan = debut + (fin - debut) * 0.95;
        VitesseDuScan = (fin - debut) / _dureesAttendues[numero - 1];
        LabelEtapeDuScan = string.Format(CultureInfo.CurrentCulture, _localize("Scan.Etape.Format"),
            numero, EtapesDuScan, _localize(cle));
    }

    /// <summary>Ce que durent les bancs, mesuré le 17/10/2026 sur la machine de référence : processeur 44 s, mémoire 56 à
    /// 79 s. Une étape sans banc dure une à quatre secondes.</summary>
    internal const double DureeBancProcesseur = 45, DureeBancMemoire = 60, DureeReseau = 4, DureeSansBanc = 0.5;

    /// <summary>Une étape qui sait ce qu'elle va faire (lancer un banc ou non) recale sa durée attendue : la jauge ne reste
    /// plus figée une minute sur un banc imprévu, ni ne saute une étape prévue longue. Ce qui est affiché ne recule pas
    /// (l'arc de progression garde son avance).</summary>
    internal void Prevoir(int numero, double secondes)
    {
        if (numero != _etapeEnCours || secondes <= 0) return;
        _dureesAttendues[numero - 1] = secondes;
        var (debut, fin) = Bornes(_dureesAttendues, numero);
        ProgressionDuScan = debut + (fin - debut) * Math.Min(0.95, _chronoEtape.Elapsed.TotalSeconds / secondes);
        PlafondDuScan = debut + (fin - debut) * 0.95;
        VitesseDuScan = (fin - debut) / secondes;
    }

    private void AllumerPole(params string[] cles)
    {
        var bits = PolesLus;
        // Le bit d'un pôle est sa place sur le cercle des huit satellites.
        foreach (var cle in cles)
            if (Array.IndexOf(OrdreDesSatellites, cle) is var i and >= 0) bits |= 1 << i;
        PolesLus = bits;
    }

    private async Task MasquerLaJaugeAsync(int numero)
    {
        await Task.Delay(1100).ConfigureAwait(true);
        if (numero == _numeroDeScan && !IsScanning) JaugeVisible = false;
    }
}
