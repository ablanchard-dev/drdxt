using System;
using System.Collections.Generic;
using System.Globalization;
using DrDxt.Measurement.Memory;

namespace DrDxt.App.ViewModels;

/// <summary>
/// La moitie memoire de la section « ton passage dans le BIOS » : ce que la machine mesurait
/// Avant, ce qu'elle mesure apres, et si l'ecart est un resultat.
///
/// ══ ce que cette section n'a pas le droit de faire ══
/// Dire « leger gain ». Un ecart plus petit que 3 sigma du bruit de l'instrument n'est pas un
/// petit resultat, c'est une absence de resultat -- et c'est la phrase que tous les optimiseurs
/// du marche se permettent. La conclusion n'a donc que trois etats : c'est un resultat, ce n'en
/// est pas un, ou on ne se prononce pas (mesure refusee).
///
/// Chaque ligne porte les deux valeurs brutes a cote du pourcentage : « +79 % » seul demande
/// d'etre cru sur parole, « 19,1 -> 34,2 Go/s, +79 % » se recalcule a la main.
/// </summary>
public sealed class MemoryAbViewModel
{
    private MemoryAbViewModel(string header, IReadOnlyList<MemoryAbLineViewModel> lines, string conclusion)
    {
        Header = header;
        Lines = lines;
        Conclusion = conclusion;
    }

    /// <summary>Rend <c>null</c> quand il n'y a rien a comparer : pas de reference, pas de
    /// voyage BIOS. Une section vide ne doit pas s'afficher.</summary>
    public static MemoryAbViewModel? From(MemoryAbVerdict? verdict, Func<string, string> localize)
    {
        if (verdict is null) return null;

        var header = localize(MemoryAbStringKeys.Header);

        // Mesure refusee : Aucun chiffre. Un nombre affiche a cote d'un refus serait lu comme
        // un resultat malgre le refus.
        //
        // Et deux phrases, pas une. « Relance la machine au repos » est actionnable quand la
        // machine etait occupee (Q-03). Sur un banc qui n'a pas pu tourner ou une faute
        // d'instrument, c'est une fausse piste : le client fermerait des programmes pour un
        // probleme qui n'a rien a voir. Un conseil faux coute plus cher que pas de conseil.
        if (!verdict.HasVerdict)
        {
            var motif = verdict.NoVerdictReason ?? string.Empty;
            var cle = motif switch
            {
                // La machine etait occupee : elle peut se calmer, on dit comment.
                _ when motif.StartsWith("Q-03", StringComparison.Ordinal)
                    => MemoryAbStringKeys.NoVerdictBusy,

                // Le banc n'a pas pu demarrer, et c'est le plus souvent un manque de memoire
                // libre : il en demande 384 Mo d'un coup. Le ranger avec les fautes d'instrument
                // lui collerait « ce n'est pas une question de programmes ouverts » -- alors que
                // fermer des programmes est exactement ce qui repare ce cas.
                _ when motif.StartsWith(
                    DrDxt.Measurement.Memory.MemoryBaselineService.BenchUnavailable,
                    StringComparison.Ordinal)
                    => MemoryAbStringKeys.NoVerdictNoRoom,

                // La mesure d'avant ne date pas du depart vers le BIOS : rien a voir avec l'instrument.
                _ when motif.StartsWith(
                    DrDxt.Measurement.Memory.MemoryBaselineService.AvantPerime,
                    StringComparison.Ordinal)
                    => MemoryAbStringKeys.NoVerdictStale,

                // Un jeu tournait pendant la mesure d'après : ni l'instrument ni la machine en cause.
                _ when motif.StartsWith(
                    DrDxt.Measurement.Memory.MemoryBaselineService.JeuLance,
                    StringComparison.Ordinal)
                    => MemoryAbStringKeys.NoVerdictGame,

                // Q-01, Q-02, Q-04 : l'instrument mesure la mauvaise chose. La, et la seulement,
                // on a le droit d'ecarter les programmes ouverts.
                _ => MemoryAbStringKeys.NoVerdictFailed,
            };

            var raison = string.Format(CultureInfo.CurrentCulture, localize(cle), motif);
            return new MemoryAbViewModel(header, Array.Empty<MemoryAbLineViewModel>(), raison);
        }

        var lines = new List<MemoryAbLineViewModel>
        {
            Line(localize(MemoryAbStringKeys.Bandwidth), verdict.BandwidthBeforeGBs,
                 verdict.BandwidthAfterGBs, "N1", localize(MemoryAbStringKeys.UnitGBs),
                 verdict.BandwidthDeltaPct, verdict.BandwidthSignificant),
            Line(localize(MemoryAbStringKeys.Latency), verdict.LatencyBeforeNs,
                 verdict.LatencyAfterNs, "N1", localize(MemoryAbStringKeys.UnitNs),
                 verdict.LatencyDeltaPct, verdict.LatencySignificant),
        };

        if (verdict.BandwidthSignificant || verdict.LatencySignificant)
            return new MemoryAbViewModel(
                header, lines, localize(MemoryAbStringKeys.Significant));

        // « Aucune difference mesurable » sans son seuil se lit « ca ne sert a rien ». La phrase
        // porte donc la RESOLUTION : le plus petit ecart que ces deux mesures pouvaient
        // distinguer. Mesure du 17/09/2026 sur la machine de reference : 7,8 % sur le debit,
        // quand passer de 3 800 a 4 000 MT/s n'en promet que 5,3 -- le banc ne peut pas trancher
        // cette question, et c'est ce chiffre qui le dit. Le GPU parle deja ainsi
        // (Scan.Drift.None). On annonce la plus FINE des deux resolutions : c'est celle qui
        // decrit ce que l'instrument savait voir de mieux.
        var resolution = Math.Min(
            verdict.BandwidthResolutionPct, verdict.LatencyResolutionPct);

        return new MemoryAbViewModel(
            header, lines,
            string.Format(
                CultureInfo.CurrentCulture,
                localize(MemoryAbStringKeys.NotSignificant),
                resolution));
    }

