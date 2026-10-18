namespace DrDxt.Core;

/// <summary>Les limites de puissance du processeur Intel telles que le registre MSR_PKG_POWER_LIMIT (0x610) les dit, avec
/// la puissance de base publiée par le processeur (MSR_PKG_POWER_INFO 0x614). <paramref name="Pl1Active"/> = bit 15, dit
/// seulement ; <paramref name="RegistreAuMaximum"/> = champ PL1 brut à 0x7FFF, la façon des cartes mères de dire « sans
/// limite » (seule la valeur brute décide, jamais le bit 15, dont la fiabilité n'est pas vérifiée,
/// et jamais une conversion en watts qui dépend de l'unité).</summary>
public sealed record LimiteDePuissanceLue(double Pl1W, double Pl2W, double NominaleW, bool Verrou, bool Pl1Active = true,
    bool RegistreAuMaximum = false,
    // La copie MMIO de la carte mère (null = non lue, 0 = inactive, sinon la PL1 qu'elle tient) et la
    // puissance maximale que le processeur publie (0 = absente). La limite effective est la plus basse de MSR et MMIO.
    double? CopieMmioPl1W = null, double MaximaleW = 0,
    // La température la plus haute relevée sur les dernières parties (null = aucune) ; un processeur
    // qui touche déjà sa limite ne se voit pas monter sa PL1.
    double? ChaleurEnPartieMaxC = null,
    // Vrai = Core 13e/14e de bureau au microcode d'avant le correctif Vmin d'Intel (ou révision non lue). Porté
    // par la lecture de la limite, que le levier refait avant chaque pose : la garde tient aussi pour un essai déjà lancé.
    bool MicrocodeVminPerime = false,
    // Le plafond de la limite continue que la dernière partie relevée demande (freinée par la chaleur : sa
    // médiane plus un tiers) ; null = rien à plafonner. Le levier `fixit.cpu_power_cap` le pose, la montée s'abstient.
    double? PlafondThermiqueW = null,
    // Champ PL2 brut à 0x7FFF : la PL2 de la carte dit « sans limite » (4095 W), ce n'est pas un plafond.
    bool Pl2SansLimite = false)
{
    public bool SansLimite => RegistreAuMaximum;
    public bool SeFreineEnPartie => ChaleurEnPartieMaxC is { } c && c >= 95;
}

/// <summary>
/// Un processeur tenu sous sa puissance de base par la carte mère tient sa fréquence quelques secondes
/// puis redescend ; c'est un goulot que les compteurs Windows ne voient pas (RAPL « Performance Limit Flags » à 0 pendant
/// que la fréquence tombe, mesuré). La PL1 du registre MSR ne se lit que par PawnIO, s'il est installé ; la carte mère
/// peut tenir plus bas encore par sa copie MMIO (lue depuis B1, prise en compte par le levier).
/// Optimiser sait monter la PL1 à chaud jusqu'à la PL2 que la carte autorise (PawnIO, jugé en jeu,
/// rendu sans gain) ; au BIOS reste ce que DrDXT n'écrit pas : la PL2, un verrou. Sur un Intel non-K, PL1 = puissance de
/// base est la valeur d'Intel ; sur un K, Intel documente aussi un profil Performance plus haut : un choix, pas une réparation.
/// </summary>
public static class LimiteDePuissance
{
    public const string Nom = "limite de puissance";

    /// <summary>La PL1 à poser pour la montée, en watts, ou null quand il n'y a pas de marge sûre : le plafond est la PL2 de
    /// la carte, bornée par la puissance maximale publiée (quand elle l'est) et par la copie MMIO active (quand elle l'est) ;
    /// il faut qu'il dépasse la PL1 lue d'au moins un demi-watt, sans verrou ni registre « sans limite ». Jamais sur un Core
    /// 13e/14e au microcode d'avant le correctif Vmin (guide 5.3), ni pendant que la chaleur parle (95 °C en partie, ou un
    /// plafond thermique à poser), ni tant qu'aucune température de partie n'est relevée : « inconnu » n'est pas « pas
    /// chaud ». Une seule source pour le levier et pour ce que le docteur promet.</summary>
    public static double? CibleDeMontee(LimiteDePuissanceLue lu)
    {
        if (lu.Verrou || lu.SansLimite || lu.Pl2W <= 0 || lu.ChaleurEnPartieMaxC is null || lu.SeFreineEnPartie
            || lu.MicrocodeVminPerime || lu.PlafondThermiqueW is not null)
            return null;
        // Une PL2 « sans limite » sans puissance maximale publiée ne borne rien : le helper refuse alors toute montée
        // (PawnIoCpuPowerActions), le levier ne la propose donc pas.
        if (lu.Pl2SansLimite && lu.MaximaleW <= 0) return null;
        var plafond = lu.Pl2W;
        if (lu.MaximaleW > 0) plafond = Math.Min(plafond, lu.MaximaleW);
        if (lu.CopieMmioPl1W is > 0 and var mmio) plafond = Math.Min(plafond, mmio);
        return plafond > lu.Pl1W + 0.5 ? plafond : null;
    }

    public static RepairFinding Juger(LimiteDePuissanceLue? lu)
    {
        if (lu is null)
            return new RepairFinding("CPU", Nom, null, null, RepairStatus.Unknown,
                Docteur.T("LP.NonLue"));

        var attendue = Docteur.T("LP.Attendue", W(lu.NominaleW));
        if (lu.SansLimite)
            return new RepairFinding("CPU", Nom, Docteur.T("LP.SansLimite") + (lu.Pl1Active ? "" : Docteur.T("LP.BitZero")), attendue,
                RepairStatus.Observed,
                Docteur.T("LP.SansLimiteExplication"));

        var courante = Docteur.T("LP.Courante", W(lu.Pl1W), W(lu.Pl2W)) + (lu.Verrou ? Docteur.T("LP.Verrouillee") : "");
        if (lu.Pl1W + 0.5 < lu.NominaleW)
            return new RepairFinding("CPU", Nom, courante, attendue, RepairStatus.Suspicious,
                Docteur.T("LP.Sous", W(lu.Pl1W), W(lu.NominaleW))
                + (lu.Verrou
                    ? Docteur.T("LP.Verrou")
                    : CibleDeMontee(lu) is { } cible
                        ? Docteur.T("LP.Monter", W(cible))
                        : Docteur.T("LP.Bios")),
                RepairAction.Recommandation);
        if (Math.Abs(lu.Pl1W - lu.NominaleW) < 0.5)
            return new RepairFinding("CPU", Nom, courante, attendue, RepairStatus.Observed,
                Docteur.T("LP.ABase", W(lu.NominaleW))
                // Un non-K (65 W de base) n'a pas de profil Intel plus haut ; le lui taire faisait
                // passer la montée pour un profil documenté.
                + (lu.NominaleW <= 65.5
                    ? Docteur.T("LP.HorsSpec")
                    : Docteur.T("LP.ProfilPerf"))
                + (CibleDeMontee(lu) is { } essai
                    ? Docteur.T("LP.Essayer", W(essai))
                    : ""));
        return new RepairFinding("CPU", Nom, courante, attendue, RepairStatus.Observed,
            Docteur.T("LP.AuDessus", W(lu.NominaleW)));
    }

    private static string W(double w) => w.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " W";
}
