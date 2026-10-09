namespace DrDxt.Core;

/// <summary>Les limites de puissance du processeur Intel telles que le registre MSR_PKG_POWER_LIMIT (0x610) les dit, avec
/// la puissance de base publiée par le processeur (MSR_PKG_POWER_INFO 0x614). <paramref name="Pl1Active"/> = bit 15, dit
/// seulement ; <paramref name="RegistreAuMaximum"/> = champ PL1 brut à 0x7FFF, la façon des cartes mères de dire « sans
/// limite » (contre-revue du 09/10 : seule la valeur brute décide, jamais le bit 15, dont la fiabilité n'est pas vérifiée,
/// et jamais une conversion en watts qui dépend de l'unité).</summary>
public sealed record LimiteDePuissanceLue(double Pl1W, double Pl2W, double NominaleW, bool Verrou, bool Pl1Active = true,
    bool RegistreAuMaximum = false)
{
    public bool SansLimite => RegistreAuMaximum;
}

/// <summary>
/// J-MECA (09/10/2026) : un processeur tenu sous sa puissance de base par la carte mère tient sa fréquence quelques secondes
/// puis redescend ; c'est un goulot que les compteurs Windows ne voient pas (RAPL « Performance Limit Flags » à 0 pendant
/// que la fréquence tombe, mesuré). La PL1 du registre MSR ne se lit que par PawnIO, s'il est installé ; la carte mère
/// peut tenir plus bas encore par sa copie MMIO, que DrDXT ne lit pas : ce constat dit « du registre », pas « effective ».
/// Lecture seule : le réglage est au BIOS (guide 5.3), DrDXT ne l'écrit jamais. Sur un Intel non-K, PL1 = puissance de base
/// est la valeur d'Intel ; sur un K, Intel documente aussi un profil Performance plus haut : un choix, pas une réparation.
/// </summary>
public static class LimiteDePuissance
{
    public const string Nom = "limite de puissance";

    public static RepairFinding Juger(LimiteDePuissanceLue? lu)
    {
        if (lu is null)
            return new RepairFinding("CPU", Nom, null, null, RepairStatus.Unknown,
                "la limite de puissance du processeur n'a pas pu être lue (elle ne se lit que dans ses registres : PawnIO installé, "
                + "DrDXT lancé en administrateur, processeur Intel).");

        var attendue = $"au moins la puissance de base ({W(lu.NominaleW)})";
        if (lu.SansLimite)
            return new RepairFinding("CPU", Nom, "PL1 au maximum du registre (sans limite)" + (lu.Pl1Active ? "" : ", bit d'activation à zéro"), attendue,
                RepairStatus.Observed,
                "la carte mère ne tient pas le processeur en puissance : c'est son refroidissement qui décide. Surveille sa température ; "
                + "le guide 5.3 dit où remettre une limite.");

        var courante = $"PL1 {W(lu.Pl1W)}, PL2 {W(lu.Pl2W)}" + (lu.Verrou ? ", verrouillée" : "");
        if (lu.Pl1W + 0.5 < lu.NominaleW)
            return new RepairFinding("CPU", Nom, courante, attendue, RepairStatus.Suspicious,
                $"la carte mère tient le processeur à {W(lu.Pl1W)} en continu, sous sa puissance de base de {W(lu.NominaleW)} : "
                + "il tient sa fréquence quelques secondes puis redescend. Le réglage est au BIOS (limite de puissance, profil "
                + "constructeur) ; DrDXT ne l'écrit jamais." + (lu.Verrou ? " Le registre est verrouillé jusqu'au redémarrage : seul le BIOS le change." : ""),
                RepairAction.Recommandation);
        if (Math.Abs(lu.Pl1W - lu.NominaleW) < 0.5)
            return new RepairFinding("CPU", Nom, courante, attendue, RepairStatus.Observed,
                $"PL1 à la puissance de base ({W(lu.NominaleW)}) : le profil de base d'Intel pour ce processeur. Sur un K, Intel "
                + "documente aussi un profil Performance plus haut (guide 5.3) ; monter chauffe plus : un choix, pas une réparation.");
        return new RepairFinding("CPU", Nom, courante, attendue, RepairStatus.Observed,
            $"PL1 relevée par le BIOS au-dessus de la puissance de base ({W(lu.NominaleW)}) : le processeur n'est pas tenu en "
            + "puissance ; surveille sa température.");
    }

    private static string W(double w) => w.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " W";
}
