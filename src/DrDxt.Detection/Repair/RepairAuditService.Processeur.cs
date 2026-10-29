using System.Runtime.Versioning;
using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Cpu;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Settings;
using Microsoft.Win32;

namespace DrDxt.Detection.Repair;

/// <summary>Processeur : le plan d'alimentation et ses sous-valeurs, le bridage, MMCSS et les fils de rendu de Call of Duty.</summary>
public sealed partial class RepairAuditService
{
    // ── POWER ──────────────────────────────────────────────────────────────────────────────
    // Le nom du plan ne suffit pas : deux plans nommes « Equilibre » peuvent porter des reglages
    // differents. On lit le GUID, puis chaque sous-valeur processeur contre le defaut de sa propre
    // personnalite : c'est la que se voient les degats d'un ancien optimiseur, sans connaitre son
    // nom. Le parking de coeurs reste dans l'outil : c'est un echantillonnage de 3 s, pas une
    // lecture.
    private IEnumerable<RepairFinding> Power()
    {
        var plan = new PowerProvider().GetActiveSchemeGuid();
        var planGuid = plan.IsOk ? plan.Value?.Trim('{', '}').ToLowerInvariant() : null;

        yield return ModeAlimentation.JugerPlan(planGuid);
        // Le mode d'alimentation de Windows 11, qui ne joue qu'avec Équilibré ou un plan
        // qui en dérive : jugé par la personnalité du plan.
        var personnaliteDuPlan = Guid.TryParse(planGuid, out var g) ? PowerSettingsReader.Personnalite(g)?.ToString() : null;
        foreach (var m in ModeAlimentation.JugerMode(personnaliteDuPlan, OverlaySecteur(), Batterie())) yield return m;

        if (planGuid is null || !Guid.TryParse(planGuid, out var planActif)) yield break;

        // Les valeurs se lisent dans le plan actif, leur défaut dans sa personnalité : un plan personnalisé a les deux différents.
        var (personnalite, planLu) = CleDeLecture(planActif, PowerSettingsReader.Personnalite);
        var portable = Batterie() == true;
        foreach (var v in new PowerSettingsReader().LireProcesseur(personnalite, planLu))
        {
            var (statut, phrase) = JugerReglageProcesseur(v, portable);
            // Un ecart sans geste est un ecart invisible. `RepairTriage.Trier` range dans
            // « verifies » (comptes, jamais affiches) tout ce qui n'est ni suspect ni nuisible et
            // ne porte aucune action. Un `NonDefault` muet finirait donc dans le meme sac qu'un
            // reglage identique au defaut : le minimum processeur a 100 %, la signature des scripts
            // gaming, serait lu, compte, et jamais montre. La recommandation est la seule facon de
            // le rendre visible sans le declarer nuisible : la doctrine « different n'est pas
            // endommage » interdit `Suspicious`, elle n'impose pas le silence.
            // DISPLAY suit la meme regle.
            yield return new RepairFinding("POWER", v.Nom,
                v.ValeurAc is { } a ? Lisible(v.Nom, a, v.Unite) : null,
                v.DefautAc is { } d ? Lisible(v.Nom, d, v.Unite) : null,
                statut, phrase,
                statut is RepairStatus.NonDefault or RepairStatus.Suspicious ? RepairAction.Recommandation : RepairAction.Aucune);
        }
    }

    /// <summary>Pur : la personnalité dont se lisent les défauts, et le plan dont se lisent les valeurs. Personnalité non lue :
    /// le plan lui-même (un plan Windows est sa propre personnalité ; sinon la lecture du défaut échoue et se dit non lue).</summary>
    internal static (Guid Personnalite, Guid Plan) CleDeLecture(Guid planActif, Func<Guid, Guid?> personnaliteDe)
        => (personnaliteDe(planActif) ?? planActif, planActif);

    /// <summary>Pur : le jugement d'un réglage processeur contre le défaut de sa personnalité. Un écart reste un écart, il ne dit
    /// pas que la valeur est mauvaise. Un frein (plafond de fréquence, refroidissement passif) n'est « à corriger » que s'il
    /// s'écarte de ce défaut, sur un PC fixe : égal au défaut, c'est le réglage de Windows ; défaut non lu, rien ne prouve un
    /// écart ; sur un portable, souvent voulu par le constructeur. Le constat le dit au lieu de l'accuser.</summary>
    internal static (RepairStatus Statut, string Phrase) JugerReglageProcesseur(PowerSettingValue v, bool portable)
    {
        var (statut, phrase) = v.EcarteDuDefaut switch
        {
            null => (RepairStatus.Unknown, Docteur.T("RA.Power.NonLu")),
            false => (RepairStatus.ExpectedDefault, Docteur.T("RA.Power.Identique")),
            true => (RepairStatus.NonDefault, Docteur.T("RA.Power.Ecart")),
        };
        if (PowerSettingsReader.Frein(v) != true || v.EcarteDuDefaut == false) return (statut, phrase);
        return (portable || v.EcarteDuDefaut is null ? RepairStatus.NonDefault : RepairStatus.Suspicious,
            Docteur.T("RA.Power.Frein") + (portable ? Docteur.T("RA.Power.FreinPortable") : ""));
    }

