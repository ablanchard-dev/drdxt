using System.Diagnostics;
using System.Text;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

/// <summary>
/// `DISM /Online /Cleanup-Image /CheckHealth`, en lecture : il dit si le magasin de
/// composants est sain, reparable ou non reparable, sans rien reparer (Microsoft, « Repair a Windows
/// Image »). Mesure sur la machine de reference : 0,2 s, « The component store is repairable ».
///
/// DISM est lance depuis System32 par son chemin (jamais par le PATH), en anglais (`/English`) pour
/// que la phrase se lise a l'identique sur tout Windows. Une sortie non reconnue est un echec : on
/// ne devine pas un etat de sante.
/// </summary>
public static class SanteDesComposants
{
    public const string Sain = "sain";
    public const string Reparable = "reparable";
    public const string NonReparable = "non-reparable";

    private static string Dism => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");

    /// <summary>L'etat lu dans la sortie anglaise de DISM, ou <c>null</c> si la phrase n'est pas reconnue.</summary>
    public static string? Interpreter(string sortie) =>
        sortie.Contains("No component store corruption detected", StringComparison.OrdinalIgnoreCase) ? Sain
        : sortie.Contains("The component store is repairable", StringComparison.OrdinalIgnoreCase) ? Reparable
        : sortie.Contains("cannot be repaired", StringComparison.OrdinalIgnoreCase) ? NonReparable
        : null;

    public static Result<byte[]> Lire()
    {
        try
        {
            var psi = new ProcessStartInfo(Dism)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "/Online", "/Cleanup-Image", "/CheckHealth", "/English" }) psi.ArgumentList.Add(a);
            var r = ProcessusBorne.Executer(psi, TimeSpan.FromSeconds(120));
            if (!r.Lance) return Result<byte[]>.Fail(ErrorCodes.HelperOpFailed, "dism.exe could not be started.");
            if (!r.Fini) return Result<byte[]>.Fail(ErrorCodes.HelperOpFailed, "dism.exe did not return in time.");
            return Interpreter(r.Sortie) is { } etat
                ? Result<byte[]>.Ok(Encoding.UTF8.GetBytes(etat))
                : Result<byte[]>.Fail(ErrorCodes.HelperOpFailed, $"DISM output not recognised (exit {r.Code}).");
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Fail(ErrorCodes.HelperOpFailed, $"DISM CheckHealth failed: {ex.Message}");
        }
    }
}
