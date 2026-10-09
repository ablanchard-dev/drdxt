using System.Diagnostics;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// Win32BcdActions — retirer / reposer un element BCD de `{current}`, puis relire.
//
// Les elements et valeurs arrivent deja valides par OpAllowlist (ensemble ferme). bcdedit.exe est
// resolu depuis le dossier systeme, jamais depuis le PATH. Chaque ecriture est relue par
// `bcdedit /enum {current}` : un code de sortie 0 n'est pas une preuve.
// Les libelles de `bcdedit /enum` sont traduits (« identificateur »), pas les noms d'elements :
// c'est sur eux seuls que la relecture s'appuie.
// -----------------------------------------------------------------------
public sealed class Win32BcdActions : IBcdActions
{
    private const string Entree = "{current}";
    private static string Bcdedit => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "bcdedit.exe");

    public Result DeleteValue(string element)
    {
        if (Run(out _, "/deletevalue", Entree, element) != 0 && Lire(element) is not null)
            return Result.Fail(ErrorCodes.HelperOpFailed, $"bcdedit could not delete {element}.");
        return Lire(element) is null
            ? Result.Ok()
            : Result.Fail(ErrorCodes.HelperOpFailed, $"{element} is still present after deletion.");
    }

    public Result SetValue(string element, string value)
    {
        Run(out _, "/set", Entree, element, value);
        return string.Equals(Lire(element), value, StringComparison.OrdinalIgnoreCase)
            ? Result.Ok()
            : Result.Fail(ErrorCodes.HelperOpFailed, $"{element} does not read back as {value}.");
    }

    /// <summary>La valeur de l'element dans `{current}`, ou null s'il n'y est pas.</summary>
    public static string? Lire(string element)
    {
        if (Run(out var sortie, "/enum", Entree) != 0) return null;
        foreach (var ligne in sortie.Split('\n'))
        {
            var t = ligne.Trim();
            if (t.StartsWith(element + " ", StringComparison.OrdinalIgnoreCase))
                return t[element.Length..].Trim();
        }
        return null;
    }

    private static int Run(out string sortie, params string[] args)
    {
        sortie = "";
        try
        {
            var psi = new ProcessStartInfo(Bcdedit) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return -1;
            sortie = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15_000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; }
    }
}