    // « Mode de boost : 2 » ne dit rien à personne. Table PERFBOOSTMODE lue sur
    // learn.microsoft.com (options-for-perf-state-engine-perfboostmode) : Microsoft documente 0 à 4, 3 = 1
    // et 4 = 2, et 2 n'est « agressif » qu'en CPPC non autonome ; en autonome il vaut « activé ».
    // Powercfg en énumère 7 sur Windows 11 26200 (5 et 6 « à garanti »), non documentés :
    // ils s'affichent en chiffre nu, sans traduction inventée.
    // « 2 (agressif) » partout serait faux sur un processeur autonome.
    internal static string Lisible(string nom, uint valeur, string unite) => nom switch
    {
        "mode de boost" => valeur switch
        {
            0 => Docteur.T("RA.Boost.Desactive"),
            1 or 3 => Docteur.T("RA.Boost.Active", valeur),
            2 or 4 => Docteur.T("RA.Boost.Agressif", valeur),
            _ => valeur.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
        "preference energie/perf" => Docteur.T("RA.Epp", valeur),
        "politique de refroidissement" => Docteur.T(valeur == 0 ? "RA.Refroidissement.Passif" : "RA.Refroidissement.Actif"),
        // 0 MHz n'est pas une fréquence : c'est « aucun plafond ».
        "frequence maximale" or "frequence maximale (coeurs efficaces)" when valeur == 0 => Docteur.T("RA.Freq.SansPlafond"),
        // « 100% » sur la page Processeur ; en français l'unité se sépare du nombre.
        _ => string.IsNullOrEmpty(unite) ? valeur.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{valeur} {unite}",
    };

    // Le mode d'alimentation sur secteur (Windows 11), sans accolades ; null = absent ou non lu.
    private static string? OverlaySecteur()
    {
        try
        {
            using var cle = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
            return (cle?.GetValue("ActiveOverlayAcPowerScheme") as string)?.Trim('{', '}').ToLowerInvariant();
        }
        catch (Exception) { return null; }
    }

    private IEnumerable<RepairFinding> FilsDeRenduCallOfDuty()
    {
        // Le dossier du fichier le plus récent parmi les emplacements connus : la version courante écrit sous
        // %LOCALAPPDATA%, Documents ne garde qu'une ancienne version (voir ReglagesCod.Trouver).
        if (DrDxt.Detection.Game.ReglagesCod.Trouver()?.DirectoryName is not { } dossier) return [];
        var lu = DrDxt.Detection.Jeu.ConfigurationCallOfDuty.Lire(dossier);
        if (lu is null) return [];
        // Les cœurs de la classe la plus rapide, lus comme le banc processeur ; l'estimation par les comptes WMI en secours.
        var rapides = CoeursRapidesLus();
        if (rapides > 0) return DrDxt.Detection.Jeu.ConfigurationCallOfDuty.Juger(lu, rapides);
        var cpu = _wmi.Query("SELECT Manufacturer, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
        var ligne = cpu.IsOk ? cpu.Value!.FirstOrDefault() : null;
        var performants = ligne is null ? 0
            : DrDxt.Detection.Jeu.ConfigurationCallOfDuty.CoeursPerformants(ligne.GetString("Manufacturer") ?? "",
                ligne.GetInt("NumberOfCores") ?? 0, ligne.GetInt("NumberOfLogicalProcessors") ?? 0);
        return DrDxt.Detection.Jeu.ConfigurationCallOfDuty.Juger(lu, performants);
    }

    private static int CoeursRapidesLus()
    {
        try { return DrDxt.Detection.Cpu.CpuTopology.CoeursRapides(new DrDxt.Detection.Cpu.CpuTopology().Lire()).Count; }
        catch (Exception) { return 0; }
    }

    // ── CPU : la frequence reelle, pas celle que WMI raconte ───────────────────────────────
    private static IEnumerable<RepairFinding> Cpu()
    {
        var lecteur = new CpuFrequencyReader();
        var cpu = lecteur.Lire();

        // Les cœurs chargés, pas l'instance `_Total` : elle fait la moyenne de tous les processeurs logiques,
        // cœurs inactifs compris. Mesuré le 09/09/2026 pendant une partie : `_Total` a 3 622 MHz quand
        // les huit a dix cœurs qui travaillaient tournaient a 3 861. La moyenne repond a une question que personne ne pose.
        //
        // Et au repos, aucune des deux ne predit ce que la machine rend en charge : sur ce
        // processeur un cœur seul tient 5 292 MHz et huit cœurs sous charge continue tombent a
        // 3 698. La phrase le dit plutot que de laisser croire qu'un scan mesure un jeu.
        var charges = lecteur.LireParProcesseur()
            .Where(c => c.UtilisationPourcent >= 60 && c.PourcentageDeNominal is not null)
            .ToList();
        var baseMhz = cpu.FrequenceNominaleMhz ?? 0;

        if (charges.Count > 0 && baseMhz > 0)
        {
            var moyenne = charges.Average(c => c.PourcentageDeNominal!.Value) * baseMhz / 100.0;
            yield return new RepairFinding("CPU", "frequence effective",
                Docteur.T(charges.Count == 1 ? "RA.Freq.Chargee" : "RA.Freq.Chargees", moyenne.ToString("0"), charges.Count), null,
                RepairStatus.Observed, Docteur.T("RA.Freq.CoeursQuiTravaillent"));
        }
        else if (cpu.FrequenceEffectiveMhz is { } mhz)
        {
            yield return new RepairFinding("CPU", "frequence effective",
                mhz.ToString("0") + " MHz"
                + (baseMhz > 0 ? Docteur.T("RA.Freq.Base", baseMhz.ToString("0"), (cpu.PourcentageDeNominal ?? 0).ToString("0")) : ""),
                null, RepairStatus.Observed,
                // Cette phrase citait « 2100 MHz » et « un i7-14700F » en dur, lus sur
                // la machine de référence : faux sur toute autre machine.
                Docteur.T("RA.Freq.AucunCharge")
                + (baseMhz > 0 ? Docteur.T("RA.Freq.Annoncee", baseMhz.ToString("0")) : ""));
        }
        else
        {
            yield return new RepairFinding("CPU", "frequence effective", null, null,
                RepairStatus.Unknown, Docteur.T("RA.Freq.NonLue"));
        }

        // Le bridage porte aussi la question thermique : on ne sait pas combien il fait (voir
        // Thermique), mais on sait si un indicateur Windows est leve. Un seul constat pour les
        // deux lectures : c'est le meme masque.
        //
        // Le cas « aucun » est Observed et jamais ExpectedDefault : `Performance Limit Flags` ne
        // voit pas les limites de puissance du paquet (RAPL). Mesure sur cette machine : les
        // indicateurs valent 0 pendant que la frequence tombe de 5292 a 3698 MHz sous charge
        // soutenue. On observe une absence d'indicateur, on ne certifie pas une absence de limite.
        yield return cpu.EstBride switch
        {
            null => new RepairFinding("CPU", "bridage", null, null, RepairStatus.Unknown, Docteur.T("RA.Bridage.NonLu")),
            false => new RepairFinding("CPU", "bridage", Docteur.T("RA.Bridage.Aucun"), null, RepairStatus.Observed,
                Docteur.T("RA.Bridage.AucunExplication")),
            true => new RepairFinding("CPU", "bridage", Docteur.T("RA.Bridage.Masque", ((long)(cpu.IndicateursDeLimite ?? 0)).ToString("X")),
                null, RepairStatus.Observed, Docteur.T("RA.Bridage.Leve")),
        };
    }

    private static IEnumerable<RepairFinding> Mmcss()
    {
        foreach (var m in new MmcssReader().Lire())
        {
            var (statut, action) = m.Effet switch
            {
                MmcssEffet.NonLu => (RepairStatus.Unknown, RepairAction.Aucune),
                // Un placebo n'est pas un degat : il ne casse rien, il ment.
                MmcssEffet.SansEffet => (RepairStatus.Observed, RepairAction.Aucune),
                MmcssEffet.Desactive => (RepairStatus.Suspicious, RepairAction.Recommandation),
                // `Observed` et non `ExpectedDefault` : la page Microsoft ne publie aucun defaut
                // pour ces reglages. Savoir ce qu'une valeur fait ne dit rien de son origine.
                _ => (RepairStatus.Observed, RepairAction.Aucune),
            };

            yield return new RepairFinding("MMCSS", m.Nom, m.Valeur, null, statut,
                Action: action,
                Explication: m.Phrase + "  [source : " + MmcssReader.Source + "]");
        }
    }
}
