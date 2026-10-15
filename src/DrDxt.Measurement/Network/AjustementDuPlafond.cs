namespace DrDxt.Measurement.Network;

/// <summary>
/// Le plafond d'envoi en boucle fermee, pendant la partie.
///
/// Un plafond fixe (50 % du plus fort envoi vu au scan) serait calibre sur une machine, et la consigne
/// QoS de Windows est lache (200 demandes, ~340 obtenus). Ici on le juge la ou il agit : sur une
/// fenetre glissante prise plafond en place, le meme juge que le scan (<see cref="Bufferbloat.Juger"/>)
/// dit si le ping monte encore quand l'envoi est a son maximum.
///
///   - il monte encore de <see cref="Bufferbloat.SeuilMs"/> ou plus : on descend de 20 % ;
///   - il monte de moins de la moitie du seuil, sur assez d'echantillons des deux cotes : on remonte
///     de 10 %, jamais au-dessus de la capacite vue au scan ;
///   - sinon (trop peu vu, zone grise) : on ne touche a rien. Pas de mouvement sans preuve.
///
/// Descendre plus vite que monter : un ping qui monte se sent tout de suite, un envoi un peu trop
/// bride ne coute qu'un transfert un peu plus lent. Rien n'est envoye pour mesurer : la fenetre est le
/// trafic reel du joueur.
/// </summary>
public static class AjustementDuPlafond
{
    public const double FacteurDescente = 0.8;
    public const double FacteurMontee = 1.1;

    /// <summary>Le plancher de l'allowlist du helper (`OpAllowlist.MinUploadCapBps`, 1 Mbit/s) : un
    /// plafond plus bas serait refuse par le helper de toute facon.</summary>
    public const long PlancherBps = 1_000_000;

    /// <summary>Le plafond a appliquer apres cette fenetre. <paramref name="plafondMaxBps"/> = la
    /// capacite vue au scan : la boucle ne l'ouvre jamais au-dela.</summary>
    public static long Suivant(long plafondActuelBps, long plafondMaxBps, IReadOnlyList<EchantillonEnvoi> fenetre)
    {
        var verdict = Bufferbloat.Juger(fenetre);
        if (verdict.NRepos < Bufferbloat.MinParCote || verdict.NCharge < Bufferbloat.MinParCote)
            return plafondActuelBps;

        var hausse = verdict.PingChargeP90!.Value - verdict.PingReposP90!.Value;
        var suivant = hausse >= Bufferbloat.SeuilMs ? plafondActuelBps * FacteurDescente
            : hausse < Bufferbloat.SeuilMs / 2 ? plafondActuelBps * FacteurMontee
            : plafondActuelBps;
        return Math.Clamp((long)suivant, PlancherBps, Math.Max(PlancherBps, plafondMaxBps));
    }
}
