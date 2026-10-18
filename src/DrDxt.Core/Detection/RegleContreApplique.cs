namespace DrDxt.Core;

/// <summary>Un timing réglé dans le BIOS qui ne tourne pas sur un canal : la valeur du menu et celle du contrôleur.</summary>
public sealed record EcartRegleApplique(string Reglage, string Demande, string Applique, char Canal);

/// <summary>
/// Le réglé contre l'appliqué sur un Ryzen : chaque timing que le BIOS nomme (<see cref="TablesBiosAmd"/>), « Auto » mis
/// à part, comparé à ce que le contrôleur applique (<see cref="TimingsMemoireAmd"/>) sur tous les canaux, et le mode de
/// secours de la mémoire : un profil EXPO ou XMP demandé alors que tous les canaux tournent au socle JEDEC (entraînement
/// raté). Pur, lecture seule.
/// </summary>
public static class RegleContreApplique
{
    private static readonly (string Nom, Func<TimingsCanal, string> Applique)[] Timings =
    [
        ("tCL", c => c.Cl.ToString()), ("tRCD", c => c.Rcd.ToString()), ("tRP", c => c.Rp.ToString()),
        ("tRAS", c => c.Ras.ToString()), ("tRC", c => c.Rc.ToString()), ("tRFC", c => c.Rfc.ToString()),
        ("Command Rate", c => c.Commande2T ? "2T" : "1T"),
        ("Gear Down Mode", c => c.GearDown ? "Enabled" : "Disabled"),
        ("Power Down Enable", c => c.PowerDown ? "Enabled" : "Disabled"),
    ];

    /// <summary>Le socle JEDEC du type, la même convention que l'inférence XMP de DrDXT (DDR4 3200, DDR5 4800) : une
    /// inférence, pas la vitesse JEDEC du kit, que seul son SPD porte (lu par le bus SMBus, que DrDXT refuse).</summary>
    public static int SocleJedec(TypeMemoireAmd type) => type == TypeMemoireAmd.Ddr5 ? 4800 : 3200;

    /// <summary>Pur : chaque timing réglé (pas « Auto ») qui diffère, canal par canal, sur tous les canaux.</summary>
    public static IReadOnlyList<EcartRegleApplique> Comparer(IReadOnlyList<BiosSettingReading> bios, TimingsMemoire appliques)
    {
        var ecarts = new List<EcartRegleApplique>();
        foreach (var (nom, applique) in Timings)
        {
            if (bios.FirstOrDefault(r => r.Setting == nom) is not { } r || r.Libelle == "Auto") continue;
            foreach (var c in appliques.Canaux)
                if (applique(c) != r.Libelle) ecarts.Add(new EcartRegleApplique(nom, r.Libelle, applique(c), (char)('A' + c.Canal)));
        }
        return ecarts;
    }

    /// <summary>
    /// Pur : true quand un profil est demandé (EXPO ni « Disabled » ni « Auto ») et que tous les canaux tournent au socle
    /// JEDEC ou dessous. False quand aucun profil n'est demandé : une mémoire lente réglée à la main n'est pas un mode de
    /// secours. Null quand on ne peut pas trancher : profil non lu, ou kit dont la vitesse annoncée ne dépasse pas le socle
    /// (sa vitesse de profil égale sa vitesse JEDEC).
    /// </summary>
    public static bool? ModeDeSecours(IReadOnlyList<BiosSettingReading> bios, TimingsMemoire appliques, int vitesseAnnonceeKit)
    {
        if (bios.FirstOrDefault(r => r.Setting == "EXPO") is not { } expo) return null;
        if (expo.Libelle is "Disabled" or "Auto") return false;
        var socle = SocleJedec(appliques.Type);
        if (vitesseAnnonceeKit <= socle) return null;
        return appliques.Canaux.All(c => c.DebitMts <= socle);
    }

    /// <summary>Pur : le constat sur une machine AMD reconnue. Null = non lu (timings appliqués ou BIOS nommé).</summary>
    public static IEnumerable<RepairFinding> Juger(GenerationAmd.Ligne? generation, IReadOnlyList<BiosSettingReading>? bios,
        TimingsMemoire? appliques, int vitesseAnnonceeKit)
    {
        if (generation is null) yield break;
        const string Nom = "regle contre applique (memoire)";
        if (appliques is null)
        {
            yield return new RepairFinding("RAM", Nom, null, null, RepairStatus.Unknown, Docteur.T("RCA.AppliquesNonLus"));
            yield break;
        }
        if (bios is null || !bios.Any(r => r.Setting == "EXPO" || Timings.Any(t => t.Nom == r.Setting)))
        {
            yield return new RepairFinding("RAM", Nom, null, null, RepairStatus.Unknown, Docteur.T("RCA.BiosNonNomme"));
            yield break;
        }
        if (ModeDeSecours(bios, appliques, vitesseAnnonceeKit) == true)
        {
            var debit = appliques.Canaux.Max(c => c.DebitMts);
            yield return new RepairFinding("RAM", Nom, $"{debit} MT/s", null, RepairStatus.Suspicious,
                Docteur.T("RCA.Secours", bios.First(r => r.Setting == "EXPO").Libelle, debit, vitesseAnnonceeKit));
            yield break;
        }
        var ecarts = Comparer(bios, appliques);
        yield return ecarts.Count == 0
            ? new RepairFinding("RAM", Nom, null, null, RepairStatus.Observed, Docteur.T("RCA.Conforme", appliques.Canaux.Count))
            : new RepairFinding("RAM", Nom, string.Join(", ", ecarts.Select(e => $"{e.Reglage} {e.Canal}")), null, RepairStatus.NonDefault,
                Docteur.T("RCA.Ecarts", string.Join(" ; ", ecarts.Select(e => $"{e.Reglage} {e.Demande} → {e.Applique} ({e.Canal})"))));
    }
}
