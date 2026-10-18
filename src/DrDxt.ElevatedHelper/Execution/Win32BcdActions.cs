using System.Diagnostics;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// Win32BcdActions : retirer / reposer un element BCD de `{current}`, puis relire.
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

    private readonly Func<string[], (int Code, string Sortie)> _run;

    public Win32BcdActions() : this(Run) { }

    internal Win32BcdActions(Func<string[], (int Code, string Sortie)> run) => _run = run;

    public Result DeleteValue(string element)
    {
        if (_run(["/deletevalue", Entree, element]).Code != 0 && LireEtat(element) is (true, not null))
            return Result.Fail(ErrorCodes.HelperOpFailed, $"bcdedit could not delete {element}.");
        return LireEtat(element) is (true, null)
            ? Result.Ok()
            : Result.Fail(ErrorCodes.HelperOpFailed, $"{element} is not confirmed absent after deletion.");
    }

    // L'état d'avant est lu d'abord : une écriture que la relecture ne confirme pas est reposée tout de suite (l'App ne
    // garde rien d'un échec, et le démarrage suivant porterait la valeur).
    public Result SetValue(string element, string value)
    {
        var avant = LireEtat(element);
        if (!avant.Lu)
            return Result.Fail(ErrorCodes.HelperOpFailed, $"{element} could not be read before the write; nothing written.");
        _run(["/set", Entree, element, value]);
        if (LireEtat(element) is (true, var apres) && string.Equals(apres, value, StringComparison.OrdinalIgnoreCase))
            return Result.Ok();

        _run(avant.Valeur is null ? ["/deletevalue", Entree, element] : ["/set", Entree, element, avant.Valeur]);
        return LireEtat(element) is (true, var rendu) && string.Equals(rendu, avant.Valeur, StringComparison.OrdinalIgnoreCase)
            ? Result.Fail(ErrorCodes.HelperOpFailed, $"{element} did not read back as {value}; the previous state is restored.")
            : Result.Fail(ErrorCodes.TweakPartiallyApplied,
                $"{element} did not read back as {value} and the previous state ({avant.Valeur ?? "absent"}) could not be restored.");
    }

    // La séquence vit sur le gestionnaire de démarrage, pas sur l'entrée courante. `{memdiag}` est l'alias que bcdedit
    // affiche aussi en relecture (sans /v) : la relecture compare l'alias, jamais un GUID propre à la machine.
    private const string Gestionnaire = "{bootmgr}";
    private const string TestMemoire = "{memdiag}";

    public Result ScheduleMemoryDiagnostic()
    {
        // Une séquence vers une entrée absente ne démarrerait rien : sans l'entrée du testeur, rien n'est écrit.
        if (_run(["/enum", TestMemoire]).Code != 0)
            return Result.Fail(ErrorCodes.HelperOpFailed, "The Windows memory diagnostic boot entry is missing; nothing written.");
        // Une séquence unique déjà posée par quelqu'un d'autre (outil de récupération, firmware) n'est pas écrasée.
        if (SequenceLue() is not (true, var avant) || (avant is not null && !string.Equals(avant, TestMemoire, StringComparison.OrdinalIgnoreCase)))
            return Result.Fail(ErrorCodes.HelperOpFailed, "Another one-time boot sequence is pending, or it could not be read; nothing written.");
        _run(["/bootsequence", TestMemoire]);
        return SequenceLue() is (true, var s) && string.Equals(s, TestMemoire, StringComparison.OrdinalIgnoreCase)
            ? Result.Ok()
            : Result.Fail(ErrorCodes.HelperOpFailed, "The memory diagnostic is not confirmed for the next boot.");
    }

    public Result CancelMemoryDiagnostic()
    {
        // Seule la séquence du testeur se retire : celle d'un autre outil reste.
        switch (SequenceLue())
        {
            case (true, null): return Result.Ok();
            case (true, var s) when !string.Equals(s, TestMemoire, StringComparison.OrdinalIgnoreCase):
                return Result.Fail(ErrorCodes.HelperOpFailed, "The pending one-time boot sequence is not the memory diagnostic; nothing removed.");
            case (false, _): return Result.Fail(ErrorCodes.HelperOpFailed, "The one-time boot sequence could not be read; nothing removed.");
        }
        // bcdedit rend une erreur quand il n'y a rien à retirer : c'est la relecture qui tranche.
        _run(["/deletevalue", Gestionnaire, "bootsequence"]);
        return SequenceLue() is (true, null)
            ? Result.Ok()
            : Result.Fail(ErrorCodes.HelperOpFailed, "The one-time boot sequence is not confirmed removed.");
    }

    private (bool Lu, string? Valeur) SequenceLue()
    {
        var (code, sortie) = _run(["/enum", Gestionnaire]);
        if (code != 0) return (false, null);
        foreach (var ligne in sortie.Split('\n'))
        {
            var t = ligne.Trim();
            if (t.StartsWith("bootsequence ", StringComparison.OrdinalIgnoreCase))
                return (true, t["bootsequence".Length..].Trim());
        }
        return (true, null);
    }

    /// <summary>La valeur de l'élément dans `{current}` sur cette machine, null s'il n'y est pas ou ne se lit pas (outils de banc).</summary>
    public static string? Lire(string element) => new Win32BcdActions().LireEtat(element).Valeur;

    /// <summary>Lu = `bcdedit /enum` a répondu ; Valeur = celle de l'élément dans `{current}`, null s'il n'y est pas.</summary>
    private (bool Lu, string? Valeur) LireEtat(string element)
    {
        var (code, sortie) = _run(["/enum", Entree]);
        if (code != 0) return (false, null);
        foreach (var ligne in sortie.Split('\n'))
        {
            var t = ligne.Trim();
            if (t.StartsWith(element + " ", StringComparison.OrdinalIgnoreCase))
                return (true, t[element.Length..].Trim());
        }
        return (true, null);
    }

    private static (int, string) Run(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Bcdedit) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var r = ProcessusBorne.Executer(psi, TimeSpan.FromSeconds(15));
            return r.Fini ? (r.Code, r.Sortie) : (-1, "");
        }
        catch { return (-1, ""); }
    }
}
