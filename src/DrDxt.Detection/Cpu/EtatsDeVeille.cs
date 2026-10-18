using System.Globalization;
using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Detection.Cpu;

/// <summary>
/// La part du temps que le processeur passe dans chaque état de veille (C1, C2, C3), relevée au scan et comparée au
/// scan précédent. C'est un signal de stabilité, pas un levier : une baisse de tension qui manque de marge plante
/// typiquement au repos, quand les cœurs entrent et sortent de veille. Le constat ne conseille jamais de couper les
/// C-states ; ce geste est refusé par la barrière anti-mythe. Lecture seule ; le relevé gardé est écrit par l'App.
/// </summary>
public static class EtatsDeVeille
{
    /// <param name="Repos">Part du temps où le processeur ne travaillait pas, de 0 à 1.</param>
    /// <param name="Transitions">Entrées en C1, C2 et C3 par seconde, tous cœurs.</param>
    public sealed record Releve(DateTimeOffset Quand, double C1, double C2, double C3, double Repos, double Transitions);

    public const string Fichier = "etats-de-veille.json";

    /// <summary>
    /// Écart retenu comme un saut entre deux scans : 10 points de C3, ou 25 points de C2 + C3. **Non chiffré** : c'est
    /// un choix, pris large pour ne pas signaler la variation d'une machine qui travaille un peu plus à un scan qu'à
    /// l'autre. Les deux relevés doivent avoir été pris au repos (<see cref="ReposMinimum"/>).
    /// </summary>
    public const double SautC3 = 0.10, SautProfond = 0.25;

    /// <summary>Un relevé pris pendant que la machine travaille ne dit rien de son repos. **Non chiffré.**</summary>
    public const double ReposMinimum = 0.80;

    /// <summary>Deux relevés bruts de « Processor Information(_Total) » espacés de <paramref name="ecart"/>.
    /// <c>null</c> = non lu.</summary>
    public static Releve? Mesurer(TimeSpan ecart)
    {
        try
        {
            if (Brut() is not { } a) return null;
            Thread.Sleep(ecart);
            return Brut() is { } b ? Calculer(a, b, DateTimeOffset.Now) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Les compteurs bruts, sans traduction de leur nom : la classe WMI garde ses noms anglais quelle que soit
    /// la langue de Windows.</summary>
    internal sealed record Compteurs(ulong C1, ulong C2, ulong C3, ulong Repos, ulong Sys100Ns, ulong T1, ulong T2, ulong T3,
        ulong PerfTime, ulong PerfFreq);

    private static Compteurs? Brut()
    {
        using var s = new System.Management.ManagementObjectSearcher(
            "SELECT PercentC1Time, PercentC2Time, PercentC3Time, PercentIdleTime, Timestamp_Sys100NS, C1TransitionsPersec, "
            + "C2TransitionsPersec, C3TransitionsPersec, Timestamp_PerfTime, Frequency_PerfTime "
            + "FROM Win32_PerfRawData_Counters_ProcessorInformation WHERE Name = '_Total'");
        using var rangees = s.Get();
        foreach (var o in rangees.Cast<System.Management.ManagementObject>())
            using (o)
            {
                ulong U(string n) => Convert.ToUInt64(o[n], CultureInfo.InvariantCulture);
                return new(U("PercentC1Time"), U("PercentC2Time"), U("PercentC3Time"), U("PercentIdleTime"), U("Timestamp_Sys100NS"),
                    U("C1TransitionsPersec"), U("C2TransitionsPersec"), U("C3TransitionsPersec"), U("Timestamp_PerfTime"),
                    U("Frequency_PerfTime"));
            }
        return null;
    }

    /// <summary>
    /// Pur. Microsoft Learn, types de compteurs : « % Cx Time » est un PERF_100NSEC_TIMER (delta du compteur sur delta de
    /// Timestamp_Sys100NS) ; « Cx Transitions/sec » un PERF_COUNTER_COUNTER (delta sur delta de Timestamp_PerfTime divisé
    /// par Frequency_PerfTime). Sur _Total, la valeur est la moyenne des cœurs (mesuré le 16/10/2026 : 98,45 % de C2 sur
    /// deux secondes, contre 99 % rendus formatés par Windows au même moment).
    /// </summary>
    internal static Releve? Calculer(Compteurs a, Compteurs b, DateTimeOffset quand)
    {
        if (b.Sys100Ns <= a.Sys100Ns || b.PerfTime <= a.PerfTime || a.PerfFreq == 0) return null;
        if (b.C1 < a.C1 || b.C2 < a.C2 || b.C3 < a.C3 || b.Repos < a.Repos || b.T1 < a.T1 || b.T2 < a.T2 || b.T3 < a.T3) return null;
        double dt = b.Sys100Ns - a.Sys100Ns;
        double Part(ulong x, ulong y) => Math.Clamp((y - x) / dt, 0, 1);
        var secondes = (double)(b.PerfTime - a.PerfTime) / a.PerfFreq;
        return new Releve(quand, Part(a.C1, b.C1), Part(a.C2, b.C2), Part(a.C3, b.C3), Part(a.Repos, b.Repos),
            (b.T1 - a.T1 + (b.T2 - a.T2) + (b.T3 - a.T3)) / secondes);
    }

    /// <summary>Pur : le constat.</summary>
    public static IEnumerable<RepairFinding> Juger(Releve? actuel, Releve? precedent)
    {
        if (actuel is null)
        {
            yield return new RepairFinding("CPU", "etats de veille", null, null, RepairStatus.Unknown, Docteur.T("CS.NonLu"));
            yield break;
        }
        var valeur = Docteur.T("CS.Valeur", actuel.C1 * 100, actuel.C2 * 100, actuel.C3 * 100, actuel.Transitions);
        var phrase = actuel.Repos < ReposMinimum ? Docteur.T("CS.PasAuRepos", actuel.Repos * 100) : Docteur.T("CS.AuRepos");
        if (precedent is not null && actuel.Repos >= ReposMinimum && precedent.Repos >= ReposMinimum
            && (Math.Abs(actuel.C3 - precedent.C3) >= SautC3 || Math.Abs(actuel.C2 + actuel.C3 - precedent.C2 - precedent.C3) >= SautProfond))
            phrase += Docteur.T("CS.Saut", precedent.Quand.ToLocalTime().ToString(Docteur.T("ST.FormatDateHeure"), Docteur.Langue),
                precedent.C1 * 100, precedent.C2 * 100, precedent.C3 * 100);
        yield return new RepairFinding("CPU", "etats de veille", valeur, null, RepairStatus.Observed, phrase + Docteur.T("CS.Levier"));
    }

    /// <summary>Mesure, juge contre le relevé gardé, puis garde l'actuel s'il a été pris au repos : un scan pris sous
    /// charge ne remplace pas la référence de repos.</summary>
    public static IReadOnlyList<RepairFinding> ComparerEtGarder(string fichier)
    {
        var actuel = Mesurer(TimeSpan.FromSeconds(2));
        var constats = Juger(actuel, LirePrecedent(fichier)).ToList();
        if (actuel is { Repos: >= ReposMinimum })
            new FichierJsonAtomique(fichier, ErrorCodes.StorageFail).WriteAtomic(JsonSerializer.Serialize(actuel));
        return constats;
    }

    /// <summary>Le relevé gardé du scan précédent, ou null.</summary>
    public static Releve? LirePrecedent(string fichier)
    {
        try { return File.Exists(fichier) ? JsonSerializer.Deserialize<Releve>(File.ReadAllText(fichier)) : null; }
        catch (Exception) { return null; }
    }
}
