using DrDxt.Core;

namespace DrDxt.App.Platform;

public static class VariablesFirmware
{
    // 24/09/2026 — lire une variable firmware pour le scan. Jamais d'UAC : le helper deja lance
    // (client sans lancement), sinon lecture directe si l'appli tourne deja en administrateur.
    // Aucun des deux : echec, et le scan garde son inference.
    public static async Task<Result<byte[]>> Lire(string nom, string guid, CancellationToken ct)
    {
        var parHelper = await ElevatedClientFactory.CreateSansLancer()
            .ReadAsync(new ReadFirmwareVariableOp(nom, guid), ct);
        if (parHelper.IsOk) return parHelper;

        var eleve = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        return eleve
            ? new DrDxt.ElevatedHelper.Execution.Win32FirmwareBootActions().ReadFirmwareVariable(nom, guid)
            : parHelper;
    }
}
