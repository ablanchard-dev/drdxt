using System.Diagnostics;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// Win32NetworkQosActions : le plafond d'envoi QoS, par les cmdlets NetQos de Windows.
//
// Deux politiques, en ActiveStore (memoire : un redemarrage les efface, rien ne reste colle) :
//   DrDXT-upload-cap      tout TCP+UDP, envoi plafonne
//   DrDXT-upload-cap-jeu  l'exe du jeu, plafond hors d'atteinte : l'exe specifique l'emporte
//                         sur la politique generale (mesure 24/09 : 343 -> 839 Mbit/s).
//
// Les valeurs arrivent deja validees par OpAllowlist (nom d'exe nu par regex, plafond borne) :
// c'est ce qui rend leur interpolation dans le script sure.
// Simplification voulue : un powershell.exe par appel (~1 s). Passer par CIM (MSFT_NetQosPolicySettingData)
// si ce cout compte un jour.
// -----------------------------------------------------------------------
public sealed class Win32NetworkQosActions : INetworkQosActions
{
    public const string PolitiqueGenerale = "DrDXT-upload-cap";
    public const string PolitiqueJeu = "DrDXT-upload-cap-jeu";
    private const long PlafondExemption = 100_000_000_000; // 100 Gbit/s : jamais atteint

    // powershell.exe resolu depuis le dossier systeme, jamais depuis le PATH (meme regle que
    // shutdown.exe dans Win32FirmwareBootActions).
    private static string PowerShellExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        @"WindowsPowerShell\v1.0\powershell.exe");

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    // PowerShell 5.1 met toujours `Documents\WindowsPowerShell\Modules` en tete de
    // PSModulePath, meme lance eleve, et l'utilisateur y ecrit sans droits. Un faux module NetQos
    // pose la etait charge a la place de celui de Windows, donc execute en administrateur. On
    // importe le module systeme par son chemin, puis on refuse si une commande vient d'ailleurs.
    // Le chemin vient de l'API (GetFolderPath), pas de $env:SystemRoot que l'utilisateur peut fixer.
    internal static string ModuleSysteme(string system32) =>
        $"Import-Module -Name '{system32}\\WindowsPowerShell\\v1.0\\Modules\\NetQos\\NetQos.psd1' -ErrorAction Stop;" +
        "if (Get-Command New-NetQosPolicy,Get-NetQosPolicy,Remove-NetQosPolicy | Where-Object { " +
        $"-not $_.Module.Path.StartsWith('{system32}\\', [StringComparison]::OrdinalIgnoreCase) }}) {{ exit 3 }};";

    internal const string Retirer =
        "Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction SilentlyContinue | " +
        "Where-Object { $_.Name -in '" + PolitiqueGenerale + "','" + PolitiqueJeu + "' } | " +
        "ForEach-Object { Remove-NetQosPolicy -Name $_.Name -PolicyStore ActiveStore -Confirm:$false };";

    // La générale (tout l'envoi plafonné) naît avant l'exemption du jeu : chaque sortie en échec retire les deux, une
    // cmdlet en erreur arrête le script (-ErrorAction Stop), et un script tué au délai est suivi d'un retrait.
    internal static string ScriptDePose(string gameExeName, long capBitsPerSecond) =>
        Retirer +
        "try {" +
        $"New-NetQosPolicy -Name '{PolitiqueGenerale}' -IPProtocolMatchCondition Both " +
        $"-ThrottleRateActionBitsPerSecond {capBitsPerSecond} -PolicyStore ActiveStore -ErrorAction Stop | Out-Null;" +
        $"New-NetQosPolicy -Name '{PolitiqueJeu}' -AppPathNameMatchCondition '{gameExeName}' " +
        $"-ThrottleRateActionBitsPerSecond {PlafondExemption} -PolicyStore ActiveStore -ErrorAction Stop | Out-Null;" +
        "} catch { " + Retirer + " exit 1 };" +
        // Relecture : les deux politiques, avec les valeurs demandees, sinon echec.
        $"$g = Get-NetQosPolicy -Name '{PolitiqueGenerale}' -PolicyStore ActiveStore -ErrorAction SilentlyContinue;" +
        $"$j = Get-NetQosPolicy -Name '{PolitiqueJeu}' -PolicyStore ActiveStore -ErrorAction SilentlyContinue;" +
        $"if ($g -and $j -and $g.ThrottleRateAction -eq {capBitsPerSecond} -and $j.AppPathName -eq '{gameExeName}') {{ exit 0 }} else {{ " + Retirer + " exit 1 }";

    public Result ApplyUploadCap(string gameExeName, long capBitsPerSecond)
    {
        var r = Run(ScriptDePose(gameExeName, capBitsPerSecond), "The upload cap could not be applied and read back; nothing is left in place.");
        // Tué au délai, le script a pu s'arrêter entre deux créations : un retrait à part, au mieux. Sorti en échec, il a
        // déjà tout retiré lui-même ; un second powershell tiendrait le helper (une op à la fois) jusqu'à 30 s de plus.
        if (r.Error?.Message == PasRevenu) Run(Retirer, "");
        return r;
    }

    public Result RemoveUploadCap() => Run(
        Retirer +
        "$r = Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction SilentlyContinue | " +
        "Where-Object { $_.Name -in '" + PolitiqueGenerale + "','" + PolitiqueJeu + "' };" +
        "if ($r) { exit 1 } else { exit 0 }",
        "The upload cap could not be removed.");

    private const string PasRevenu = "powershell.exe did not return in time.";

    private static Result Run(string script, string failureMessage)
    {
        try
        {
            var psi = new ProcessStartInfo(PowerShellExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var systeme = ModuleSysteme(Environment.GetFolderPath(Environment.SpecialFolder.System));
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", systeme + script })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return Result.Fail(ErrorCodes.HelperOpFailed, "powershell.exe could not be started.");
            // Lues, jamais affichées : une sortie non lue remplit le tampon et bloque powershell jusqu'au délai.
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)CallTimeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* deja parti */ }
                return Result.Fail(ErrorCodes.HelperOpFailed, PasRevenu);
            }
            return p.ExitCode == 0 ? Result.Ok() : Result.Fail(ErrorCodes.HelperOpFailed, failureMessage);
        }
        catch
        {
            return Result.Fail(ErrorCodes.HelperOpFailed, failureMessage);
        }
    }
}
