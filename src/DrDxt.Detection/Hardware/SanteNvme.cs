using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using DrDxt.Core;
using Microsoft.Win32.SafeHandles;
using static DrDxt.Detection.Natif;

namespace DrDxt.Detection.Hardware;

/// <summary>
/// Ce que le SSD NVMe compte lui-même (page de journal 02h « SMART / Health Information », 512 octets), lu par
/// IOCTL_STORAGE_QUERY_PROPERTY avec la requête propre au protocole NVMe, sans écrire ni rien envoyer d'autre au disque.
/// Sources : NVM Express Base Specification 2.0, figure « SMART / Health Information Log Page » (offsets ci-dessous) ;
/// Microsoft Learn, « Working with NVMe drives » (StorageDeviceProtocolSpecificProperty, ProtocolTypeNvme,
/// NVMeDataTypeLogPage, STORAGE_PROTOCOL_DATA_DESCRIPTOR).
/// Les compteurs de 128 bits sont lus en entier (UInt128), jamais tronqués.
/// </summary>
public sealed record SanteNvmeLue(string Disque, byte AvertissementCritique, int TemperatureKelvin, byte ReservePourcent,
    byte SeuilReservePourcent, byte UsurePourcent, UInt128 UnitesLues, UInt128 UnitesEcrites, UInt128 Cycles, UInt128 Heures,
    UInt128 ArretsDangereux, UInt128 ErreursMedia, uint MinutesTemperatureHaute, uint MinutesTemperatureCritique,
    uint SecondesBridage1, uint SecondesBridage2)
{
    /// <summary>Pur : la page 02h décodée ; <c>null</c> si elle fait moins de 512 octets.</summary>
    public static SanteNvmeLue? Decoder(string disque, byte[] p)
    {
        if (p.Length < 512) return null;
        UInt128 U128(int o) => BinaryPrimitives.ReadUInt128LittleEndian(p.AsSpan(o, 16));
        uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(o, 4));
        return new SanteNvmeLue(disque, p[0], BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(1, 2)), p[3], p[4], p[5],
            U128(32), U128(48), U128(112), U128(128), U128(144), U128(160), U32(192), U32(196), U32(224), U32(228));
    }
}

[SupportedOSPlatform("windows")]
public static class SanteNvme
{
    /// <summary>Le relevé gardé d'un disque : le compte d'arrêts dangereux, quand il a été lu, et le dernier scan qui l'a vu monter.</summary>
    public sealed record Reference(string Disque, string ArretsDangereux, DateTimeOffset Quand, DateTimeOffset? DerniereHausse);

    public const string Fichier = "sante-nvme.json";

    /// <summary>Les SSD NVMe de la machine (MSFT_PhysicalDisk, BusType 17) et leur page 02h ; une page non lue reste <c>null</c>.
    /// <c>null</c> en entier si la liste des disques n'a pas pu être lue.</summary>
    public static IReadOnlyList<(string Disque, SanteNvmeLue? Sante)>? Lire() => LectureWmiBornee.Lire<IReadOnlyList<(string Disque, SanteNvmeLue? Sante)>?>(() =>
    {
        try
        {
            var lus = new List<(string Nom, string? Id)>();
            using var s = new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, FriendlyName FROM MSFT_PhysicalDisk WHERE BusType = 17");
            using var rangees = s.Get();
            foreach (var o in rangees.Cast<System.Management.ManagementObject>())
                using (o)
                    lus.Add((o["FriendlyName"]?.ToString() ?? "NVMe", o["DeviceId"]?.ToString()));
            return Nommer(lus).Zip(lus, (nom, l) => (nom, int.TryParse(l.Id, out var n) ? LirePage(n, nom) : null)).ToList();
        }
        catch (Exception) { return null; }
    }, () => null);

    /// <summary>Pur : le nom qui retrouve le relevé d'un disque. Deux SSD du même modèle portent le même nom : le numéro
    /// du disque les sépare, sinon le compteur de l'un serait comparé à celui de l'autre. Jamais traduit : c'est la clé du
    /// relevé gardé, elle ne doit pas changer avec la langue.</summary>
    internal static IReadOnlyList<string> Nommer(IReadOnlyList<(string Nom, string? Id)> disques)
        => disques.Select(d => disques.Count(x => x.Nom == d.Nom) > 1 ? $"{d.Nom} #{d.Id ?? "?"}" : d.Nom).ToList();

    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const int StorageDeviceProtocolSpecificProperty = 50, ProtocolTypeNvme = 3, NVMeDataTypeLogPage = 2, PageSante = 2;
    private const int Entete = 8, Donnees = 40, Taille = 512;

