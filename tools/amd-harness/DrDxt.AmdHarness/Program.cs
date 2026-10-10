using DrDxt.AmdHarness;
using DrDxt.AmdHarness.Reporting;
using DrDxt.Core;
using DrDxt.Detection;
using DrDxt.Detection.Gpu.Adlx;
using DrDxt.Detection.Repair;
using Microsoft.Extensions.DependencyInjection;

// -----------------------------------------------------------------------
// DrDXT-Test : le banc portable distribué aux joueurs (zip de validation ou zip de rapport en lecture seule).
//   sans argument / --rapport : rapport en LECTURE SEULE, rien n'est modifié ;
//   --valider : chaque levier du produit posé, relu, rendu, relu (ValidationMachine) ;
//   --remettre : rend seulement ce qu'un essai interrompu a laissé ;
//   --lire : ce que l'ADLX lit sur la carte AMD dédiée ;
//   --etape N : une étape du rapport, dans un processus enfant (usage interne).
// 09/10/2026 (diffusion publique) : aucun rapport ne porte le nom du PC ni du compte ; tout le texte passe par les règles
// de la machine exportée (MachineExportee.CeQuiEstRetire) avant d'être écrit. L'ancienne session d'écriture de
// fréquence et de tension (juillet) est supprimée du code, pas seulement désactivée.
// -----------------------------------------------------------------------

const string LienTicket = "https://github.com/ablanchard-dev/drdxt/issues/new?template=rapport-de-verification.yml";

// 10/10/2026 (revue de sécurité) : le banc ne demande plus l'élévation lui-même (manifeste asInvoker). Un programme .NET
// élevé par l'UAC hérite de HKCU\Environment, où tout programme du compte peut poser de quoi charger sa DLL avant cette
// ligne. Lancé sans droits (double-clic), il passe la main au lanceur natif à côté de lui, qui demande l'élévation et le
// relance avec un environnement reconstruit ; puis il s'arrête. Sans lanceur, il le dit et s'arrête.
if (!LanceurDuBanc.EstAdministrateur())
{
    var code = LanceurDuBanc.RelancerParLeLanceur(args);
    if (code != 0) Console.WriteLine(LanceurDuBanc.Message(code));
    if (code != 0 && !Console.IsInputRedirected) { Console.WriteLine("Appuie sur une touche pour fermer."); try { Console.ReadKey(true); } catch (InvalidOperationException) { } }
    return;
}

// Revue n° 8 : « --etape N » = une seule étape, dans ce processus enfant ; sa sortie est relue par le parent.
if (args.Length == 2 && args[0] == "--etape" && int.TryParse(args[1], out var numero))
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    var servicesEtape = new ServiceCollection();
    servicesEtape.AddSingleton<IElevatedClient>(new RefusDElevation());
    servicesEtape.AddDrDxtDetection();
    using var fournisseurEtape = servicesEtape.BuildServiceProvider();
    var toutes = RapportLecture.EtapesReelles(fournisseurEtape.GetRequiredService<IHardwareDetector>(), fournisseurEtape.GetRequiredService<ISettingsStateReader>());
    if (numero < 0 || numero >= toutes.Count) { Console.WriteLine($"étape {numero} inconnue"); return; }
    // Pour éprouver le rapport : DRDXT_BANC_PLANTER=N tue l'étape N net, comme un plantage du pilote.
    if (Environment.GetEnvironmentVariable("DRDXT_BANC_PLANTER") == numero.ToString(System.Globalization.CultureInfo.InvariantCulture))
        Environment.FailFast("plantage simulé de l'étape (DRDXT_BANC_PLANTER)");
    Console.WriteLine(await toutes[numero].Lire(CancellationToken.None));
    return;
}

