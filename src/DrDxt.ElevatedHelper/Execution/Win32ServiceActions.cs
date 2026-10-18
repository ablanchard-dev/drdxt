using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Execution;

// -----------------------------------------------------------------------
// Win32ServiceActions : le vrai pilotage de services + l'installation de PresentMon
// (frontière du m2smoke).
//
// * Control(start/stop/query) utilise System.ServiceProcess.ServiceController sur un
//   nom de service déjà restreint par l'allowlist (AMD3DVCacheSvc + actions).
// * InstallPresentMonService installe et démarre le PresentMonService.exe embarqué
//   seulement : borné au binaire que le helper livre à côté de lui ; aucun chemin libre.
//
// Tout échec est emballé → HELPER.OP_FAILED ; ne lève jamais.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class Win32ServiceActions : IServiceActions
{
    // Le service PresentMon embarqué (docs/PRESENTMON-COEXISTENCE.md).
    private const string PresentMonServiceName = "PresentMonService";
    private const string PresentMonBinaryFileName = "PresentMonService.exe";

    private readonly TimeSpan _waitTimeout;
    private readonly Func<string?> _presentMonBinaryLocator;
    private readonly Func<string, string?> _verifierChemin;

    public Win32ServiceActions(Func<string?>? presentMonBinaryLocator = null, TimeSpan? waitTimeout = null,
        Func<string, string?>? verifierChemin = null)
    {
        _presentMonBinaryLocator = presentMonBinaryLocator ?? DefaultPresentMonBinaryLocator;
        _waitTimeout = waitTimeout ?? TimeSpan.FromSeconds(15);
        _verifierChemin = verifierChemin ?? CheminProtege.Verifier;
    }

    public Result Control(string serviceName, ServiceAction action)
    {
        try
        {
            return action switch
            {
                ServiceAction.Query => Query(serviceName),
                ServiceAction.Start => Start(serviceName),
                ServiceAction.Stop => Stop(serviceName),
                // L'installation générique d'un service quelconque n'est pas prise en charge ici :
                // le seul chemin d'installation est l'op PresentMon bornée.
                ServiceAction.Install => Fail("Generic service install is not supported."),
                _ => Fail("Unsupported service action."),
            };
        }
        catch (Exception ex)
        {
            return Fail($"Service control failed: {ex.GetType().Name}.");
        }
    }

    public Result InstallPresentMonService()
    {
        try
        {
            var binary = _presentMonBinaryLocator();
            if (string.IsNullOrWhiteSpace(binary) || !File.Exists(binary))
                return Fail("Vendored PresentMon service binary not found.");

            // Avant de créer, réparer ou démarrer : le démarrage seul suffirait à faire
            // tourner en SYSTEM un binaire remplacé. Un build de dev sous le profil échoue ici : la mesure
            // passe au compteur DXGI de repli.
            if (_verifierChemin(binary) is { } nonProtege)
                return Fail($"PresentMon service not started: its executable is not protected ({nonProtege}).");

            // Quatre cas, pas deux. « Le service existe ⇒ demarre-le » laisserait passer
            // celui qui produit le conflit : un service d'une installation plus ancienne de DrDXT,
            // ailleurs ou avec des arguments qu'on ne pose plus. Le demarrer ne suffit pas : il republie le
            // tube par defaut, que le service Intel possede deja (mesure du 07/09 : les deux
            // services Running, une seule paire de tubes).
            //
            // Et l'inverse : quand la configuration est deja la bonne, on n'ecrit rien. Un
            // produit qui recree son service a chaque mesure se bat contre le systeme.
            var exists = ServiceExists(PresentMonServiceName);
            // L'echappement du binPath vit dans Core, teste : ecrit naivement, sc.exe rend 1639
            // et ne cree rien (mesure du 07/09/2026). Le chemin d'installation par defaut contient
            // une espace, donc c'est le cas nominal qui casse.
            var binPath = PresentMonServicePlan.BinPathArgument(binary);

            switch (PresentMonServicePlan.Decide(exists, exists ? ReadBinPath(PresentMonServiceName) : null, binary))
            {
                case PresentMonServiceAction.Install:
                    var create = RunSc($"create {PresentMonServiceName} {binPath} start= demand");
                    if (!create.IsOk) return create;
                    break;

                case PresentMonServiceAction.Repair:
                    // Le service doit etre arrete pour que la nouvelle ligne de commande prenne.
                    // Un arret rate fait echouer la reparation (PresentMonServicePlan.Reparer).
                    var repare = PresentMonServicePlan.Reparer(
                        () => Stop(PresentMonServiceName),
                        () => RunSc($"config {PresentMonServiceName} {binPath} start= demand"));
                    if (!repare.IsOk) return repare;
                    break;

                case PresentMonServiceAction.StartOnly:
                    break;

                case PresentMonServiceAction.PasANous:
                    // Le service d'un autre produit (même nom que le PresentMon d'Intel) : ni repointé ni démarré.
                    return Fail("PresentMon service belongs to another product: left untouched; measurement uses the DXGI fallback.");
            }

            return Start(PresentMonServiceName);
        }
        catch (Exception ex)
        {
            return Fail($"PresentMon service install failed: {ex.GetType().Name}.");
        }
    }

    private Result Start(string serviceName)
    {
        using var sc = new ServiceController(serviceName);
        if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
            return Result.Ok();
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, _waitTimeout);
        return sc.Status == ServiceControllerStatus.Running
            ? Result.Ok()
            : Fail($"Service '{serviceName}' did not reach Running.");
    }

    private Result Stop(string serviceName)
    {
        using var sc = new ServiceController(serviceName);
        if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
            return Result.Ok();
        if (!sc.CanStop)
            return Fail($"Service '{serviceName}' cannot be stopped.");
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, _waitTimeout);
        return sc.Status == ServiceControllerStatus.Stopped
            ? Result.Ok()
            : Fail($"Service '{serviceName}' did not stop.");
    }

    private static Result Query(string serviceName)
    {
        if (!ServiceExists(serviceName))
            return Fail($"Service '{serviceName}' is not installed.");
        using var sc = new ServiceController(serviceName);
        // Toucher Status pour qu'une requête reflète vraiment l'état du SCM.
        _ = sc.Status;
        return Result.Ok();
    }

    private static bool ServiceExists(string serviceName) =>
        ServiceController.GetServices().Any(s =>
            string.Equals(s.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>La ligne de commande enregistree dans le SCM, ou <c>null</c> si elle n'a pas pu
    /// etre lue. Le registre est la source : `ServiceController` n'expose pas le `binPath`.
    /// `null` ne veut pas dire « elle est correcte » : <see cref="PresentMonServicePlan"/>
    /// repare dans ce cas, par la meme doctrine que partout ailleurs : Unknown ne deduit rien.</summary>
    private static string? ReadBinPath(string serviceName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return key?.GetValue("ImagePath") as string;
        }
        catch
        {
            return null;
        }
    }

    private static Result RunSc(string arguments)
    {
        // Resolu depuis System32, jamais par nom nu. Un nom nu part dans la recherche
        // de CreateProcess (dossier de l'appli, puis dossier courant, avant System32 : Microsoft Learn,
        // CreateProcessW, lpCommandLine). Meme regle que shutdown.exe et powershell.exe.
        var psi = new ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var r = ProcessusBorne.Executer(psi, TimeSpan.FromSeconds(15));
        if (!r.Lance)
            return Fail("Could not launch sc.exe.");
        return r.Fini && r.Code == 0
            ? Result.Ok()
            : Fail($"sc.exe failed (exit {(r.Fini ? r.Code : -1)}).");
    }

    // Le binaire embarqué se déploie à côté de l'exe du helper (ou, en dev, dans un dossier
    // voisin third_party\presentmon).
    private static string? DefaultPresentMonBinaryLocator()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, PresentMonBinaryFileName),
            Path.Combine(baseDir, "third_party", "presentmon", PresentMonBinaryFileName),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static Result Fail(string message) => Result.Fail(ErrorCodes.HelperOpFailed, message);
}
