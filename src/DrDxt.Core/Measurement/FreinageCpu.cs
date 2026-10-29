namespace DrDxt.Core;

/// <summary>
/// Une seconde du processeur : deux températures et quatre raisons de freiner, car la température seule trompe. Relevé sur
/// la machine de référence : le paquet (0x1B1) donne le cœur le plus chaud (98 °C lus, cœur 0 à 77 °C, écart moyen
/// 15 °C), et un soir le frein était la limite de courant (bit 12 de 0x19C, 544 s sur 966), pas la chaleur.
/// Source : Intel SDM vol. 4, IA32_THERM_STATUS (0x19C) et IA32_PACKAGE_THERM_STATUS (0x1B1) ; bits de statut (l'instant),
/// pas les journaux collants (1, 3, 11, 13). Le bit 10 du cœur dit « sous la fréquence demandée, pour
/// toute raison » ; seul celui du paquet dit « par la limite de puissance » : c'est lui qui fait <see cref="LimiteW"/>.
/// PROCHOT (bit 2) = un signal venu d'ailleurs (VRM, carte), pas la chaleur des cœurs. <paramref name="VentiloCpu"/> : le
/// ventilateur du processeur à la même seconde (puce Super I/O) ; <paramref name="Ventilateurs"/> : tous ceux de la carte
/// (demande 23) ; null si non lus.
/// </summary>
public sealed record EtatCpu(int ChaudC, int? Coeur0C, bool Chaleur, bool Prochot, bool LimiteW, bool LimiteCourant,
    VentilateurLu? VentiloCpu = null, IReadOnlyList<VentilateurLu>? Ventilateurs = null)
{
    /// <summary>Pur : TjMax (0x1A2), l'état du paquet (0x1B1) et celui du cœur 0 (0x19C, null = non lu). Null si les
    /// registres ne disent rien de plausible. Le cœur ne compte que si sa lecture est marquée valide (bit 31).</summary>
    public static EtatCpu? Decoder(ulong temperatureTarget, ulong paquet, ulong? coeur)
    {
        var tj = (int)((temperatureTarget >> 16) & 0xFF);
        var ecart = (int)((paquet >> 16) & 0x7F);
        if (tj is < 50 or > 120 || ecart > tj) return null;
        var c = coeur is { } k && Bit(k, 31) && (int)((k >> 16) & 0x7F) <= tj ? k : 0UL;
        return new EtatCpu(tj - ecart, c != 0 ? tj - (int)((c >> 16) & 0x7F) : null,
            Bit(paquet, 0) || Bit(c, 0), Bit(paquet, 2) || Bit(c, 2), Bit(paquet, 10), Bit(c, 12));
    }

    private static bool Bit(ulong x, int i) => ((x >> i) & 1) == 1;
}

