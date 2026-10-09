using DrDxt.Core;
using DrDxt.Detection;
using DrDxt.Detection.Repair;
using Microsoft.Extensions.DependencyInjection;

// Enregistre CETTE machine pour tests/Shared/machines (chantier B8a, B8b) : inventaire, réglages et constats du
// docteur, en lecture seule. Usage : enregistreur <fichier.json> <nom> [note]. Lancé sans élévation, il enregistre ce
// qu'un compte sans droits administrateur voit.
// 07/10/2026 (T1) : même format et même anonymisation que « Exporter ma machine » de l'App (MachineExportee) ; le
// dossier du compte devient « joueur », les numéros de série et adresses de ce PC sont retirés.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage : enregistreur <fichier.json> <nom> [note]");
    return 2;
}

var sp = new ServiceCollection().AddDrDxtDetection().BuildServiceProvider();
var hw = (await sp.GetRequiredService<IHardwareDetector>().DetectAsync()).Value!;
var st = (await sp.GetRequiredService<ISettingsStateReader>().ReadAsync(hw)).Value!;
var sante = new RepairAuditService().Lire();
var eleve = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
File.WriteAllText(args[0], MachineExportee.Json(args[1], args.Length > 2 ? args[2] : null, eleve, hw, st, sante,
    MachineExportee.ValeursDeCePc()));
Console.WriteLine($"ok {sante.Count} constats");
return 0;