    private static SanteNvmeLue? LirePage(int numero, string nom)
    {
        // Sans droit d'accès d'abord (la requête est FILE_ANY_ACCESS), puis en lecture seule ; jamais en écriture.
        foreach (var acces in new[] { 0u, 0x80000000u })
        {
            using var h = CreateFileW($@"\\.\PhysicalDrive{numero}", acces, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            var tampon = new byte[Entete + Donnees + Taille];
            BinaryPrimitives.WriteInt32LittleEndian(tampon.AsSpan(0), StorageDeviceProtocolSpecificProperty);
            BinaryPrimitives.WriteInt32LittleEndian(tampon.AsSpan(4), 0); // PropertyStandardQuery
            BinaryPrimitives.WriteInt32LittleEndian(tampon.AsSpan(Entete + 0), ProtocolTypeNvme);
            BinaryPrimitives.WriteInt32LittleEndian(tampon.AsSpan(Entete + 4), NVMeDataTypeLogPage);
            BinaryPrimitives.WriteInt32LittleEndian(tampon.AsSpan(Entete + 8), PageSante);
            BinaryPrimitives.WriteInt32LittleEndian(tampon.AsSpan(Entete + 16), Donnees);
            BinaryPrimitives.WriteInt32LittleEndian(tampon.AsSpan(Entete + 20), Taille);
            if (!DeviceIoControl(h, IoctlStorageQueryProperty, tampon, tampon.Length, tampon, tampon.Length, out _, IntPtr.Zero)) continue;
            if (Extraire(tampon) is { } page) return SanteNvmeLue.Decoder(nom, page);
        }
        return null;
    }

    /// <summary>Pur : la page rendue dans le tampon de réponse ; <c>null</c> si la réponse n'est pas celle demandée ou si son
    /// décalage sort du tampon (Microsoft Learn, « Working with NVMe drives » : ProtocolDataOffset au moins la taille de
    /// STORAGE_PROTOCOL_SPECIFIC_DATA, ProtocolDataLength au moins la page).</summary>
    internal static byte[]? Extraire(byte[] tampon)
    {
        // STORAGE_PROTOCOL_DATA_DESCRIPTOR : Version et Size valent sa taille, sinon la réponse n'est pas celle demandée.
        if (tampon.Length < Entete + Donnees || BinaryPrimitives.ReadInt32LittleEndian(tampon) != Entete + Donnees) return null;
        var decalage = BinaryPrimitives.ReadInt32LittleEndian(tampon.AsSpan(Entete + 16));
        var longueur = BinaryPrimitives.ReadInt32LittleEndian(tampon.AsSpan(Entete + 20));
        if (decalage < Donnees || longueur < Taille || (long)Entete + decalage + Taille > tampon.Length) return null;
        return tampon[(Entete + decalage)..(Entete + decalage + Taille)];
    }

    /// <summary>Les dates des scans qui ont vu monter un compteur d'arrêts dangereux, lues sans rien écrire (garde de stabilité).</summary>
    public static IReadOnlyList<DateTimeOffset> Hausses(string fichier)
        => LireReferences(fichier)?.Select(r => r.DerniereHausse).OfType<DateTimeOffset>().ToList() ?? [];

    private static List<Reference>? LireReferences(string fichier)
    {
        // Un JSON valide peut porter des entrées vides ([null], [{}]) : elles sont écartées, sans quoi le scan de santé
        // entier tomberait sur la première.
        try { return File.Exists(fichier) ? JsonSerializer.Deserialize<List<Reference?>>(File.ReadAllText(fichier))?.OfType<Reference>()
                .Where(r => r is { Disque: not null, ArretsDangereux: not null }).ToList() : null; }
        catch (Exception) { return null; }
    }

    /// <summary>Lit les disques, juge contre le relevé précédent et les arrêts brutaux de Windows lus depuis
    /// <paramref name="arretsDepuis"/>, puis garde l'actuel.</summary>
    public static IReadOnlyList<RepairFinding> ComparerEtGarder(string fichier, IReadOnlyList<DateTimeOffset>? arretsBrutaux,
        DateTimeOffset arretsDepuis)
    {
        var lus = Lire();
        var precedents = LireReferences(fichier) ?? [];
        var maintenant = DateTimeOffset.Now;
        var constats = Juger(lus, precedents, arretsBrutaux, maintenant, arretsDepuis).ToList();
        if (lus is null) return constats;
        new FichierJsonAtomique(fichier, ErrorCodes.StorageFail).WriteAtomic(JsonSerializer.Serialize(Garder(lus, precedents, maintenant)));
        return constats;
    }

    /// <summary>Pur : le relevé à garder. Un disque lu remplace le sien ; un disque non lu ou absent garde le précédent tel
    /// quel, date de la dernière hausse comprise : un scan raté n'efface pas la trace d'une coupure pour la garde.</summary>
    internal static List<Reference> Garder(IReadOnlyList<(string Disque, SanteNvmeLue? Sante)> lus, IReadOnlyList<Reference> precedents,
        DateTimeOffset maintenant)
    {
        var gardes = lus.Where(l => l.Sante is not null).Select(l =>
        {
            var avant = precedents.FirstOrDefault(p => p.Disque == l.Disque);
            var hausse = avant is not null && UInt128.TryParse(avant.ArretsDangereux, CultureInfo.InvariantCulture, out var a) && l.Sante!.ArretsDangereux > a
                ? maintenant : avant?.DerniereHausse;
            return new Reference(l.Disque, l.Sante!.ArretsDangereux.ToString(CultureInfo.InvariantCulture), maintenant, hausse);
        }).ToList();
        gardes.AddRange(precedents.Where(p => gardes.All(g => g.Disque != p.Disque)));
        return gardes;
    }

    /// <summary>Pur. Par disque : l'état que le SSD déclare, puis les arrêts dangereux depuis le scan précédent croisés avec
    /// les arrêts brutaux de Windows sur la même période. <paramref name="arretsDepuis"/> : le début de la fenêtre lue des
    /// arrêts brutaux ; un relevé plus ancien ne se croise pas, la fenêtre ne couvre pas la période.</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<(string Disque, SanteNvmeLue? Sante)>? lus, IReadOnlyList<Reference> precedents,
        IReadOnlyList<DateTimeOffset>? arretsBrutaux, DateTimeOffset maintenant, DateTimeOffset? arretsDepuis = null)
    {
        if (lus is null)
        {
            yield return new RepairFinding("STOCKAGE", "sante NVMe", null, null, RepairStatus.Unknown, Docteur.T("NV.ListeNonLue"));
            yield break;
        }
        foreach (var (disque, s) in lus)
        {
            var nom = Docteur.Nom("NV.Nom", disque);
            if (s is null)
            {
                yield return new RepairFinding("STOCKAGE", nom, null, null, RepairStatus.Unknown, Docteur.T("NV.NonLu"));
                continue;
            }
            // 0 kelvin : le disque ne renseigne pas sa température, jamais « −273 °C ».
            object celsius = s.TemperatureKelvin > 0 ? s.TemperatureKelvin - 273 : Docteur.T("NV.TemperatureNonLue");
            var valeur = Docteur.T("NV.Valeur", s.UsurePourcent, s.ReservePourcent, celsius, s.Heures, s.Cycles, s.ArretsDangereux, s.ErreursMedia);
            var alertes = new List<string>();
            if (s.AvertissementCritique != 0) alertes.Add(Docteur.T("NV.Critique", Avertissements(s.AvertissementCritique)));
            if (s.ReservePourcent < s.SeuilReservePourcent) alertes.Add(Docteur.T("NV.Reserve", s.ReservePourcent, s.SeuilReservePourcent));
            if (s.ErreursMedia > 0) alertes.Add(Docteur.T("NV.Media", s.ErreursMedia));
            var bridage = s.MinutesTemperatureHaute + s.MinutesTemperatureCritique > 0 || s.SecondesBridage1 + s.SecondesBridage2 > 0
                ? Docteur.T("NV.Bridage", s.MinutesTemperatureHaute, s.MinutesTemperatureCritique, (s.SecondesBridage1 + (ulong)s.SecondesBridage2) / 60) : "";

            var avant = precedents.FirstOrDefault(p => p.Disque == disque);
            var croisement = avant is null || !UInt128.TryParse(avant.ArretsDangereux, CultureInfo.InvariantCulture, out var a) || s.ArretsDangereux < a
                ? Docteur.T("NV.Reference")
                : Croiser(s.ArretsDangereux - a, arretsBrutaux?.Count(d => d > avant.Quand && d <= maintenant), avant.Quand,
                    couvert: arretsDepuis is not { } depuis || avant.Quand >= depuis);

            yield return new RepairFinding("STOCKAGE", nom, valeur, null,
                alertes.Count > 0 ? RepairStatus.Suspicious : RepairStatus.Observed,
                string.Concat(alertes) + croisement + bridage + Docteur.T("NV.Lecture"),
                alertes.Count > 0 ? RepairAction.Recommandation : RepairAction.Aucune,
                Remede: alertes.Count > 0 ? Docteur.T("NV.Remede") : null);
        }
    }

