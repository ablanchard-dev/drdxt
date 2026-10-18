namespace DrDxt.Core;

/// <summary>
/// Les défauts que les constructeurs ont eux-mêmes reconnus et corrigés par un firmware : une table de données, une
/// ligne par bulletin, chacune avec son adresse et la date où DrDXT l'a relevée. Un disque dont le firmware précède le
/// correctif est signalé ; une famille de firmware que la ligne ne connaît pas n'est pas comparée, jamais supposée.
/// Le microcode des Core Intel de 13e et 14e génération a son propre garde (<see cref="MicrocodeVmin"/>).
/// </summary>
public static class DefautsConnus
{
    /// <param name="Modele">Ce que le nom du disque contient (« 990 PRO »).</param>
    /// <param name="Corrige">Le premier firmware qui porte le correctif ; ceux de la même famille (même longueur, même
    /// fin) qui le précèdent sont concernés.</param>
    /// <param name="Phrase">Clé du texte du docteur qui dit le défaut, avec les mots du bulletin.</param>
    public sealed record Ligne(string Fabricant, string Modele, string Corrige, string Phrase, string Source, DateOnly Releve);

    public static readonly IReadOnlyList<Ligne> Table =
    [
        // Page « Samsung Magician & SSD Tools & Software » : 990 PRO, 7B2QJXD7, septembre 2025, « To address the
        // intermittent non-recognition and blue screen issue ».
        new("Samsung", "990 PRO", "7B2QJXD7", "DC.Samsung990Pro",
            "https://semiconductor.samsung.com/consumer-storage/support/tools/", new DateOnly(2026, 10, 16)),
    ];

    /// <summary>Pur : vrai si le firmware précède le correctif, faux s'il le porte, null s'il n'est pas de la même famille.
    /// Les firmwares Samsung de ces disques ne changent que par leur premier caractère (révision) : « 4B2QJXD7 » précède
    /// « 7B2QJXD7 ».</summary>
    public static bool? Precede(string? firmware, string corrige)
    {
        if (firmware is null) return null;
        firmware = firmware.Trim();
        if (firmware.Length != corrige.Length || firmware.Length < 2 || !firmware[1..].Equals(corrige[1..], StringComparison.OrdinalIgnoreCase))
            return null;
        return char.ToUpperInvariant(firmware[0]).CompareTo(char.ToUpperInvariant(corrige[0])) < 0;
    }

    /// <summary>Pur : un constat par disque concerné par une ligne. <paramref name="disques"/> null = liste non lue.</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<(string Nom, string? Firmware)>? disques)
    {
        if (disques is null)
        {
            yield return new RepairFinding("STOCKAGE", "defauts connus des disques", null, null, RepairStatus.Unknown, Docteur.T("DC.NonLu"));
            yield break;
        }
        foreach (var (nom, firmware) in disques)
            foreach (var l in Table.Where(l => nom.Contains(l.Fabricant, StringComparison.OrdinalIgnoreCase)
                                               && nom.Contains(l.Modele, StringComparison.OrdinalIgnoreCase)))
            {
                var source = Docteur.T("DC.Source", l.Source, l.Releve.ToString(Docteur.T("Format.Date"), System.Globalization.CultureInfo.InvariantCulture));
                yield return Precede(firmware, l.Corrige) switch
                {
                    true => new RepairFinding("STOCKAGE", Docteur.Nom("DC.Nom", nom), firmware, Docteur.T("DC.OuPlusRecent", l.Corrige),
                        RepairStatus.Suspicious, Docteur.T(l.Phrase, firmware, l.Corrige) + source, RepairAction.Recommandation),
                    false => new RepairFinding("STOCKAGE", Docteur.Nom("DC.Nom", nom), firmware, Docteur.T("DC.OuPlusRecent", l.Corrige),
                        RepairStatus.ExpectedDefault, Docteur.T("DC.AJour", firmware, l.Corrige) + source),
                    null => new RepairFinding("STOCKAGE", Docteur.Nom("DC.Nom", nom), firmware, Docteur.T("DC.OuPlusRecent", l.Corrige),
                        RepairStatus.Unknown, Docteur.T("DC.NonCompare", firmware ?? "?", l.Corrige) + source),
                };
            }
    }
}
