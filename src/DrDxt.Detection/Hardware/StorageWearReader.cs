using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.Detection.Wmi;

namespace DrDxt.Detection.Hardware;

/// <summary>L'usure et la temperature d'un disque, telles que Windows les publie.</summary>
/// <param name="Disque">Le nom lisible du disque.</param>
/// <param name="UsurePercent">Part d'endurance consommee, 0 a 100. <c>null</c> = non publiee.</param>
/// <param name="TemperatureC">En degres. <c>null</c> = non publiee.</param>
/// <param name="ErreursNonCorrigees">06/10/2026 (chantier B6) : erreurs de lecture et d'écriture que le disque n'a pas
/// pu corriger. <c>null</c> = aucun des deux compteurs publié.</param>
public sealed record UsureDisque(string Disque, int? UsurePercent, int? TemperatureC, int? ErreursNonCorrigees = null);

/// <summary>
/// L'usure des disques — et pourquoi la requete evidente rend zero ligne.
///
/// Verifie sur la machine avant d'ecrire une ligne, et la premiere tentative etait fausse.
/// Interroger directement `MSFT_StorageReliabilityCounter` rend **la classe presente et aucune
/// instance** : on en conclurait que la machine ne publie pas son usure. C'est faux. Le compteur
/// n'existe que par une ASSOCIATION au disque physique, et c'est cette forme qui repond :
///
/// <code>ASSOCIATORS OF {MSFT_PhysicalDisk.ObjectId="…"} where ResultClass=MSFT_StorageReliabilityCounter</code>
///
/// Mesure du 10/09/2026 sur la machine de reference : **Wear = 0, Temperature = 43-44 °C** pour un
/// Samsung SSD 990 PRO 2 To. `PowerOnHours` et `ReadErrorsTotal` reviennent VIDES sur ce NVMe —
/// tous les disques ne publient pas les memes compteurs, et les absents restent `null` plutot que
/// de valoir zero.
///
/// Lecture pure, non elevee.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StorageWearReader
{
    /// <summary>sans le prefixe `\\.\`. Avec lui, `ManagementObjectSearcher` rend
    /// « Parametre non valide » sur ce namespace imbrique — verifie le 10/09/2026.</summary>
    private const string Namespace = @"root\Microsoft\Windows\Storage";

    private readonly IWmiQuery _wmi;

    public StorageWearReader(IWmiQuery? wmi = null) => _wmi = wmi ?? new ManagementWmiQuery();

    public Result<IReadOnlyList<UsureDisque>> Lire()
    {
        var disques = _wmi.QueryInNamespace(Namespace,
            "SELECT * FROM MSFT_PhysicalDisk");
        if (!disques.IsOk)
            return Result<IReadOnlyList<UsureDisque>>.Fail(ErrorCodes.StorageFail, disques.Error!.Value.Message);

        var usures = new List<UsureDisque>();
        foreach (var d in disques.Value!)
        {
            var nom = d.GetString("FriendlyName") ?? "disque inconnu";
            var objectId = d.GetString("ObjectId");
            if (string.IsNullOrWhiteSpace(objectId)) continue;

            // Un ObjectId porte des antislashs et des guillemets : dans un litteral WQL, les deux
            // doivent etre echappes, sinon la requete est syntaxiquement fausse et rend zero ligne
            // — c'est-a-dire exactement l'allure d'un disque qui ne publierait rien.
            var echappe = objectId.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var compteurs = _wmi.QueryInNamespace(Namespace,
                "ASSOCIATORS OF {MSFT_PhysicalDisk.ObjectId=\"" + echappe + "\"} "
                + "WHERE ResultClass=MSFT_StorageReliabilityCounter");

            if (!compteurs.IsOk || compteurs.Value!.Count == 0)
            {
                usures.Add(new UsureDisque(nom, null, null));
                continue;
            }

            var c = compteurs.Value![0];
            var lecture = Entier(c, "ReadErrorsUncorrected");
            var ecriture = Entier(c, "WriteErrorsUncorrected");
            usures.Add(new UsureDisque(nom, Entier(c, "Wear"), Entier(c, "Temperature"),
                lecture is null && ecriture is null ? null : (lecture ?? 0) + (ecriture ?? 0)));
        }

        return Result<IReadOnlyList<UsureDisque>>.Ok(usures);
    }

    /// <summary>06/10/2026 (chantier B6) : un disque qui n'a pas pu corriger des erreurs perd déjà des données ou
    /// est en train d'en perdre. Rien n'est dit à zéro ni quand le compteur n'est pas publié.</summary>
    public static RepairFinding? JugerErreurs(UsureDisque d) => d.ErreursNonCorrigees is > 0 and { } n
        ? new RepairFinding("STOCKAGE", $"erreurs non corrigées · {d.Disque}", n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "0", RepairStatus.Suspicious,
            $"ce disque a rencontré {n} erreur(s) de lecture ou d'écriture qu'il n'a pas pu corriger : une partie de ce qu'il "
            + "contient peut être abîmée. Fais une copie de ce qui compte sur un autre disque, puis lance « chkdsk » ou "
            + "l'outil du fabricant.",
            RepairAction.Recommandation)
        : null;

    /// <summary>Un compteur absent rend <c>null</c>, jamais zero : sur ce NVMe, `PowerOnHours` et
    /// `ReadErrorsTotal` sont vides, et les rendre a zero ferait croire a un disque neuf.</summary>
    private static int? Entier(WmiRow row, string nom)
    {
        var brut = row.GetString(nom);
        return int.TryParse(brut, out var v) ? v : null;
    }
}