    // Un arrêt dangereux est une coupure d'alimentation que le SSD n'a pas vu venir ; un Kernel-Power 41 est un redémarrage
    // que Windows n'a pas vu venir. Les deux ensemble : coupure. L'un sans l'autre se dit, sans trancher.
    internal static string Croiser(UInt128 hausse, int? arrets, DateTimeOffset depuis, bool couvert = true)
    {
        var quand = depuis.ToLocalTime().ToString(Docteur.T("ST.FormatDateHeure"), Docteur.Langue);
        if (arrets is null) return Docteur.T("NV.HausseSansJournal", hausse, quand);
        // Zéro arrêt brutal sur une fenêtre plus courte que la période ne prouve rien : non lu, pas un verdict.
        if (!couvert) return Docteur.T("NV.JournalTropCourt", hausse, quand);
        return (hausse > 0, arrets > 0) switch
        {
            (true, true) => Docteur.T("NV.Coupure", hausse, arrets, quand),
            (true, false) => Docteur.T("NV.SansArret", hausse, quand),
            (false, true) => Docteur.T("NV.SansCoupure", arrets, quand),
            _ => Docteur.T("NV.Rien", quand),
        };
    }

    // Les bits de l'avertissement critique (NVMe Base 2.0, octet 0 de la page 02h).
    private static string Avertissements(byte b) => string.Join(", ", Enumerable.Range(0, 6)
        .Where(i => (b & (1 << i)) != 0).Select(i => Docteur.T("NV.Bit" + i.ToString(CultureInfo.InvariantCulture))));
}
