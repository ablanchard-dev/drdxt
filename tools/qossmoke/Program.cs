// qossmoke — le plafond d'envoi QoS, pour de vrai (session administrateur).
//
//   qossmoke            la frontiere native : pose, remplace, retire, retire deux fois.
//   qossmoke --bail     le mode automatique contre le VRAI helper (IPC signee, bail) : un faux
//                       jeu (ping -t), pose a son lancement, retrait a sa fermeture, puis l'appli
//                       « meurt » (plus de battement) et le helper doit retirer le plafond SEUL.
//                       Helper lance avant avec --session-key et --app-path=<ce qossmoke.exe>,
//                       meme DRDXT_HELPER_SESSION_KEY dans l'environnement (voir tools/m2smoke).
// Code 0 = tout PASS.
using System.Diagnostics;
using DrDxt.Application.Network;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Ipc;

var ok = true;
void Etape(string nom, bool reussi) { Console.WriteLine($"  [{(reussi ? "PASS" : "FAIL")}] {nom}"); ok &= reussi; }

string Lister() => Run("Get-NetQosPolicy -PolicyStore ActiveStore | Where-Object Name -like 'DrDXT-upload*' | " +
                       "ForEach-Object { \"$($_.Name) | $($_.AppPathName) | $($_.ThrottleRateAction)\" }");
bool Pose() => Lister().Contains("upload-cap-jeu");

// `--bcd` : retire bootstatuspolicy par la classe du produit, relit, puis repose la
// valeur d'origine par l'inverse et relit. La machine finit comme elle a commence.
if (args.Contains("--bcd"))
{
    var bcd = new Win32BcdActions();
    var origine = Win32BcdActions.Lire("bootstatuspolicy");
    Console.WriteLine($"  origine : bootstatuspolicy = {origine ?? "(absent)"}");
    if (origine is null) { Console.WriteLine("  rien a eprouver : l'element est deja absent"); return 2; }
    Etape("retrait (DeleteValue) et relecture : absent", bcd.DeleteValue("bootstatuspolicy").IsOk && Win32BcdActions.Lire("bootstatuspolicy") is null);
    Etape($"inverse (SetValue {origine}) et relecture", bcd.SetValue("bootstatuspolicy", origine).IsOk && Win32BcdActions.Lire("bootstatuspolicy") == origine);
    return ok ? 0 : 1;
}

if (args.Contains("--bail"))
{
    var cle = Convert.FromBase64String(Environment.GetEnvironmentVariable("DRDXT_HELPER_SESSION_KEY") ?? "");
    IElevatedClient helper = new TypedNamedPipeElevatedClient(
        cle, new NamedPipeTypedRequestTransport(PipeSecurityFactory.DefaultPipeName));

    // Le helper refuse un binaire de Windows comme jeu : le faux jeu est une copie de ping.exe sous un autre nom.
    var fauxJeu = Path.Combine(Path.GetTempPath(), "drdxtfauxjeu.exe");
    File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), fauxJeu, overwrite: true);
    Process Jeu() => Process.Start(new ProcessStartInfo(fauxJeu, "-t 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
    var jeu = Jeu();
    var mode = new PlafondEnvoiPendantLeJeu(helper, PlafondEnvoiPendantLeJeu.ProcessusVivant, "drdxtfauxjeu.exe", 400_000_000);

    Console.WriteLine($"  jeu vu par ProcessusVivant : {PlafondEnvoiPendantLeJeu.ProcessusVivant("drdxtfauxjeu.exe")}");
    var direct = await helper.ExecuteAsync(new ApplyUploadCapOp("drdxtfauxjeu.exe", 400_000_000), CancellationToken.None);
    Console.WriteLine($"  apply direct par le helper : {(direct.IsOk ? "OK" : $"{direct.Error?.Code} {direct.Error?.Message}")}");
    await helper.ExecuteAsync(new RemoveUploadCapOp(), CancellationToken.None);

    var demarre = await mode.DemarrerAsync(CancellationToken.None);
    Etape($"bail ouvert aupres du helper ({demarre.Error?.Code})", demarre.IsOk);

    await mode.TickAsync(CancellationToken.None);
    Etape("jeu lance -> plafond pose (lu dans Windows)", Pose());

    jeu.Kill(); jeu.WaitForExit();
    await mode.TickAsync(CancellationToken.None);
    Etape("jeu ferme -> plafond retire", !Pose());

    jeu = Jeu();
    await mode.TickAsync(CancellationToken.None);
    Etape("jeu relance -> plafond repose", Pose());

    // L'appli « meurt » : plus aucun battement. Le bail (30 s) doit expirer et le helper retirer.
    var t = Stopwatch.StartNew();
    while (Pose() && t.Elapsed < TimeSpan.FromSeconds(120)) await Task.Delay(5000);
    Etape($"appli muette -> le helper retire le plafond seul ({t.Elapsed.TotalSeconds:0} s)", !Pose());

    jeu.Kill();
    return ok ? 0 : 1;
}

var qos = new Win32NetworkQosActions();
var pose = qos.ApplyUploadCap("cod.exe", 400_000_000);
Etape("apply", pose.IsOk && Lister().Contains("cod.exe"));
var reapplique = qos.ApplyUploadCap("cod.exe", 300_000_000);
var deux = Lister();
Etape("reapply (remplace, ne s'empile pas)", reapplique.IsOk && deux.Contains("300000000")
      && deux.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2);
Etape("remove", qos.RemoveUploadCap().IsOk && Lister().Trim().Length == 0);
Etape("remove x2 (idempotent)", qos.RemoveUploadCap().IsOk);
return ok ? 0 : 1;

static string Run(string cmd)
{
    var psi = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, UseShellExecute = false };
    foreach (var a in new[] { "-NoProfile", "-Command", cmd }) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); return o;
}
