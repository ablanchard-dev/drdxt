using DrDxt.Core;

namespace DrDxt.Detection.Settings;

/// <summary>
/// XMP/EXPO n'est pas un flag : on l'infère (ConfiguredClockSpeed vs Speed/SPD rated).
/// Libeller « probablement OFF ». Inconnu si on ne peut pas comparer : on ne ment pas.
///
/// Heuristique JEDEC (inférence, probable) :
///   DDR4 : vitesse JEDEC base ≤ 3200 MHz ; kits XMP jusqu'à 3600/4000/4400 mais base 3200
///   DDR5 : vitesse JEDEC base ≥ 4800 MHz
/// XMP/EXPO est OFF seulement quand le module tourne à ou sous sa base JEDEC alors que sa vitesse
/// nominale est au-dessus (il est resté au défaut JEDEC, pas sur le profil). Tourner sous le
/// max nominal mais au-dessus du JEDEC (ex. kit 4000 stable à 3800) = XMP ON : comparer au max
/// nominal donnerait un faux « off » (on n'invente jamais un problème).
/// Données manquantes → Unknown (jamais On par défaut).
/// </summary>
public static class XmpInferencer
{
    // Tensions nominales JEDEC, en mV. Un module qui tourne nettement au-dessus a forcément un
    // profil chargé : rien d'autre ne monte la tension.
    private const int Ddr4JedecMv = 1200;
    private const int Ddr5JedecMv = 1100;
    private const int ProfileVoltageMarginMv = 50;   // marge de bruit de mesure

    public static TriState Infer(IReadOnlyList<MemoryModule> modules)
    {
        if (modules.Count == 0) return TriState.Unknown;

        // ── Seconde source : la tension configurée. Elle sert uniquement quand la fréquence
        //    ne peut pas trancher (vitesse nominale ou configurée non lue).
        //
        //    Jamais avant la fréquence : une carte peut charger la tension du profil, échouer l'entraînement et retomber
        //    au JEDEC (1,35 V à 2133 MT/s), et « profil chargé » masquerait le défaut. Asymétrique : elle conclut On,
        //    jamais Off (des profils XMP tournent à la tension JEDEC), comme Above4G et Csm. ──
        if (modules.Any(m => m.RatedMhz <= 0 || m.ConfiguredMhz <= 0))
            return modules.Any(ProfileVoltageProven) ? TriState.On : TriState.Unknown;

        foreach (var m in modules)
        {
            int jedecBase = SocleJedec.Mhz(m);

            // OFF seulement quand le module est à ou sous sa base JEDEC alors que sa vitesse nominale
            // est au-dessus (il tourne au défaut JEDEC, pas sur le profil). Sous le max nominal mais
            // au-dessus du JEDEC = XMP ON : jamais de « off » juste parce qu'il est sous le max.
            if (m.ConfiguredMhz <= jedecBase && m.RatedMhz > jedecBase)
                return TriState.Off;
        }

        // Tout au socle JEDEC (ou sous), sans vitesse nominale plus haute : la frequence ne prouve
        // rien. Apres un reset XMP, Windows rabat souvent la vitesse nominale sur la reglee
        // (2667/2667 releve sur la machine de reference le 29/06) : conclure On ici est le faux « XMP actif ». Seule la
        // tension peut encore trancher, et seulement vers On.
        if (modules.All(m => m.ConfiguredMhz <= SocleJedec.Mhz(m)))
            return modules.Any(ProfileVoltageProven) ? TriState.On : TriState.Unknown;

        return TriState.On;
    }

    // Le profil est prouvé chargé quand la tension configurée dépasse nettement le nominal
    // JEDEC du type lu. Type inconnu ou tension non lue (0 mV) ⇒ aucun signal, jamais une
    // conclusion : « pas lu » n'est pas « zéro volt ».
    private static bool ProfileVoltageProven(MemoryModule m)
    {
        if (m.ConfiguredVoltageMv <= 0) return false;

        int jedecMv = m.SmbiosMemoryType switch
        {
            SocleJedec.SmbiosDdr4 => Ddr4JedecMv,
            SocleJedec.SmbiosDdr5 => Ddr5JedecMv,
            _ => 0,
        };
        if (jedecMv == 0) return false;

        return m.ConfiguredVoltageMv >= jedecMv + ProfileVoltageMarginMv;
    }
}
