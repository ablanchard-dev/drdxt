using DrDxt.Core;

namespace DrDxt.Measurement.Cpu;

/// <summary>Le fichier reel sous <see cref="CpuBaselineStore"/>. Il ne porte aucune logique :
/// l'ecriture atomique vit dans <see cref="FichierJsonAtomique"/>, partagee avec la reference
/// memoire qui en avait besoin a l'identique.</summary>
public sealed class FileSystemCpuBaselineFile : ICpuBaselineFile
{
    private readonly FichierJsonAtomique _fichier;

    public FileSystemCpuBaselineFile() : this(FichierJsonAtomique.CheminLocal("cpu-baseline.json")) { }

    public FileSystemCpuBaselineFile(string chemin)
        => _fichier = new FichierJsonAtomique(chemin, ErrorCodes.IoFailure);

    public Result WriteAtomic(string json) => _fichier.WriteAtomic(json);

    public Result<string?> Read() => _fichier.Read();
}

/// <summary>
/// Le banc reel : la meme charge, deux fois, et c'est la duree qui change.
///
/// D'abord en rafale : quelques secondes, la machine ne quitte pas son turbo. Puis en soutenu,
/// apres une chauffe continue qui franchit la marche observee vers la quinzieme seconde. Le meme
/// nombre de cœurs dans les deux cas : le deficit ne veut rien dire a nombre different.
///
/// La liste des processeurs vient de l'appelant. Ce projet ne lit pas la topologie (c'est
/// `DrDxt.Detection` qui la publie), et la deviner par une formule epinglerait le banc sur les
/// mauvais cœurs.
/// </summary>
public sealed class RealCpuSustainedBench : ICpuSustainedBench
{
    /// <summary>Chauffe avant la mesure soutenue. 35 s : la marche a ete observee vers la 15e, on
    /// la franchit largement.</summary>
    private const double ChauffeSecondes = 35;

    private const int Passes = 4;

    /// <summary>Premier releve de puissance, en secondes depuis le debut de la charge soutenue.
    /// Non chiffré au-dela de ceci : la marche est observee vers la 15e seconde. Si la puissance
    /// descend encore apres, l'ecart des releves grandit et le juge ne conclut pas : l'erreur possible
    /// est un silence, pas une affirmation.</summary>
    private const double DebutRelevesSecondes = 20;

    private readonly Func<IReadOnlyList<int>> _processeursRapides;
    private readonly Func<double?>? _puissancePaquetW;
    private readonly Func<IReadOnlyList<int>, double?>? _frequenceMhz;

    /// <param name="puissancePaquetW">La puissance du paquet en watts, lue pendant la
    /// mesure soutenue. <c>null</c> = pas de relevé (le banc reste le meme).</param>
    /// <param name="frequenceMhz">La fréquence effective moyenne des processeurs logiques donnés.
    /// <c>null</c> = pas de relevé.</param>
    public RealCpuSustainedBench(Func<IReadOnlyList<int>> processeursRapides, Func<double?>? puissancePaquetW = null,
        Func<IReadOnlyList<int>, double?>? frequenceMhz = null)
    {
        _processeursRapides = processeursRapides;
        _puissancePaquetW = puissancePaquetW;
        _frequenceMhz = frequenceMhz;
    }

    /// <summary>Lance <paramref name="charge"/> et relève la fréquence des <paramref name="processeurs"/> pendant
    /// qu'elle tourne, à partir de <paramref name="apresSecondes"/> ; la médiane des relevés, <c>null</c> sans relevé.</summary>
    private T PendantQue<T>(Func<T> charge, IReadOnlyList<int> processeurs, double apresSecondes, out double? frequence)
    {
        if (_frequenceMhz is null) { frequence = null; return charge(); }
        var releves = new List<double>();
        using var fin = new CancellationTokenSource();
        var releveur = Task.Run(() =>
        {
            var debut = DateTime.UtcNow;
            while (!fin.IsCancellationRequested)
            {
                // Chaque lecture attend elle-même ~300 ms entre ses deux échantillons.
                if ((DateTime.UtcNow - debut).TotalSeconds < apresSecondes) { fin.Token.WaitHandle.WaitOne(250); continue; }
                try { if (_frequenceMhz(processeurs) is { } f && f > 0) lock (releves) releves.Add(f); }
                catch { /* un relevé perdu n'invalide pas le banc */ }
            }
        });
        var resultat = charge();
        fin.Cancel();
        releveur.Wait();
        lock (releves) frequence = releves.Count == 0 ? null : releves.Order().ElementAt(releves.Count / 2);
        return resultat;
    }

