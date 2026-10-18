using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static DrDxt.Detection.Natif;

namespace DrDxt.Detection.Cpu;

/// <summary>Un processeur logique, tel que Windows le decrit.</summary>
/// <param name="CpuSetId">L'identifiant a passer a <c>SetThreadSelectedCpuSets</c>.</param>
/// <param name="Groupe">Le groupe de processeurs.</param>
/// <param name="IndexLogique">Son index dans le groupe.</param>
/// <param name="IndexCoeur">Le coeur physique : deux jumeaux SMT partagent cet index.</param>
/// <param name="ClasseEfficacite">La classe de performance relative. **Plus haut = plus rapide.**
/// C'est un ordre, pas un nom : Windows ne dit ni « P » ni « E ».</param>
/// <param name="Parque">Etat instantane. Il change en permanence selon la charge.</param>
public sealed record LogicalProcessor(
    uint CpuSetId,
    ushort Groupe,
    byte IndexLogique,
    byte IndexCoeur,
    byte ClasseEfficacite,
    bool Parque);

/// <summary>
/// La carte reelle des coeurs, sans jamais coder « CPU 0-15 = P-core ».
///
/// Un i7-14700F melange huit coeurs performants et douze coeurs efficaces. Un tableau statique
/// d'index serait faux sur la machine suivante, faux apres une mise a jour de firmware, et faux sur
/// un CPU AMD. Windows publie la reponse : <c>GetSystemCpuSetInformation</c> rend une
/// `EfficiencyClass` par processeur logique.
///
/// **`EfficiencyClass` est un ordre, pas un nom.** Windows ne dit ni « P-core » ni « E-core » :
/// il dit « cette classe est plus performante que celle-la ». Ce fichier respecte ca : il parle de
/// classe haute et basse, et laisse le vocabulaire marketing dehors. Sur un CPU homogene il n'y a
/// qu'une seule classe, et c'est une reponse valide.
///
/// `Parque` est un etat instantane, pas une configuration. Mesure du 08/09/2026 : trois lectures
/// a quelques secondes d'ecart rendaient « 19, 20 », « 20 », « 19, 20 », et « aucun » pendant que
/// le jeu tournait.
///
/// Échec propre : ne leve jamais ; une lecture impossible rend une liste vide, et l'appelant doit
/// traiter « vide » comme Unknown, jamais comme « un seul coeur ».
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CpuTopology
{
    // Disposition de SYSTEM_CPU_SET_INFORMATION, deja verifiee en direct dans ce depot
    // (voir CpuSetParsing) : 28 structures lues, jumeaux SMT parques par paires.
    private const int OffsetSize = 0;
    private const int OffsetType = 4;
    private const int OffsetId = 8;
    private const int OffsetGroup = 12;
    private const int OffsetLogicalProcessorIndex = 14;
    private const int OffsetCoreIndex = 15;
    private const int OffsetEfficiencyClass = 18;
    private const int OffsetAllFlags = 19;
    private const int TailleMinimale = OffsetAllFlags + 1;
    private const int TypeCpuSetInformation = 0;
    private const byte DrapeauParque = 0x01;
    private const uint OctetsMaximum = 1 << 20;

    /// <summary>Tous les processeurs logiques. Liste vide quand la lecture echoue, et « vide »
    /// veut dire Unknown, jamais « un seul coeur ».</summary>
    public IReadOnlyList<LogicalProcessor> Lire()
    {
        try
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out var requis, IntPtr.Zero, 0);
            if (requis == 0 || requis > OctetsMaximum) return Array.Empty<LogicalProcessor>();

            var tampon = Marshal.AllocHGlobal((int)requis);
            try
            {
                if (!GetSystemCpuSetInformation(tampon, requis, out var ecrit, IntPtr.Zero, 0))
                    return Array.Empty<LogicalProcessor>();

                var octets = new byte[Math.Min(ecrit, requis)];
                Marshal.Copy(tampon, octets, 0, octets.Length);
                return Decoder(octets);
            }
            finally { Marshal.FreeHGlobal(tampon); }
        }
        catch { return Array.Empty<LogicalProcessor>(); }
    }

    private static List<LogicalProcessor> Decoder(byte[] o)
    {
        var sortie = new List<LogicalProcessor>();
        var i = 0;
        while (i + TailleMinimale <= o.Length)
        {
            var taille = BitConverter.ToInt32(o, i + OffsetSize);
            // Une taille absurde arrete la lecture : on rend ce qui a ete lu, jamais hors bornes.
            if (taille < TailleMinimale || i + taille > o.Length) break;

            if (BitConverter.ToInt32(o, i + OffsetType) == TypeCpuSetInformation)
                sortie.Add(new LogicalProcessor(
                    BitConverter.ToUInt32(o, i + OffsetId),
                    BitConverter.ToUInt16(o, i + OffsetGroup),
                    o[i + OffsetLogicalProcessorIndex],
                    o[i + OffsetCoreIndex],
                    o[i + OffsetEfficiencyClass],
                    (o[i + OffsetAllFlags] & DrapeauParque) != 0));

            i += taille;
        }
        return sortie;
    }

    /// <summary>Les classes presentes, de la plus performante a la moins. Vide = Unknown ;
    /// une seule = CPU homogene, ce qui est une reponse valide et non un echec.</summary>
    public static IReadOnlyList<byte> ClassesParPerformance(IReadOnlyList<LogicalProcessor> carte) =>
        carte.Select(p => p.ClasseEfficacite).Distinct().OrderByDescending(x => x).ToList();

    /// <summary>Un processeur logique par cœur physique de la classe la plus rapide, groupe 0 seulement (hors du groupe 0
    /// le masque d'affinité change de sens). Sorti du câblage de l'App pour que le banc processeur et le
    /// relevé des parties lisent les mêmes cœurs ; sinon leurs fréquences ne se comparent pas.</summary>
    public static IReadOnlyList<int> CoeursRapides(IReadOnlyList<LogicalProcessor> carte)
    {
        if (carte.Count == 0) return [];
        var classes = ClassesParPerformance(carte);
        return carte
            .Where(x => x.ClasseEfficacite == classes[0] && x.Groupe == 0)
            .GroupBy(x => x.IndexCoeur)
            .Select(g => (int)g.OrderBy(x => x.IndexLogique).First().IndexLogique)
            .OrderBy(x => x)
            .ToList();
    }

    /// <summary>Le nom prudent d'une classe. On ne dit « P-core » ni « E-core » nulle part : la
    /// seule chose que Windows garantit est un ordre relatif.</summary>
    public static string NommerClasse(byte classe, IReadOnlyList<byte> classes)
    {
        if (classes.Count <= 1) return "classe unique (CPU homogene)";
        if (classe == classes[0]) return "classe HAUTE performance";
        if (classe == classes[^1]) return "classe BASSE performance";
        return $"classe intermediaire ({classe})";
    }

}
