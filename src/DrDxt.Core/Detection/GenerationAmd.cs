namespace DrDxt.Core;

/// <summary>
/// Le portail des lectures propres à AMD : la génération Zen tirée de CPUID, pour n'ouvrir une lecture que sur une puce
/// dont AMD a publié la référence. Une famille et un modèle absents de la table rendent « non reconnue », et tout ce qui
/// en dépend reste fermé. Lecture seule, pur ; la lecture de CPUID vit dans DrDxt.Detection.
/// </summary>
public static class GenerationAmd
{
    /// <param name="ModeleMin">Premier modèle couvert par le document (inclus).</param>
    /// <param name="ModeleMax">Dernier modèle couvert (inclus) ; égal à <paramref name="ModeleMin"/> quand le document
    /// n'en nomme qu'un.</param>
    /// <param name="Document">La référence AMD qui nomme ce couple famille et modèle (numéro de publication).</param>
    public sealed record Ligne(int Famille, int ModeleMin, int ModeleMax, string Generation, string Document);

    /// <summary>
    /// Une ligne par couple que nomme un document AMD publié (titres relevés le 16/10/2026 dans la liste des sources
    /// AMD de illumos usr/src/data/amdpmc/README, et pour 1Ah modèle 44h dans l'annonce de la publication 57896,
    /// CPUID B40F40). Renoir : « PPR for AMD Family 17h Model 60h, Revision A1 », publication 55922 (liste des publications
    /// AMD de WikiChip, CPUID 860F01). Strix Point : « PPR for AMD Family 1Ah Model 24h, Revision B0 », publiée sous le
    /// numéro 57254 (CPUID B20F40), que NetBSD cite sous 57274 ; le titre nomme le modèle. Seuls les modèles nommés sont
    /// reconnus : un modèle voisin, même de la même famille, est une autre puce. Ajouter une ligne demande son document.
    /// Cherchés le 25/10/2026 sans document AMD qui les nomme : Cezanne (19h 50h ; un PPR existe pour le modèle 51h,
    /// sans copie AMD trouvée), Rembrandt (19h 44h), les Phoenix qui se déclarent 74h, 75h ou 78h dont la ROG Ally (la
    /// publication 57019 ne nomme que le modèle 70h), Krackan (1Ah 60h) et Mendocino (17h A0h) : ils restent non reconnus.
    /// </summary>
    public static readonly IReadOnlyList<Ligne> Table =
    [
        new(0x17, 0x00, 0x2F, "Zen / Zen+", "AMD 56255, OSRR Family 17h Models 00h-2Fh"),
        new(0x17, 0x31, 0x31, "Zen 2", "AMD 55803, PPR Family 17h Model 31h B0"),
        new(0x17, 0x60, 0x60, "Zen 2", "AMD 55922, PPR Family 17h Model 60h A1"),
        new(0x17, 0x71, 0x71, "Zen 2", "AMD 56176, PPR Family 17h Model 71h B0"),
        new(0x19, 0x01, 0x01, "Zen 3", "AMD 55898, PPR Family 19h Model 01h B1"),
        new(0x19, 0x21, 0x21, "Zen 3", "AMD 56214, PPR Family 19h Model 21h B0"),
        new(0x19, 0x11, 0x11, "Zen 4", "AMD 55901, PPR Family 19h Model 11h B1"),
        new(0x19, 0x61, 0x61, "Zen 4", "AMD 56713, PPR Family 19h Model 61h B1"),
        new(0x19, 0x70, 0x70, "Zen 4", "AMD 57019, PPR Family 19h Model 70h A0"),
        new(0x1A, 0x24, 0x24, "Zen 5", "AMD 57254, PPR Family 1Ah Model 24h B0"),
        new(0x1A, 0x44, 0x44, "Zen 5", "AMD 57896, PPR Family 1Ah Model 44h B0"),
    ];

    /// <summary>
    /// Pur : famille et modèle affichés à partir de CPUID 1 EAX, règle d'AMD (CPUID Specification, publication 25481,
    /// Fn0000_0001_EAX) : la famille étendue (bits 27:20) ne s'ajoute, et le modèle étendu (bits 19:16) ne se place devant
    /// le modèle de base, que si la famille de base (bits 11:8) vaut 0xF. Sinon la famille est celle de base.
    /// </summary>
    public static (int Famille, int Modele) Decoder(uint eax)
    {
        var baseFamille = (int)(eax >> 8) & 0xF;
        var baseModele = (int)(eax >> 4) & 0xF;
        if (baseFamille != 0xF) return (baseFamille, baseModele);
        return (baseFamille + ((int)(eax >> 20) & 0xFF), (((int)(eax >> 16) & 0xF) << 4) | baseModele);
    }

    /// <summary>Pur : la ligne de la table, ou null (génération non reconnue, ou pas un processeur AMD).</summary>
    public static Ligne? Reconnaitre(string? fabricant, uint eax)
    {
        if (fabricant != "AuthenticAMD") return null;
        var (famille, modele) = Decoder(eax);
        return Table.FirstOrDefault(l => l.Famille == famille && modele >= l.ModeleMin && modele <= l.ModeleMax);
    }

    /// <summary>Pur : le constat du docteur. Rien sur un processeur qui n'est pas AMD ; « non reconnue » quand CPUID
    /// n'a pas été lu ou que le couple manque à la table.</summary>
    public static IEnumerable<RepairFinding> Juger(string? fabricant, uint? eax)
    {
        if (fabricant is not null && fabricant != "AuthenticAMD") yield break;
        if (fabricant is null || eax is not { } e)
        {
            yield return new RepairFinding("CPU", "generation AMD", null, null, RepairStatus.Unknown, Docteur.T("GA.NonLue"));
            yield break;
        }
        var (famille, modele) = Decoder(e);
        var valeur = $"{famille:X2}h / {modele:X2}h";
        yield return Reconnaitre(fabricant, e) is { } l
            ? new RepairFinding("CPU", "generation AMD", $"{l.Generation} ({valeur})", null, RepairStatus.Observed,
                Docteur.T("GA.Reconnue", l.Generation, valeur, l.Document))
            : new RepairFinding("CPU", "generation AMD", valeur, null, RepairStatus.Unknown, Docteur.T("GA.NonReconnue", valeur));
    }
}
