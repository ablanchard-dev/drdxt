using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Repair;
using DrDxt.Detection.Wmi;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// -----------------------------------------------------------------------
// 19/09/2026. Un chiffre qui change de forme selon la machine qui le produit.
//
// `DiagnosticReport` — le rapport que le client colle dans son ticket — porte un test nomme
// `Numbers_use_an_invariant_format_so_a_pasted_report_reads_the_same_everywhere`. La regle
// est donc ecrite, et tenue... pour les champs que ce test inspecte.
//
// Elle ne l'etait pas a la SOURCE. `RepairAuditService` compose l'explication du constat
// thermique avec :
//
//     double.TryParse(zone, out var dk)            // lecture d'une valeur WMI
//     (dk / 10 - 273.15).ToString("0.0")           // ecriture du resultat
//
// Les deux emploient la culture courante. Sur la machine de reference, francaise, la temperature
// s'ecrit « 29,9 °C » avec une virgule — et cette phrase part telle quelle dans le rapport
// de support. Deux surfaces, une seule promesse, une seule l'applique : le motif le plus
// frequent de cette boucle.
//
// Cote lecture le risque est le meme, en miroir : une valeur WMI qui arriverait avec un
// point decimal ne se lirait pas sur une machine francaise, et le produit afficherait la
// chaine brute a la place de la temperature.
//
// Ce que ce fichier ne dit pas : quelle langue l'interface doit parler. Le rapport de
// support est un format d'echange, pas un ecran — il se lit pareil a Lille et a Seattle.
// -----------------------------------------------------------------------
public sealed class UnRapportSeLitPAREIL_PartoutTests
{
    /// <summary>Un WMI qui rend une temperature de zone ACPI, en dixiemes de kelvin.</summary>
    private sealed class ZoneThermique(string valeur) : IWmiQuery
    {
        public Result<IReadOnlyList<WmiRow>> Query(string wql) => QueryInNamespace("", wql);

        public Result<IReadOnlyList<WmiRow>> QueryInNamespace(string ns, string wql)
            => wql.Contains("CurrentTemperature", StringComparison.Ordinal)
                ? Result<IReadOnlyList<WmiRow>>.Ok(
                    [new WmiRow(new Dictionary<string, object?> { ["CurrentTemperature"] = valeur })])
                : Result<IReadOnlyList<WmiRow>>.Ok(Array.Empty<WmiRow>());
    }

    private static string ExplicationThermique(string valeurWmi)
    {
        var service = new RepairAuditService(new ServicesMuets(), new NoyauMuet(), new ArretsMuets(),
            new MemoireMuette(), new VidagesMuets(), new ZoneThermique(valeurWmi),
            // 06/10/2026 : sans doublure, le vrai HWiNFO de la machine de test répondait à la place de la zone ACPI.
            temperatureHwinfo: () => null);

        return service.Lire()
            .Single(c => c.Domaine == "THERMIQUE" && c.Nom == "temperature du processeur")
            .Explication ?? "";
    }

    /// <summary>Execute un geste sous une culture donnee, puis rend la culture d'origine.</summary>
    private static T SousLaCulture<T>(string nom, Func<T> geste)
    {
        var avant = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(nom);
            return geste();
        }
        finally { CultureInfo.CurrentCulture = avant; }
    }

    [Fact]
    public void Le_crible_lit_bien_le_constat_thermique()
    {
        // Temoin de contenu : sans lui, un constat renomme rendrait tout vert sur du vide.
        ExplicationThermique("3031").Should().Contain("zone thermique ACPI");
    }

    [Fact]
    public void La_temperature_s_ecrit_PAREIL_en_francais_et_en_anglais()
    {
        var fr = SousLaCulture("fr-FR", () => ExplicationThermique("3031"));
        var en = SousLaCulture("en-US", () => ExplicationThermique("3031"));

        fr.Should().Be(en,
            "cette phrase part dans le rapport que le client colle dans son ticket, et le "
            + "rapport promet de se lire pareil partout");
    }

    [Fact]
    public void Un_chiffre_du_rapport_ne_porte_pas_de_virgule_decimale()
    {
        // 3100 dixiemes de kelvin = 310,0 K = 36,85 °C, arrondi a 36,9 : une decimale non nulle
        // et aucune ambiguite d'arrondi. (Premier essai : 3031, dont le resultat est 29,95 —
        // arrondi a 30,0, donc sans decimale visible. Mon arithmetique, pas le produit.)
        var fr = SousLaCulture("fr-FR", () => ExplicationThermique("3100"));

        fr.Should().Contain("36.9 °C",
            "sur une machine francaise, `ToString(\"0.0\")` ecrit « 29,9 » : le support lirait "
            + "un nombre different de celui qu'un autre client lui envoie");
    }

    [Fact]
    public void Une_valeur_WMI_a_point_decimal_se_lit_meme_en_francais()
    {
        // Le miroir : la lecture aussi emploie la culture courante. Une valeur qui arriverait
        // avec un point serait illisible en francais, et le produit afficherait la chaine brute.
        var fr = SousLaCulture("fr-FR", () => ExplicationThermique("3031.5"));

        fr.Should().Contain("°C", "la valeur doit etre convertie, pas recopiee telle quelle");
        fr.Should().NotContain("(3031.5)", "c'est la chaine brute, signe que la lecture a echoue");
    }

    [Fact]
    public void Une_zone_absente_le_DIT_au_lieu_d_inventer_un_chiffre()
    {
        // Contrepoids : rendre le format invariant ne doit pas fabriquer une mesure.
        var sansZone = new RepairAuditService(new ServicesMuets(), new NoyauMuet(), new ArretsMuets(),
                new MemoireMuette(), new VidagesMuets(), new WmiMuet(), temperatureHwinfo: () => null)
            .Lire()
            .Single(c => c.Domaine == "THERMIQUE" && c.Nom == "temperature du processeur");

        sansZone.Statut.Should().Be(RepairStatus.Unknown);
        sansZone.Explication.Should().Contain("aucune zone thermique ACPI ne répond");
    }
}