    /// <summary>Moyenne et incertitude relative de cette moyenne (en %), ou <c>null</c> sous trois relevés.
    ///
    /// Pas (max - min) : un releve instantane d'une seconde est bruite. Mesure du 25/09/2026, banc
    /// reel : 13 releves de 56,3 a 73,7 W autour de 63,4 W, soit 27 % d'etendue pour 7 % d'ecart-type.
    /// L'etendue aurait fait taire le produit sur une puissance nettement tenue.</summary>
    internal static (double MoyenneW, double EcartPercent)? Resumer(IReadOnlyList<double> releves)
    {
        if (releves.Count < 3) return null;
        var moyenne = releves.Average();
        if (moyenne <= 0) return null;
        var ecartType = Math.Sqrt(releves.Average(r => (r - moyenne) * (r - moyenne)));
        // L'incertitude de la moyenne, pas la dispersion des relevés. Intel : le PL1 limite une
        // puissance moyennée (« Shown limit is a time averaged power », moyenne glissante sur Tau, datasheet
        // Core 10e génération vol. 1, p. 53 et 56) ; sous un PL1 tenu, les relevés d'une seconde varient.
        // Juger leur dispersion faisait basculer le verdict 2 fois sur 4 sur la machine de reference (7 à 12,8 % d'écart-type
        // pour une moyenne tenue entre 63,2 et 65,4 W). Limite : des relevés consécutifs sont corrélés,
        // cette incertitude est donc optimiste.
        return (moyenne, ecartType / Math.Sqrt(releves.Count) / moyenne * 100);
    }

    public CpuBaseline? Mesurer(string machineId)
    {
        var processeurs = _processeursRapides();
        if (processeurs is null || processeurs.Count == 0) return null;

        // Types ecrits plutot que `var` : ce que la reference CPU lit ici s'appelle
        // `CpuBenchResult`, et le cliquet « construit mais jamais appele » compte par nom. Avec deux
        // `var`, `CpuBenchResult` passerait pour orphelin alors que les quatre lignes suivantes le
        // lisent : le faux positif qu'annonce l'en-tete de ce test. Le nommer dit la verite.
        CpuBenchResult rafale = CpuBench.Measure(processeurs.Count, Passes, ChargeCpu.Lourde, 0, processeurs);
        // Un seul cœur chargé, la limite de puissance du paquet ne le retient pas ; c'est la fréquence
        // de référence. Relevé sur la machine le 03/10 : avec 2 s de chauffe, la charge finissait avant que la première
        // lecture (près d'une seconde) n'aboutisse, et le « cœur seul » sortait à 3 792 MHz, sous les 4 270 MHz des huit.
        // Six secondes de chauffe : 5 292 MHz tenus de la 1re à la 7e seconde.
        IReadOnlyList<int> premier = [processeurs[0]];
        PendantQue(() => CpuBench.Measure(1, Passes, ChargeCpu.Lourde, 6, premier), premier, 1, out var frequenceUnCoeur);
        // La puissance se lit a cote, une fois par seconde, a partir de DebutRelevesSecondes. Pas
        // seulement apres la chauffe : la mesure qui la suit dure une a deux secondes (banc entier
        // chronometre a 37 s le 25/09/2026), ce qui ne laissait qu'un releve.
        var releves = new List<double>();
        using var fin = new CancellationTokenSource();
        var releveur = _puissancePaquetW is null ? Task.CompletedTask : Task.Run(async () =>
        {
            var debut = DateTime.UtcNow;
            while (!fin.IsCancellationRequested)
            {
                try { await Task.Delay(1000, fin.Token); } catch (OperationCanceledException) { break; }
                if ((DateTime.UtcNow - debut).TotalSeconds < DebutRelevesSecondes) continue;
                try { if (_puissancePaquetW() is { } w) lock (releves) releves.Add(w); }
                catch { /* un relevé perdu n'invalide pas le banc */ }
            }
        });
        CpuBenchResult soutenu = PendantQue(
            () => CpuBench.Measure(processeurs.Count, Passes, ChargeCpu.Lourde, ChauffeSecondes, processeurs),
            processeurs, DebutRelevesSecondes, out var frequenceSoutenue);
        fin.Cancel();
        releveur.Wait();
        (double MoyenneW, double EcartPercent)? puissance;
        lock (releves) puissance = Resumer(releves);

        // Un epinglage incomplet rend la comparaison sans objet : les cœurs charges ne sont pas
        // ceux qu'on croit. Meme refus que dans le juge de budget : on ne rend pas un chiffre.
        if (rafale.EpinglesReussis != rafale.Threads || soutenu.EpinglesReussis != soutenu.Threads)
            return null;

        // La somme de controle doit etre la meme : sinon les deux mesures n'ont pas fait le meme
        // travail, et leur rapport ne mesure que cette difference.
        if (rafale.Somme != soutenu.Somme) return null;

        return new CpuBaseline(machineId, DateTimeOffset.UtcNow, processeurs.Count,
            rafale.MopsParThread, soutenu.MopsParThread,
            Math.Max(rafale.EcartTypePourcent, soutenu.EcartTypePourcent),
            puissance?.MoyenneW, puissance?.EcartPercent,
            FrequenceUnCoeurMhz: frequenceUnCoeur, FrequenceSoutenueMhz: frequenceSoutenue);
    }
}
