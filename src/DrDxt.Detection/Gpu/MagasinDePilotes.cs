using System.Diagnostics;
using System.Xml.Linq;
using DrDxt.Core;

namespace DrDxt.Detection.Gpu;

/// <summary>
/// Les paquets de pilote d'affichage gardés par Windows et les
/// cartes graphiques déconnectées qui restent déclarées (une ancienne carte, un pilote mal désinstallé). Lu par
/// <c>pnputil /format xml</c> (sortie relevée sur la machine le 03/10/2026, indépendante de la langue de Windows).
/// Lecture seule : DrDXT ne retire rien, il nomme le paquet en service et ceux qui restent.
/// </summary>
public static class MagasinDePilotes
{
    public sealed record Paquet(string Nom, string Fournisseur, string Version);

    internal static IReadOnlyList<Paquet> Paquets(string xml) =>
        Racine(xml)?.Elements("Driver").Select(d => new Paquet(
            (string?)d.Attribute("DriverName") ?? "?",
            (string?)d.Element("ProviderName") ?? "?",
            ((string?)d.Element("DriverVersion") ?? "").Split(' ').LastOrDefault() ?? "")).ToList() ?? [];

    internal static IReadOnlyList<(string Description, string? Pilote)> Cartes(string xml, bool seulementPci = false) =>
        Racine(xml)?.Elements("Device")
            .Where(d => !seulementPci || ((string?)d.Attribute("InstanceId") ?? "").StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase))
            .Select(d => ((string?)d.Element("DeviceDescription") ?? "?", (string?)d.Element("DriverName")))
            .ToList() ?? [];

    private static XElement? Racine(string xml)
    {
        try { return XDocument.Parse(xml).Root; }
        catch { return null; }
    }

    private const string NomInstalles = "pilotes d'affichage installés";

    public static IEnumerable<RepairFinding> Juger(string pilotesXml, string cartesXml, string deconnecteesXml)
    {
        var paquets = Paquets(pilotesXml);
        var cartes = Cartes(cartesXml);
        var enService = cartes.Select(c => c.Pilote).Where(p => p is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // La classe Affichage déconnectée contient aussi des cartes virtuelles (Bureau à distance « SWD\\ »,
        // Parsec ou écrans virtuels « ROOT\\ ») ; seule une carte sur le bus PCI est une ancienne carte.
        var restes = Cartes(deconnecteesXml, seulementPci: true);
        if (paquets.Count == 0) yield break;

        // Une carte branchée dont le pilote n'est pas lu (pnputil rend "" sur toute erreur, ou une ligne sans DriverName) :
        // on ne sait pas lequel est en service, donc aucun n'est dit inutile. Sans cette garde, le paquet en service de la carte
        // non lue sortait avec sa commande de suppression.
        if (enService.Count == 0 || cartes.Any(c => c.Pilote is null))
        {
            yield return new RepairFinding("GPU DRIVER", NomInstalles,
                string.Join(", ", paquets.Select(p => $"{p.Fournisseur} {p.Version} ({p.Nom})")), null, RepairStatus.Unknown,
                Docteur.T("MP.CartesNonLues"));
            yield break;
        }

        var inutiles = paquets.Where(p => !enService.Contains(p.Nom)).ToList();
        var lu = string.Join(", ", paquets.Select(p => $"{p.Fournisseur} {p.Version} ({p.Nom})"));
        if (inutiles.Count == 0)
            yield return new RepairFinding("GPU DRIVER", NomInstalles, lu, null, RepairStatus.ExpectedDefault,
                paquets.Count == 1
                    ? Docteur.T("MP.UnSeul")
                    : Docteur.T("MP.ChacunSert"));
        else
            // La valeur listait aussi le paquet en service sous le titre « anciens pilotes ».
            yield return new RepairFinding("GPU DRIVER", "anciens pilotes d'affichage gardés",
                string.Join(", ", inutiles.Select(p => $"{p.Fournisseur} {p.Version} ({p.Nom})")), null, RepairStatus.Suspicious,
                Docteur.T("MP.Inutiles", inutiles.Count, string.Join(", ", inutiles.Select(p => $"{p.Fournisseur} {p.Version} ({p.Nom})"))),
                RepairAction.Recommandation,
                // Un paquet encore lié à une carte déconnectée refuse de partir ; l'ordre des gestes est dit.
                Remede: Docteur.T(restes.Count > 0 ? "MP.DAbord" : "MP.Remede",
                    string.Join(Docteur.T("MP.Puis"), inutiles.Select(p => $"pnputil /delete-driver {p.Nom}"))),
                Cible: string.Join(" ", inutiles.Select(p => p.Nom)));

        if (restes.Count > 0)
            yield return new RepairFinding("GPU DRIVER", "cartes graphiques déconnectées", $"{restes.Count}", "0", RepairStatus.Suspicious,
                Docteur.T("MP.Deconnectees", restes.Count, string.Join(", ", restes.Select(r => r.Description))),
                Remede: Docteur.T("MP.Deconnectees.Remede", string.Join(", ", restes.Select(r => r.Description))));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static System.Text.Encoding EncodageOem()
    {
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding((int)GetOEMCP());
        }
        catch { return System.Text.Encoding.UTF8; }
    }

    /// <summary>Lance pnputil depuis System32 (jamais le nom nu) et rend sa sortie XML, ou une chaîne vide.</summary>
    public static string Pnputil(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe"),
                arguments + " /format xml")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                // Relevé sur la machine : pnputil annonce « utf-8 » dans son XML mais écrit dans la page de code OEM
                // de la console (« carte m\uFFFDre » lu en UTF-8).
                StandardOutputEncoding = EncodageOem(),
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "";
            var sortie = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);
            return sortie;
        }
        catch { return ""; }
    }
}
