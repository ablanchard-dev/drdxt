using FluentAssertions;

namespace DrDxt.Integration.Tests;

// Les fichiers posés par [Files] appartiennent au compte qui lance l'installeur, et la règle CRÉATEUR PROPRIÉTAIRE de
// Program Files lui donne le contrôle total. Protégé seulement après la copie, le dossier laisse un programme non élevé de
// ce compte remplacer, pendant l'extraction, l'hôte du service, PresentMonService, le helper ou le lanceur. Le dossier se
// prépare donc AVANT [Files] (PrepareToInstall) : fiable, ou neuf, né sans CRÉATEUR PROPRIÉTAIRE et avec DROITS DU
// PROPRIÉTAIRE en lecture ; et chaque exécutable lancé en SYSTEM ou élevé porte son empreinte de compilation, relue avant
// sc start et schtasks. Le script n'est lu ici que comme du texte : le gate installeur.py le compile, et
// PreparationDesDroitsTests (DrDxt.ElevatedHelper.Tests) exécute le module natif du lanceur.
public sealed class LeDossierEstVerrouilleAvantLaCopieTests
{
    private static string Iss()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "installer", "DrDXT.iss")).Replace("\r\n", "\n");
    }

    private static string Corps(string iss, string entete)
    {
        var debut = iss.IndexOf(entete, StringComparison.Ordinal);
        debut.Should().BeGreaterThan(0, $"« {entete} » existe");
        var corps = iss[debut..];
        return corps[..corps.IndexOf("\nend;", StringComparison.Ordinal)];
    }

    [Fact]
    public void Le_dossier_se_verrouille_avant_toute_copie_et_un_echec_arrete_l_installation()
    {
        var preparer = Corps(Iss(), "function PrepareToInstall(var NeedsRestart: Boolean): String;");

        var admis = preparer.IndexOf("if not DossierAdmis() then", StringComparison.Ordinal);
        var verrou = preparer.IndexOf("if not PreparerLeDossier() then", StringComparison.Ordinal);
        verrou.Should().BeGreaterThan(admis, "le dossier admis d'abord, puis préparé");
        preparer[verrou..].Should().MatchRegex(@"^if not PreparerLeDossier\(\) then\s+begin\s+Result := CustomMessage\('VerrouImpossible'\)",
            "un message non vide arrête l'installation avant [Files], même silencieuse");
    }

    [Fact]
    public void Le_dossier_nait_sans_createur_proprietaire_et_borne_le_proprietaire_a_la_lecture()
    {
        var iss = Iss();
        Corps(iss, "function PreparerLeDossier(): Boolean;")
            .Should().Contain("Result := DossierAdmis() and Droits('app', ExpandConstant('{app}'), SddlApp) and DossierAdmis();");
        iss.Should().Contain("SddlApp = 'O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;OICI;0x1200a9;;;OW)';",
            "plus d'héritage de Program Files (CRÉATEUR PROPRIÉTAIRE), et DROITS DU PROPRIÉTAIRE en lecture : le compte qui crée un "
            + "fichier n'en reçoit pas le contrôle");
        iss.Should().NotMatchRegex(@"Exec\([^;]*/reset", "aucun recalcul des droits ne suit une jonction posée par le compte");
    }

    [Fact]
    public void Les_droits_se_preparent_par_le_lanceur_natif_verifie_par_empreinte_sans_powershell()
    {
        var iss = Iss();
        iss.Should().NotContain("powershell", "aucun PowerShell n'est lancé par l'installeur : il lirait l'environnement du compte");

        var pret = Corps(iss, "function LanceurPret(): String;");
        pret.Should().Contain("ExtractTemporaryFile('DrDxt.Lanceur.exe')", "le lanceur est extrait tôt, avant la copie")
            .And.Contain("CopyFile(Source, Chemin, False)", "il est copié hors du dossier temporaire du compte")
            .And.Contain("GetSHA256OfFile(Chemin), '{#EmpreinteLanceur}'", "l'empreinte est relue sur la copie qui sera lancée")
            .And.Contain("if not DirExists(ExpandConstant('{win}\\SystemTemp')) then", "sans SystemTemp, rien n'est lancé");
        Corps(iss, "function DossierDuLanceur(): String;").Should().Contain("ExpandConstant('{win}\\SystemTemp\\DrDXT-installeur')",
            "un dossier que seuls SYSTEM et les Administrateurs écrivent");

        var droits = Corps(iss, "function Droits(Mode, Chemin, Sddl: String): Boolean;");
        droits.Should().Contain("Lanceur := LanceurPret();").And.Contain("if Lanceur = '' then Exit;")
            .And.Contain("ExecAndCaptureOutput(Lanceur, 'droits ' + Mode", "l'installeur lance le lanceur natif, jamais un outil cherché par son nom")
            .And.Contain("and (ResultCode = 0)", "le verdict est celui du lanceur");
    }

    public static TheoryData<string, string> Lances() => new()
    {
        { "EmpreinteHote", "service\\DrDxt.ReconcilerHost.exe" },
        { "EmpreinteHoteDll", "service\\DrDxt.ReconcilerHost.dll" },
        { "EmpreintePresentMon", "PresentMonService.exe" },
        { "EmpreinteHelper", "DrDxt.ElevatedHelper.exe" },
        { "EmpreinteHelperDll", "DrDxt.ElevatedHelper.dll" },
        { "EmpreinteLanceur", "DrDxt.Lanceur.exe" },
        { "EmpreinteApp", "DrDxt.App.exe" },
        { "EmpreinteAppDll", "DrDxt.App.dll" },
    };

    // L'empreinte vient de la compilation (ISPP), sur les fichiers signés ; elle est relue sur le disque après la copie.
    [Theory]
    [MemberData(nameof(Lances))]
    public void Chaque_executable_lance_en_SYSTEM_ou_eleve_porte_son_empreinte_de_compilation_relue(string nom, string fichier)
    {
        var iss = Iss();
        iss.Should().Contain($"#define {nom} GetSHA256OfFile(AddBackslash(SourcePath) + \"publish\\{fichier}\")");
        Corps(iss, "function EmpreintesConformes(): Boolean;")
            .Should().Contain($"Conforme('{fichier}', '{{#{nom}}}')");
    }

    [Fact]
    public void Les_services_et_la_tache_ne_partent_que_sur_le_dossier_verrouille_et_les_empreintes_conformes()
    {
        var iss = Iss();
        Corps(iss, "function ProtegerLeDossier(): Boolean;")
            .Should().Contain("Result := DossierAdmis() and Droits('verifier', ExpandConstant('{app}'), '') and EmpreintesConformes();");
        var conforme = Corps(iss, "function Conforme(Fichier, Attendue: String): Boolean;");
        conforme.Should().Contain("CompareText(GetSHA256OfFile(ExpandConstant('{app}\\') + Fichier), Attendue) = 0")
            .And.Contain("except", "un fichier absent ou illisible n'est pas conforme");
    }
}