    private static MemoryAbLineViewModel Line(
        string label, double before, double after, string format, string unit,
        double deltaPct, bool significant)
        => new(
            label,
            $"{before.ToString(format, CultureInfo.CurrentCulture)} {unit}",
            $"{after.ToString(format, CultureInfo.CurrentCulture)} {unit}",
            deltaPct.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + " %",
            significant);

    public string Header { get; }

    /// <summary>Une ligne par grandeur mesuree. Vide quand la mesure a ete refusee.</summary>
    public IReadOnlyList<MemoryAbLineViewModel> Lines { get; }

    public string Conclusion { get; }

    public bool HasLines => Lines.Count > 0;
}

/// <summary>Une grandeur : son nom, l'avant, l'apres, l'ecart, et s'il compte.</summary>
public sealed class MemoryAbLineViewModel
{
    public MemoryAbLineViewModel(string label, string before, string after, string delta, bool isSignificant)
    {
        Label = label;
        Before = before;
        After = after;
        Delta = delta;
        IsSignificant = isSignificant;
    }

    public string Label { get; }
    public string Before { get; }
    public string After { get; }
    public string Delta { get; }

    /// <summary>Vrai quand l'ecart depasse 3 sigma. L'ecran ne met en avant que ceux-la.</summary>
    public bool IsSignificant { get; }
}

/// <summary>Les cles de la section. Toutes en resx FR+EN, jamais une chaine en dur.</summary>
public static class MemoryAbStringKeys
{
    public const string Header = "Memory.Ab.Header";
    public const string Bandwidth = "Memory.Ab.Bandwidth";
    public const string Latency = "Memory.Ab.Latency";
    public const string UnitGBs = "Memory.Ab.Unit.GBs";
    public const string UnitNs = "Memory.Ab.Unit.Ns";
    public const string Significant = "Memory.Ab.Significant";
    public const string NotSignificant = "Memory.Ab.NotSignificant";
    /// <summary>La machine etait occupee (Q-03) : le client peut agir, on lui dit comment.</summary>
    public const string NoVerdictBusy = "Memory.Ab.NoVerdict.Busy";

    /// <summary>Le banc n'a pas pu demarrer : il demande 384 Mo libres d'un coup. Ici, fermer
    /// des programmes est le geste qui repare -- l'inverse de la phrase suivante.</summary>
    public const string NoVerdictNoRoom = "Memory.Ab.NoVerdict.NoRoom";

    /// <summary>Faute d'INSTRUMENT (Q-01, Q-02, Q-04) : la seule ou l'on peut ecarter les
    /// programmes ouverts, parce que l'instrument mesure la mauvaise chose.</summary>
    public const string NoVerdictFailed = "Memory.Ab.NoVerdict.Failed";

    /// <summary>La mesure d'avant ne date pas du depart vers le BIOS : comparer a une machine d'un
    /// autre jour annoncerait un ecart que le changement n'a pas produit.</summary>
    public const string NoVerdictStale = "Memory.Ab.NoVerdict.Stale";

    /// <summary>Un jeu tournait pendant la mesure d'après : l'écart lu aurait été le sien.</summary>
    public const string NoVerdictGame = "Memory.Ab.NoVerdict.Game";
}
