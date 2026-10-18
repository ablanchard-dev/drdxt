using System.Buffers.Binary;
using System.Text;

namespace DrDxt.Bios;

/// <summary>
/// Décode le magasin des valeurs d'usine d'un firmware AMI (la variable « StdDefaults ») : une suite d'enregistrements
/// NVAR, chacun avec son nom, son GUID et ses octets. Sert à dire quels réglages du BIOS ne sont plus à leur valeur
/// d'usine. Le format est celui que documente UEFITool (github.com/LongSoft/UEFITool, commit dac91b26,
/// common/nvram.h et common/ksy/ami_nvar.ksy) ; il a été relu le 17/10/2026 sur la carte de référence (AMI 1.H0) :
/// 24 enregistrements, « Setup » et « CpuSetup » aux tailles des variables vivantes. Lecture seule, pur.
/// </summary>
public static class NvarAmi
{
    public const string NomMagasin = "StdDefaults";

    /// <summary>Un enregistrement décodé : la dernière donnée de sa chaîne, sans son en-tête étendu.</summary>
    public sealed record Entree(string Nom, Guid Guid, byte[] Donnees);

    // En-tête NVAR (nvram.h, NVAR_ENTRY_HEADER) : signature, taille u16 (en-tête compris), puis 24 bits de lien vers
    // l'enregistrement suivant de la chaîne et 8 bits d'attributs.
    private const int TailleEnTete = 10;
    private const int SansLien = 0xFFFFFF;
    private const byte Valide = 0x80, EnTeteEtendu = 0x10, DonneesSeules = 0x08, GuidLocal = 0x04, NomAscii = 0x02;

    /// <summary>
    /// Les enregistrements valides du magasin, ou null si le magasin ne suit pas le format (signature cassée, taille
    /// qui déborde, lien qui ne mène nulle part). On parcourt les en-têtes un par un : chercher la chaîne « Setup »
    /// dans les octets trouverait aussi la fin de « AmdSetup ».
    /// </summary>
    public static IReadOnlyList<Entree>? Decoder(ReadOnlySpan<byte> magasin)
    {
        var brutes = new List<(int Pos, int Lien, byte Attributs, string? Nom, int? IndexGuid, Guid? Guid, int Debut, int Fin)>();
        var pos = 0;
        // Le magasin s'arrête au premier octet qui n'ouvre pas un « NVAR » (ami_nvar.ksy, repeat-until) : l'espace libre
        // (0xFF), puis la table des GUID, rangée depuis la fin.
        while (pos + TailleEnTete <= magasin.Length && magasin[pos] == (byte)'N')
        {
            if (!magasin.Slice(pos, 4).SequenceEqual("NVAR"u8)) return null;
            int taille = BinaryPrimitives.ReadUInt16LittleEndian(magasin[(pos + 4)..]);
            var mot = BinaryPrimitives.ReadUInt32LittleEndian(magasin[(pos + 6)..]);
            if (taille <= TailleEnTete || pos + taille > magasin.Length) return null;
            int lien = (int)(mot & 0xFFFFFF);
            var attributs = (byte)(mot >> 24);
            var fin = pos + taille;
            var debut = pos + TailleEnTete;
            string? nom = null;
            int? indexGuid = null;
            Guid? guid = null;
            if ((attributs & Valide) != 0 && (attributs & DonneesSeules) == 0)
            {
                if ((attributs & GuidLocal) != 0)
                {
                    if (debut + 16 > fin) return null;
                    guid = new Guid(magasin.Slice(debut, 16));
                    debut += 16;
                }
                else
                {
                    if (debut + 1 > fin) return null;
                    indexGuid = magasin[debut++];
                }
                if (!LireNom(magasin[debut..fin], (attributs & NomAscii) != 0, out nom, out var lus)) return null;
                debut += lus;
            }
            // L'en-tête étendu finit l'enregistrement ; ses deux derniers octets disent sa taille, qui ne compte que si
            // elle couvre au moins l'octet d'attributs étendus et ce champ (ami_nvar.ksy, extended_header_size).
            if ((attributs & Valide) != 0 && (attributs & EnTeteEtendu) != 0 && taille > TailleEnTete + 2)
            {
                int etendu = BinaryPrimitives.ReadUInt16LittleEndian(magasin[(fin - 2)..]);
                if (etendu >= 3) fin -= etendu;
                if (fin < debut) return null;
            }
            brutes.Add((pos, lien, attributs, nom, indexGuid, guid, debut, fin));
            pos += taille;
        }

        var finDesEnregistrements = pos;
        var parPosition = brutes.ToDictionary(b => b.Pos);
        var entrees = new List<Entree>();
        foreach (var b in brutes.Where(b => (b.Attributs & Valide) != 0 && (b.Attributs & DonneesSeules) == 0))
        {
            Guid guid;
            if (b.Guid is { } g) guid = g;
            else
            {
                // La table des GUID part de la fin du magasin et remonte (nvramparser.cpp) ; elle ne peut pas mordre sur
                // les enregistrements.
                var ou = magasin.Length - 16 * (b.IndexGuid!.Value + 1);
                if (ou < finDesEnregistrements) return null;
                guid = new Guid(magasin.Slice(ou, 16));
            }
            // Un enregistrement lié renvoie à ses données les plus récentes : on suit la chaîne jusqu'au dernier maillon.
            var maillon = b;
            for (var pas = 0; maillon.Lien != SansLien; pas++)
            {
                if (pas > brutes.Count || !parPosition.TryGetValue(maillon.Pos + maillon.Lien, out var suivant)
                    || (suivant.Attributs & Valide) == 0 || (suivant.Attributs & DonneesSeules) == 0)
                    return null;
                maillon = suivant;
            }
            entrees.Add(new Entree(b.Nom!, guid, magasin[maillon.Debut..maillon.Fin].ToArray()));
        }
        return entrees;
    }

    /// <summary>Les données de l'enregistrement de ce nom et de ce GUID, exactement ; null s'il manque ou s'il est en
    /// double (un magasin qui porte deux fois la même variable ne dit pas laquelle est l'usine).</summary>
    public static byte[]? Trouver(IReadOnlyList<Entree> entrees, string nom, string guid)
    {
        if (!Guid.TryParse(guid, out var g)) return null;
        var trouves = entrees.Where(e => e.Nom == nom && e.Guid == g).ToList();
        return trouves.Count == 1 ? trouves[0].Donnees : null;
    }

    private static bool LireNom(ReadOnlySpan<byte> reste, bool ascii, out string? nom, out int lus)
    {
        nom = null;
        lus = 0;
        if (ascii)
        {
            var z = reste.IndexOf((byte)0);
            if (z < 0) return false;
            nom = Encoding.ASCII.GetString(reste[..z]);
            lus = z + 1;
            return true;
        }
        for (var i = 0; i + 1 < reste.Length; i += 2)
            if (reste[i] == 0 && reste[i + 1] == 0)
            {
                nom = Encoding.Unicode.GetString(reste[..i]);
                lus = i + 2;
                return true;
            }
        return false;
    }
}