/// <summary>Les secondes d'une capture (ou d'une partie) mises ensemble : combien ont été freinées par chaque raison, et
/// les deux températures en moyenne. Le ventilateur du processeur pendant les secondes freinées par la chaleur
/// (cycle et régime moyens, sur <paramref name="SecondesVentiloChaud"/> secondes qui l'ont lu) : ce que le docteur juge.</summary>
public sealed record FreinageCpu(int Secondes, int Chaleur, int Prochot, int LimiteW, int LimiteCourant,
    double ChaudMoyenC, int ChaudMaxC, double? Coeur0MoyenC,
    int SecondesVentiloChaud = 0, double? VentiloPwmChaud = null, double? VentiloTrMinChaud = null)
{
    public static FreinageCpu? Depuis(IEnumerable<EtatCpu> etats)
    {
        var e = etats.ToList();
        if (e.Count == 0) return null;
        var coeurs = e.Select(x => x.Coeur0C).OfType<int>().ToList();
        var ventilos = e.Where(x => x.Chaleur).Select(x => x.VentiloCpu).OfType<VentilateurLu>().Where(v => v.PwmPourcent is not null).ToList();
        return new FreinageCpu(e.Count, e.Count(x => x.Chaleur), e.Count(x => x.Prochot), e.Count(x => x.LimiteW),
            e.Count(x => x.LimiteCourant), e.Average(x => x.ChaudC), e.Max(x => x.ChaudC), coeurs.Count > 0 ? coeurs.Average() : null,
            ventilos.Count, ventilos.Count > 0 ? ventilos.Average(v => v.PwmPourcent!.Value) : null,
            ventilos.Count > 0 ? ventilos.Average(v => v.TrMin) : null);
    }

    /// <summary>Comptes additionnés, moyennes pondérées par les secondes.</summary>
    public static FreinageCpu? Cumuler(IEnumerable<FreinageCpu?> morceaux)
    {
        var m = morceaux.OfType<FreinageCpu>().Where(f => f.Secondes > 0).ToList();
        if (m.Count == 0) return null;
        var n = m.Sum(f => f.Secondes);
        var avecCoeur = m.Where(f => f.Coeur0MoyenC is not null).ToList();
        var avecVentilo = m.Where(f => f.SecondesVentiloChaud > 0 && f.VentiloPwmChaud is not null && f.VentiloTrMinChaud is not null).ToList();
        var nv = avecVentilo.Sum(f => f.SecondesVentiloChaud);
        return new FreinageCpu(n, m.Sum(f => f.Chaleur), m.Sum(f => f.Prochot), m.Sum(f => f.LimiteW), m.Sum(f => f.LimiteCourant),
            m.Sum(f => f.ChaudMoyenC * f.Secondes) / n, m.Max(f => f.ChaudMaxC),
            avecCoeur.Count > 0 ? avecCoeur.Sum(f => f.Coeur0MoyenC!.Value * f.Secondes) / avecCoeur.Sum(f => f.Secondes) : null,
            nv, nv > 0 ? avecVentilo.Sum(f => f.VentiloPwmChaud!.Value * f.SecondesVentiloChaud) / nv : null,
            nv > 0 ? avecVentilo.Sum(f => f.VentiloTrMinChaud!.Value * f.SecondesVentiloChaud) / nv : null);
    }

    /// <summary>La part des secondes (0-1).</summary>
    public double Part(int secondes) => Secondes == 0 ? 0 : (double)secondes / Secondes;

    /// <summary>Sous 5 % du temps, une raison ne coûte presque pas de fréquence. **Non chiffré** : quelques secondes par minute.</summary>
    public const double PartNegligeable = 0.05;
    /// <summary>Un quart du temps à sa limite de courant ou de puissance : c'est elle qui bride (544 s sur 966 sur la machine de
    /// référence). **Non chiffré** : choisi sous ce cas réel, au-dessus des passages brefs.</summary>
    public const double PartQuiBride = 0.25;

    /// <summary>Pur : ce qui a freiné, la seule règle, partagée par la carte des parties et le docteur pour
    /// qu'ils ne se contredisent pas. Le courant d'abord quand il pèse au moins autant que la chaleur : c'est lui qu'un réglage du
    /// BIOS lève. PROCHOT (signal de la carte mère) et la limite de puissance comptent aussi : sans eux, un non-K tenu à 65 W
    /// ou un portable bridé par sa régulation passeraient pour « presque pas freinés ».</summary>
    public RaisonDuFreinage Raison()
        => Part(LimiteCourant) >= PartQuiBride && LimiteCourant >= Chaleur ? RaisonDuFreinage.Courant
            : Part(Chaleur) >= PartNegligeable ? RaisonDuFreinage.Chaleur
            : Part(Prochot) >= PartNegligeable ? RaisonDuFreinage.Prochot
            : Part(LimiteW) >= PartQuiBride ? RaisonDuFreinage.Puissance
            : RaisonDuFreinage.PasFreine;
}

/// <summary>Ce qui a freiné une partie, d'après ses secondes (<see cref="FreinageCpu.Raison"/>).</summary>
public enum RaisonDuFreinage { Courant, Chaleur, Prochot, Puissance, PasFreine }
