using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// GATE G2 — les trois garanties de signature de l'installeur.
///
/// `installer/build-installer.ps1` les ecrit noir sur blanc dans son en-tete :
///   1. on ne peut pas expedier du non signe par accident (le nom du fichier le DIT) ;
///   2. `-Sign` sans certificat echoue bruyamment et tout de suite, jamais de repli silencieux ;
///   3. une signature est VERIFIEE apres coup, parce que signtool peut rendre 0 sur une chaine
///      que Windows refusera ensuite.
///
/// Mesure du 05/09/2026 : ces trois-la etaient les seules garanties du chemin de vente a n'avoir
/// Aucun instrument. Elles vivaient dans des commentaires — c'est-a-dire nulle part, du point de
/// vue d'un controle. Un script de packaging ne casse jamais un test : il casse une vente.
///
/// La garantie 2 est verifiee en EXECUTANT le script, pas en le lisant : c'est la seule des trois
/// qui soit observable sans certificat, et elle a ete mesuree a 3 secondes (sortie 1, avant tout
/// build). Les deux autres sont lues dans le texte du script, faute de pouvoir signer sans
/// certificat — la limite est dite, pas masquee.
/// </summary>
public sealed class TheInstallerCannotShipUnsignedGateTests
{
    private static string ScriptPath() => Path.Combine(RepoRootOf(), "installer", "build-installer.ps1");

    private static string RepoRootOf()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("DrDXT.sln introuvable depuis " + AppContext.BaseDirectory);
    }

    // ═══ 2. Le comportement, execute ═══

    [Fact]
    public async Task Asking_for_a_signature_without_a_certificate_fails_loudly_and_early()
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{ScriptPath()}\" -Sign",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Une empreinte heritee de la machine ferait passer ce test pour une raison etrangere.
        psi.Environment["DRDXT_SIGN_THUMBPRINT"] = string.Empty;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("powershell.exe n'a pas demarre");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        // 60 s : la garde doit tomber avant `dotnet publish`, qui prend des minutes. Si le script
        // tourne encore, c'est qu'il est parti construire — le « tout de suite » n'est plus vrai.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new Xunit.Sdk.XunitException(
                "la validation de signature doit echouer avant le publish : le script tournait encore apres 60 s");
        }

        process.ExitCode.Should().NotBe(0, "demander une signature sans certificat ne doit jamais reussir");

        var output = await stdout + await stderr;
        output.Should().Contain("AUCUN certificat fourni",
            "l'echec doit DIRE ce qui manque : un echec muet se contourne en relancant sans -Sign");
        output.Should().NotContain("DrDXT-Setup.exe",
            "aucun artefact signe ne doit avoir ete produit");
    }

    // ═══ 1 et 3. Les proprietes du script, lues ═══

    [Fact]
    public void An_unsigned_artefact_carries_its_nature_in_its_name()
    {
        var script = File.ReadAllText(ScriptPath());

        // Le nom conditionnel : signe -> DrDXT-Setup, sinon -> le nom qui previent.
        Regex.IsMatch(script, @"\$baseName\s*=\s*if\s*\(\s*\$Sign\s*\)\s*\{\s*'DrDXT-Setup'\s*\}\s*else\s*\{\s*'DrDXT-Setup-NON-SIGNE'\s*\}")
            .Should().BeTrue(
                "un nom est la garde la moins chere qui survive a une soiree fatiguee : personne "
                + "n'envoie « NON-SIGNE » a un client en croyant envoyer la version vendue");
    }

    [Fact]
    public void A_signature_is_read_back_before_it_is_announced()
    {
        var script = File.ReadAllText(ScriptPath());

        script.Should().MatchRegex(@"signTool\s+verify\s+/pa",
            "signtool peut rendre 0 sur une chaine que Windows refusera : on relit");
        script.Should().MatchRegex(@"Signature POSEE mais NON VERIFIEE",
            "et l'echec de la relecture doit ARRETER le script, pas seulement s'afficher");

        // L'ordre compte : verifier avant de signer ne prouverait rien.
        var sign = script.IndexOf("signTool sign", StringComparison.Ordinal);
        var verify = script.IndexOf("signTool verify", StringComparison.Ordinal);
        sign.Should().BeGreaterThan(0);
        verify.Should().BeGreaterThan(sign, "la relecture vient APRES la signature");
    }

    [Fact]
    public void The_readers_would_actually_catch_a_regression()
    {
        // Controle POSITIF : sans lui, une expression reguliere cassee rendrait la meme couleur
        // qu'un script sain — c'est deja arrive deux fois dans cette session.
        const string broken = "$baseName = 'DrDXT-Setup'\n& $signTool sign /fd SHA256 $Path\n";

        Regex.IsMatch(broken, @"\$baseName\s*=\s*if\s*\(\s*\$Sign\s*\)").Should().BeFalse();
        Regex.IsMatch(broken, @"signTool\s+verify\s+/pa").Should().BeFalse();
    }
}
