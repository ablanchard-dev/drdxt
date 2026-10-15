namespace DrDxt.Core;

/// <summary>
/// Le pur du décalage de tension
/// des cœurs Intel par la boîte aux lettres OC (MSR 0x150). Source : erpalma/throttled (calc_undervolt_msr, calc_undervolt_mv,
/// get_undervolt) : lecture = 0x80000010 | plan &lt;&lt; 40 ; écriture = 0x80000011 | plan &lt;&lt; 40 | (pas &amp; 0x7FF) &lt;&lt; 21,
/// un pas = 1/1,024 mV, sur 11 bits en complément à deux ; réponse : statut en bits 39:32 (0 = accepté), décalage en 31:21.
/// Une précaution de plus que la source : les bits 20:0 lus juste avant (ratio, tension cible, mode posés par le BIOS) sont
/// recopiés, jamais remis à zéro. Seulement vers le bas, jamais sous <see cref="PlancherMv"/>, seulement les cœurs et le cache.
/// Lu sur la machine de référence (i7-14700F, sans SGX) : décalage 0 sur les cœurs et le cache, lecture acceptée.
/// </summary>
public static class TensionCpu
{
    public const int PlanCoeurs = 0;
    public const int PlanCache = 2;
    /// <summary>La borne dure : rien ne s'écrit sous −125 mV. **Non chiffré** : les sous-tensions tenues sur Raptor Lake publiées par
    /// les joueurs vont de −50 à −100 mV ; au-delà, l'instabilité est la règle. Le levier part bien au-dessus.</summary>
    public const int PlancherMv = 125;
    private const double PasParMv = 1.024;

    public static ulong CommandeLecture(int plan) => 0x8000001000000000UL | ((ulong)plan << 40);

    /// <summary>Le décalage lu, en mV (arrondi), ou null si la boîte aux lettres a refusé (statut non nul).</summary>
    public static int? DecalageMv(ulong reponse)
    {
        if (((reponse >> 32) & 0xFF) != 0) return null;
        var pas = (int)((reponse >> 21) & 0x7FF);
        if (pas >= 0x400) pas -= 0x800;
        return (int)Math.Round(pas / PasParMv, MidpointRounding.AwayFromZero);
    }

    /// <summary>La commande qui réécrit exactement ce qui a été lu (décalage, ratio, cible, mode), pour rendre un plan à sa valeur
    /// d'avant quelle qu'elle soit ; null si la lecture avait été refusée.</summary>
    public static ulong? CommandeRetour(int plan, ulong lecture)
        => plan is not (PlanCoeurs or PlanCache) || DecalageMv(lecture) is null
            ? null
            : 0x8000001100000000UL | ((ulong)plan << 40) | (lecture & 0xFFFFFFFF);

    /// <summary>La commande d'écriture, ou null hors des bornes : décalage au-dessus de zéro ou sous le plancher, plan autre que
    /// cœurs ou cache, lecture d'avant refusée.</summary>
    public static ulong? CommandeEcriture(int plan, int decalageMv, ulong lecture)
    {
        if (plan is not (PlanCoeurs or PlanCache) || decalageMv > 0 || decalageMv < -PlancherMv) return null;
        if (DecalageMv(lecture) is null) return null;
        var pas = (long)Math.Round(decalageMv * PasParMv, MidpointRounding.AwayFromZero);
        return 0x8000001100000000UL | ((ulong)plan << 40) | (((ulong)pas & 0x7FF) << 21) | (lecture & 0x1FFFFF);
    }
}
