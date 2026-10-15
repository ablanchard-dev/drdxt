using DrDxt.Core;
using DrDxt.Detection.Repair;
using DrDxt.Detection.Wmi;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

/// <summary>
/// Une tâche planifiée lancée en SYSTEM ou au plus haut niveau depuis un fichier que le compte
/// peut modifier est la même élévation qu'un service remplaçable. Vue sur la machine de reference : une tâche « plus haut
/// niveau » qui lance un pythonw.exe de ses Téléchargements.
/// </summary>
public sealed class TachesRemplacablesTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "drdxt-taches-" + Guid.NewGuid().ToString("N"));
    private readonly string _exe;

    public TachesRemplacablesTests()
    {
        Directory.CreateDirectory(_dossier);
        _exe = Path.Combine(_dossier, "pythonw.exe");
        File.WriteAllBytes(_exe, [0]);
    }

    public void Dispose() => Directory.Delete(_dossier, recursive: true);

    private static WmiRow Tache(string nom, string? compte, int niveau, params string[] executables)
        => TacheEnEtat(nom, compte, niveau, 3, executables);

    // État MSFT_ScheduledTask : 1 = désactivée, 3 = prête.
    private static WmiRow TacheEnEtat(string nom, string? compte, int niveau, int etat, params string[] executables) => new(
        new Dictionary<string, object?>
        {
            ["State"] = (uint)etat,
            ["TaskName"] = nom,
            ["TaskPath"] = @"\",
            ["Principal"] = new WmiRow(new Dictionary<string, object?> { ["UserId"] = compte, ["RunLevel"] = (uint)niveau }),
            ["Actions"] = executables.Select(e => new WmiRow(new Dictionary<string, object?> { ["Execute"] = e })).ToArray(),
        });

    private static Result<IReadOnlyList<WmiRow>> Lues(params WmiRow[] taches) => Result<IReadOnlyList<WmiRow>>.Ok(taches);

    [Fact]
    public void Une_tache_au_plus_haut_niveau_depuis_un_dossier_du_compte_est_signalee()
    {
        // La vraie règle du helper, sur un vrai fichier du compte : c'est le cas vu sur la machine de reference.
        var constats = RepairAuditService.TachesRemplacables(
            Lues(Tache("Agent", "blanc", 1, _exe)), CheminProtege.Verifier).ToList();

        constats.Should().ContainSingle();
        constats[0].Statut.Should().Be(RepairStatus.Suspicious);
        // Une tâche n'est pas un service : sa propre famille, et son propre titre à l'écran et dans le rapport.
        RepairLibelles.Domaine(constats[0].Domaine).Should().Be("Tâches planifiées");
        // Photo de l'accueil : « Tâche planifiée \Agent de projet ». La barre du dossier racine
        // du Planificateur n'a pas de sens pour un joueur : le titre porte le nom, le détail dit où la trouver.
        constats[0].Nom.Should().StartWith("tâche planifiée Agent :");
        constats[0].Explication.Should().Contain(@"Planificateur de tâches : \Agent");
        constats[0].Explication.Should().Contain("sans passer par la fenêtre de confirmation");
        constats[0].Remede.Should().StartWith("1) ").And.Contain("Supprimer").And.Contain("Program Files");
        constats[0].Explication.Should().NotContain("Déplacer le programme");
        // « tâche planifiée remplaçable » est du jargon qui fait peur sans rien dire.
        // Le titre dit ce qui peut arriver, en mots de joueur.
        constats[0].Nom.Should().NotContain("remplaçable").And.Contain("peut être remplacé par un autre programme");
    }

    // Une tâche désactivée ne se lance pas : le constat ne dit pas « cette tâche se lance ». Le chemin reste,
    // réactivée elle élèverait.
    [Fact]
    public void Une_tache_desactivee_ne_dit_pas_qu_elle_se_lance()
    {
        var e = RepairAuditService.TachesRemplacables(Lues(TacheEnEtat("Agent", "blanc", 1, 1, _exe)), _ => "modifiable")
            .Single().Explication;
        e.Should().NotStartWith("cette tâche se lance").And.Contain("désactivée").And.Contain("réactivée");
    }

    [Fact]
    public void Une_tache_systeme_dit_SYSTEM()
        => RepairAuditService.TachesRemplacables(Lues(Tache("Maj", "SYSTEM", 0, _exe)), _ => "modifiable")
            .Single().Explication.Should().Contain("(SYSTEM)");

    [Fact]
    public void Une_tache_limitee_du_compte_n_eleve_rien_donc_rien_n_est_dit()
    {
        // Témoin : la même tâche au plus haut niveau parle.
        RepairAuditService.TachesRemplacables(Lues(Tache("A", "blanc", 1, _exe)), _ => "modifiable").Should().NotBeEmpty();
        RepairAuditService.TachesRemplacables(Lues(Tache("A", "blanc", 0, _exe)), _ => "modifiable").Should().BeEmpty();
    }

    [Fact]
    public void Un_binaire_protege_un_nom_nu_ou_une_ACL_illisible_ne_sont_pas_des_preuves()
    {
        RepairAuditService.TachesRemplacables(Lues(Tache("A", "SYSTEM", 1, _exe)), _ => "modifiable").Should().NotBeEmpty();
        RepairAuditService.TachesRemplacables(Lues(Tache("A", "SYSTEM", 1, _exe)), _ => null).Should().BeEmpty();
        RepairAuditService.TachesRemplacables(Lues(Tache("A", "SYSTEM", 1, "cmd.exe")), _ => "modifiable").Should().BeEmpty();
        RepairAuditService.TachesRemplacables(Lues(Tache("A", "SYSTEM", 1, _exe)), _ => "ACL illisible (X)").Should().BeEmpty();
    }

    [Fact]
    public void Une_liste_illisible_est_dite_non_lue()
        => RepairAuditService.TachesRemplacables(Result<IReadOnlyList<WmiRow>>.Fail(ErrorCodes.WmiFail, "refus"), _ => "x")
            .Single().Statut.Should().Be(RepairStatus.Unknown);

    // Une ACL illisible n'est pas une preuve : en anglais, sa raison ne commence plus par « ACL illisible », et elle
    // devenait « tâche remplaçable » sur tout Windows anglais.
    [Fact]
    public void Une_ACL_illisible_ne_fait_pas_de_constat_dans_aucune_langue()
    {
        foreach (var langue in new[] { "en-US", "fr-FR" })
        {
            using var _ = Docteur.Portee(System.Globalization.CultureInfo.GetCultureInfo(langue));
            RepairAuditService.TachesRemplacables(Lues(Tache("Agent", "SYSTEM", 0, _exe)), _ => Docteur.T("CP.Illisible", "UnauthorizedAccessException"))
                .Should().BeEmpty(langue);
        }
    }
}
