using System.Diagnostics;
using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Bios;
using DrDxt.Detection.Display;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Settings;
using Microsoft.Win32;

namespace DrDxt.Detection.Repair;

/// <summary>Démarrage et BIOS : les options de démarrage lues par bcdedit et l'identité du BIOS.</summary>
public sealed partial class RepairAuditService
{
    private IEnumerable<RepairFinding> Bcd() => JugerBcd(LireBcd(), _eleve());

    // Sans droits administrateur, bcdedit refuse ; la famille se taisait, et l'écran de
    // récupération coupé disparaissait. Sortie non lue (null) : « non lu », avec sa cause.
    internal static IEnumerable<RepairFinding> JugerBcd(string? sortie, bool eleve)
    {
        if (sortie is null)
            return [new RepairFinding("BCD", "configuration de démarrage", null, null, RepairStatus.Unknown,
                Docteur.T("RA.Bcd.NonLue") + (eleve ? "" : " " + SansDroits))];
        string[] interessantes =
        [
            "bootstatuspolicy", "hypervisorlaunchtype", "disabledynamictick",
            "useplatformclock", "useplatformtick", "tscsyncpolicy", "increaseuserva",
        ];
        return sortie.Split('\n')
            .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(m => m.Length >= 2 && interessantes.Contains(m[0].ToLowerInvariant()))
            .Select(m => BcdInterpretation.Juger(m[0].ToLowerInvariant(), string.Join(' ', m[1..])))
            .ToList();
    }

    /// <summary>Les entrees BCD que le produit sait interpreter, plus celles qui portent une
    /// signature connue d'optimiseur. On ne liste pas tout : un audit qui rend quarante lignes
    /// de BCD sans savoir les juger fabrique de l'inquietude, pas de l'information.</summary>
    private static string? LireBcd()
    {
        try
        {
            // System32, pas le nom nu (ordre de recherche de CreateProcess, voir Win32ServiceActions).
            var psi = new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "bcdedit.exe"), "/enum {current}")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            var r = DrDxt.Core.ProcessusBorne.Executer(psi, TimeSpan.FromSeconds(5));
            // Un refus (accès refusé) sort avec un code non nul : ce n'est pas « aucune entrée ».
            return r.Fini && r.Code == 0 ? r.Sortie : null;
        }
        catch { return null; }
    }

    // ── BIOS : identite et interface constructeur, en lecture pure ─────────────────────────
    // La matrice de qualification (`BiosQualificationMatrix`) reste dans l'outil : elle vit dans
    // `DrDxt.Bios`, que ce projet ne reference pas, et l'ajouter ici pour une lecture creerait la
    // dependance a l'envers.
    private IEnumerable<RepairFinding> BiosIdentite()
    {
        var carte = WmiFixe(RequeteCarte, "Product");
        var fabricant = WmiFixe(RequeteCarte, "Manufacturer");
        var versionBios = WmiFixe(RequeteBios, "SMBIOSBIOSVersion");
        yield return new RepairFinding("BIOS", "carte mere / firmware",
            (fabricant ?? "?") + " " + (carte ?? "?") + ", BIOS " + (versionBios ?? "?"), null,
            RepairStatus.Observed,
            Docteur.T("RA.Bios.Identite"));

        foreach (var iface in new VendorBiosInterfaceProbe().Chercher())
            yield return new RepairFinding("BIOS", "interface constructeur",
                // « lecture et écriture » seul laissait croire que DrDXT écrit le BIOS : c'est ce que
                // le constructeur déclare, pas ce que DrDXT fait (il ne l'appelle jamais pour écrire).
                iface.Classe + Docteur.T(iface.SaitEcrire ? "RA.Bios.InterfaceEcrit" : "RA.Bios.InterfaceLecture"), null,
                RepairStatus.Observed, VendorBiosInterfaceProbe.Constat(iface));
    }
}
