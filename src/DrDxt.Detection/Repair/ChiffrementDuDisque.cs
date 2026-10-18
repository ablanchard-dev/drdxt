using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Le chiffrement BitLocker du disque qui porte les jeux, lu sans rien changer : deux cas comptent. Une protection
/// suspendue paie le coût du chiffrement sans la sécurité (la clé est laissée en clair sur le disque) ; une conversion en
/// cours occupe le disque, et toute mesure faite pendant mesure la conversion. Le geste qui réarme la protection est dit,
/// jamais fait à la place du joueur ; déchiffrer n'est jamais proposé.
/// Sources : Microsoft Learn, « Win32_EncryptableVolume class » (ProtectionStatus 0 sans protection, 1 protégé, 2 inconnu ;
/// GetConversionStatus : 0 déchiffré, 1 chiffré, 2 chiffrement en cours, 3 déchiffrement en cours, 4 et 5 en pause
/// (rien ne tourne, le disque n'est pas occupé),
/// EncryptionPercentage ; GetEncryptionMethod : 5 chiffrement matériel du disque). La classe n'est lisible qu'en
/// administrateur : sans droits, « non lu ».
/// </summary>
public static class ChiffrementDuDisque
{
    public sealed record Volume(string Lettre, int? Protection, int? Conversion, int? Methode, int? Pourcent);

    /// <summary>Le vrai, en lecture seule ; <c>null</c> = classe refusée (sans droits) ou absente (BitLocker non installé).</summary>
    public static IReadOnlyList<Volume>? Lire()
    {
        try
        {
            var volumes = new List<Volume>();
            using var s = new System.Management.ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
                "SELECT * FROM Win32_EncryptableVolume");
            using var rangees = s.Get();
            foreach (var o in rangees.Cast<System.Management.ManagementObject>())
                using (o)
                {
                    if (o["DriveLetter"]?.ToString() is not { Length: > 0 } lettre) continue;
                    static int? Entier(object? v) => v is null ? null : Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
                    int? conversion = null, pourcent = null;
                    try
                    {
                        using var r = o.InvokeMethod("GetConversionStatus", null, null);
                        conversion = Entier(r?["ConversionStatus"]);
                        pourcent = Entier(r?["EncryptionPercentage"]);
                    }
                    catch (Exception) { /* état de conversion non lu : il reste null */ }
                    volumes.Add(new Volume(lettre.TrimEnd(':'), Entier(o["ProtectionStatus"]), conversion, Entier(o["EncryptionMethod"]), pourcent));
                }
            return volumes;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Pur : une conversion en cours sur un des volumes lus ; une conversion en pause n'occupe pas le disque.</summary>
    public static bool? ConversionEnCours(IReadOnlyList<Volume>? volumes)
        => volumes is null ? null : volumes.Any(v => v.Conversion is 2 or 3);

    /// <summary>Pur. Un constat par volume qui porte des jeux (lettre).</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<Volume>? volumes, IReadOnlyList<string> lettresDesJeux)
    {
        foreach (var l in lettresDesJeux.Select(x => x.TrimEnd(':').ToUpperInvariant()).Distinct())
        {
            var nom = Docteur.Nom("BL.Nom", l);
            var v = volumes?.FirstOrDefault(x => string.Equals(x.Lettre, l, StringComparison.OrdinalIgnoreCase));
            if (volumes is null)
                yield return new RepairFinding("SECURITE", nom, null, null, RepairStatus.Unknown, Docteur.T("BL.NonLu"));
            else if (v is null || v.Conversion == 0)
                yield return new RepairFinding("SECURITE", nom, Docteur.T("BL.Clair"), null, RepairStatus.ExpectedDefault, Docteur.T("BL.ClairExplication"));
            else if (v.Conversion is 2 or 3)
                yield return new RepairFinding("SECURITE", nom, Docteur.T("BL.Conversion", Pourcent(v)),
                    null, RepairStatus.Suspicious, Docteur.T("BL.ConversionExplication"), RepairAction.Recommandation, Remede: Docteur.T("BL.ConversionRemede"));
            else if (v.Conversion is 4 or 5)
                yield return new RepairFinding("SECURITE", nom, Docteur.T("BL.Pause", Pourcent(v)), null, RepairStatus.Observed, Docteur.T("BL.PauseExplication"));
            // Conversion non lue : un volume en clair rend aussi ProtectionStatus 0, ce n'est pas une suspension.
            else if (v.Conversion is null)
                yield return new RepairFinding("SECURITE", nom, null, null, RepairStatus.Unknown, Docteur.T("BL.EtatNonLu"));
            else if (v.Protection == 0)
                yield return new RepairFinding("SECURITE", nom, Docteur.T("BL.Suspendu"), Docteur.T("BL.Protege"), RepairStatus.Suspicious,
                    Docteur.T("BL.SuspenduExplication"), RepairAction.Recommandation, Remede: Docteur.T("BL.SuspenduRemede", l));
            else if (v.Protection is null or 2)
                yield return new RepairFinding("SECURITE", nom, null, null, RepairStatus.Unknown, Docteur.T("BL.EtatNonLu"));
            else
                yield return new RepairFinding("SECURITE", nom, Docteur.T(v.Methode == 5 ? "BL.Materiel" : "BL.Logiciel"), null,
                    RepairStatus.Observed, Docteur.T("BL.ProtegeExplication"));
        }
    }

    private static string Pourcent(Volume v) => v.Pourcent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
}