// A1 (06/10/2026) : « --lire » lit par ADLX, sur la carte AMD dédiée, ce que le produit affiche (SAM, Anti-Lag,
// FRTC / Chill, température), puis s'arrête. Lecture seule : aucune entrée Set* n'est appelée, aucun service lancé.
if (args.Contains("--lire"))
{
    static string Etat(bool? v) => v switch { true => "activé", false => "désactivé", null => "non lu" };
    var lus = AdlxReglages.Lire();
    Console.WriteLine($"amdadlx64.dll dans System32 : {(AdlxReglages.DllPresente() ? "présente" : "absente")}");
    Console.WriteLine(lus.Raison is null ? "ADLX : carte dédiée trouvée" : $"ADLX : rien lu ({lus.Raison})");
    Console.WriteLine($"SAM (ReBAR)   : {Etat(lus.Sam)}");
    Console.WriteLine($"Anti-Lag      : {Etat(lus.AntiLag)}");
    Console.WriteLine($"FRTC          : {Etat(lus.FrtcActif)}{(lus.FrtcFps is { } f ? $", {f} images/s" : "")}");
    Console.WriteLine($"Chill         : {Etat(lus.ChillActif)}{(lus.ChillMaxFps is { } c ? $", {lus.ChillMinFps}-{c} images/s" : "")}");
    Console.WriteLine($"Limite images : {(lus.LimiteImages is { } l ? (l == 0 ? "aucune" : $"{l} images/s") : "non lue")}");
    Console.WriteLine($"Température   : {(lus.TemperatureC is { } t ? $"{t:0.0} °C" : "non lue")}");
    return;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
var valeursDeCePc = MachineExportee.ValeursDeCePc();
string Propre(string t) => MachineExportee.AnonymiserTexte(t, valeursDeCePc);
// Même première ligne que le rapport de l'App : tools/ranger_rapport.py la reconnaît.
var version = typeof(DrDxt.Application.Autopilot.ValidationComplete).Assembly.GetName().Version?.ToString(3);
var horodatage = DateTime.Now;

// 07/10/2026 (revue n° 8) : « --valider » pose chaque levier du produit, le relit, le rend, le relit encore.
// « --remettre » rend seulement ce qu'un essai interrompu a laissé. Les deux commencent par rendre ce qui reste.
if (args.Contains("--valider") || args.Contains("--remettre"))
{
    var valider = args.Contains("--valider");
    var chemin = Path.Combine(RapportLecture.Dossier(), $"DrDXT-validation-{horodatage:yyyyMMdd-HHmmss}.txt");
    var corps = new System.Text.StringBuilder();
    // Écrit au fil de l'eau (un arrêt brutal laisse au moins le début), puis réécrit en entier avec le bilan en tête.
    void Ecrire(string t) { corps.Append(t); var p = Propre(t); Console.Write(p); try { File.AppendAllText(chemin, p, new System.Text.UTF8Encoding(false)); } catch (Exception) { } }
    Ecrire($"DrDXT {version}, vérification du {horodatage:dd/MM/yyyy HH:mm} ({(valider ? "validation complète : chaque réglage est posé puis remis d'origine" : "remise d'origine")}, zip de validation){Environment.NewLine}{Environment.NewLine}");
    var nonRemis = new List<string>();
    try
    {
        using var sp = ValidationMachine.Services();
        var remise = await ValidationMachine.RemettreAsync(sp);
        Ecrire($"=== essai précédent{Environment.NewLine}{remise.Texte}{Environment.NewLine}{Environment.NewLine}");
        if (!remise.Propre) nonRemis.Add(remise.Texte);
        else if (valider)
        {
            var r = await ValidationMachine.ValiderAsync(sp, Ecrire);
            nonRemis.AddRange(r.NonRemis);
        }
    }
    catch (Exception ex) { Ecrire($"arrêt inattendu : {ex.Message}{Environment.NewLine}"); nonRemis.Add("arrêt inattendu : relance le test, il remet d'abord ce qui reste"); }
    var bilan = nonRemis.Count == 0
        ? "BILAN : tout est remis d'origine."
        : $"BILAN : À VÉRIFIER À LA MAIN :{Environment.NewLine}  " + string.Join($"{Environment.NewLine}  ", nonRemis);
    // Le bilan en tête du fichier : c'est la seule ligne que le joueur doit lire.
    var ecrit = true;
    try { File.WriteAllText(chemin, Propre(bilan + Environment.NewLine + Environment.NewLine + corps), new System.Text.UTF8Encoding(true)); }
    catch (Exception) { ecrit = false; }
    Console.WriteLine();
    Console.WriteLine(Propre(bilan));
    Fin(ecrit ? chemin : null);
    return;
}

// 07/10/2026 (T3) : par défaut, le rapport en LECTURE SEULE (Reporting/RapportLecture.cs), écrit dans Téléchargements,
// qui ne s'arrête jamais sur une erreur.
{
    Console.WriteLine("DrDXT — rapport de test en LECTURE SEULE : rien n'est modifié sur ce PC. Compte 1 à 3 minutes. Ne ferme pas cette fenêtre.");
    Console.WriteLine();
    var entete = $"DrDXT {version}, rapport en lecture seule du {horodatage:dd/MM/yyyy HH:mm}{Environment.NewLine}{Environment.NewLine}";
    var chemin = Path.Combine(RapportLecture.Dossier(), $"DrDXT-rapport-{horodatage:yyyyMMdd-HHmmss}.txt");
    var corps = new System.Text.StringBuilder(entete);
    try
    {
        // Revue n° 8 : le parent n'appelle aucun code natif ; chaque étape tourne dans son processus.
        var services = new ServiceCollection();
        services.AddSingleton<IElevatedClient>(new RefusDElevation());
        services.AddDrDxtDetection();
        using var fournisseur = services.BuildServiceProvider();
        var noms = RapportLecture.EtapesReelles(fournisseur.GetRequiredService<IHardwareDetector>(), fournisseur.GetRequiredService<ISettingsStateReader>())
            .Select(e => e.Nom).ToList();
        var texte = await RapportLecture.DeroulerAsync(RapportLecture.EtapesEnProcessus(noms), RapportLecture.DelaiParEtape,
            nom => Console.WriteLine($"  … {nom}"),
            bloc => { corps.Append(bloc); try { File.AppendAllText(chemin, Propre(bloc), new System.Text.UTF8Encoding(false)); } catch (Exception) { } });
        corps.Append(texte.Split(Environment.NewLine)[0]).Append(Environment.NewLine);
    }
    catch (Exception ex) { corps.Append($"Le rapport n'a pas pu se dérouler : {ex.Message}"); }
    var ecrit = true;
    try { File.WriteAllText(chemin, Propre(corps.ToString()), new System.Text.UTF8Encoding(true)); }
    catch (Exception) { ecrit = false; }
    if (!ecrit) { Console.WriteLine("Le fichier n'a pas pu s'écrire. Voici le rapport :"); Console.WriteLine(Propre(corps.ToString())); }
    Fin(ecrit ? chemin : null);
}

void Fin(string? chemin)
{
    Console.WriteLine();
    if (chemin is not null) Console.WriteLine($"Rapport écrit : {chemin}");
    Console.WriteLine("Le nom du PC, le nom du compte, les numéros de série et les adresses réseau en sont retirés. Relis-le avant de l'envoyer.");
    Console.WriteLine($"Pour aider le projet : ouvre {LienTicket} et glisse ce fichier dans le ticket.");
    if (!Console.IsInputRedirected)
    {
        Console.WriteLine("Appuie sur une touche pour fermer.");
        try { Console.ReadKey(true); } catch (InvalidOperationException) { }
    }
}

/// <summary>Un canal élevé qui refuse, et qui le dit : le rapport en lecture seule ne modifie rien.</summary>
internal static class LanceurDuBanc
{
    public const string Lanceur = "DrDxt.Lanceur.exe";

    public static bool EstAdministrateur()
    {
        using var identite = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identite).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>0 relancé, 3 lanceur absent, 1223 élévation refusée, 1 autre échec.</summary>
    public static int RelancerParLeLanceur(string[] args)
    {
        var lanceur = Path.Combine(AppContext.BaseDirectory, Lanceur);
        if (!File.Exists(lanceur)) return 3;
        var psi = new System.Diagnostics.ProcessStartInfo(lanceur) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        psi.ArgumentList.Add("DrDXT-Test.exe");
        foreach (var a in args) psi.ArgumentList.Add(a);
        try { System.Diagnostics.Process.Start(psi); return 0; }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return 1223; }
        catch (Exception) { return 1; }
    }

    public static string Message(int code) => code switch
    {
        3 => $"{Lanceur} manque à côté de ce programme : extrais tout le zip, puis relance.",
        1223 => "Windows n'a pas eu ton accord : rien n'a été lu ni modifié. Relance et clique « Oui » pour faire le test.",
        _ => "Le test n'a pas pu demander les droits administrateur : rien n'a été lu ni modifié.",
    };
}

internal sealed class RefusDElevation : IElevatedClient
{
    public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        => Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed,
            $"le rapport en lecture seule n'ouvre aucun chemin privilégié : {op.GetType().Name} refusée."));
}
