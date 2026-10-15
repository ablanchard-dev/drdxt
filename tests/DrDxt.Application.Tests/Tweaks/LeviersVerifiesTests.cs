using DrDxt.Application.Tweaks;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks;

/// <summary>Les leviers vérifiés sur ce PC, cumulés ; illisible = rien de vérifié.</summary>
public sealed class LeviersVerifiesTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "drdxt-verifies-" + Guid.NewGuid().ToString("N"));
    private string Chemin => Path.Combine(_dossier, "leviers-verifies.json");

    private const string Ici = "Core i7-14700F | NVIDIA GeForce RTX 4060 617.14";

    [Fact]
    public void Les_verifications_se_cumulent_et_un_levier_tente_sans_succes_perd_son_etat()
    {
        LeviersVerifies.Enregistrer(Chemin, Ici, ["latency.hagsOn", "fixit.power_plan"], ["latency.hagsOn", "fixit.power_plan"]);
        LeviersVerifies.Enregistrer(Chemin, Ici, ["latency.hagsOn", "latency.gameModeOn"], ["latency.gameModeOn"]);

        // Hags tenté et pas validé la seconde fois ⇒ plus « vérifié » ; power_plan, pas retenté, le reste.
        LeviersVerifies.Lire(Chemin, Ici).Should().HaveCount(2).And.BeEquivalentTo(["fixit.power_plan", "latency.gameModeOn"]);
    }

    [Fact]
    public void Un_autre_materiel_ne_voit_rien_de_verifie()
    {
        LeviersVerifies.Enregistrer(Chemin, Ici, ["latency.hagsOn"], ["latency.hagsOn"]);
        LeviersVerifies.Lire(Chemin, Ici).Should().HaveCount(1);
        LeviersVerifies.Lire(Chemin, "Core i7-14700F | NVIDIA GeForce RTX 4060 618.00").Should().BeEmpty("le pilote a changé");
        LeviersVerifies.Enregistrer(Chemin, "", ["latency.hagsOn"], []);
        LeviersVerifies.Lire(Chemin, Ici).Should().HaveCount(1, "une vérification sans matériel lu ne touche à rien");
    }

    [Fact]
    public void Absent_ou_illisible_rien_n_est_verifie()
    {
        LeviersVerifies.Lire(Chemin, Ici).Should().BeEmpty();
        Directory.CreateDirectory(_dossier);
        File.WriteAllText(Chemin, "{pas du json");
        LeviersVerifies.Lire(Chemin, Ici).Should().BeEmpty();
        File.WriteAllText(Chemin, "[\"latency.hagsOn\"]");
        LeviersVerifies.Lire(Chemin, Ici).Should().BeEmpty("l'ancien format sans empreinte ne prouve rien");
    }

    public void Dispose() { try { Directory.Delete(_dossier, true); } catch (IOException) { } }
}
