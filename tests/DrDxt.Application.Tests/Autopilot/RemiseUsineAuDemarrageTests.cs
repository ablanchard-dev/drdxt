using DrDxt.Application.Autopilot;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Autopilot;

/// <summary>
/// Un essai de sous-tension AMD interrompu, que le pilote a gardé au redémarrage : le démarrage ne peut pas rendre l'origine
/// (au-delà de la bande du helper). Il le dit, garde l'essai au journal et signale à l'écran que la remise à l'usine est
/// possible ; il ne la lance jamais lui-même.
/// </summary>
public sealed class RemiseUsineAuDemarrageTests : IDisposable
{
    private readonly string _fichier = Path.Combine(Path.GetTempPath(), $"drdxt-usine-{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_fichier); } catch { /* fichier temporaire du test */ } }

    private sealed class Levier(string code) : ITweak
    {
        public int Rendus;
        public string Id => "gpu.undervolt";
        public TweakCategory Category => TweakCategory.GpuTuning;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => true;
        public string SummaryKey => "x";
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => true;
        public Task<Result<TweakSnapshot>> ApplyAsync() => throw new NotSupportedException();
        public Task<Result> RevertAsync(TweakSnapshot snapshot) { Rendus++; return Task.FromResult(Result.Fail(code, "refus")); }
    }

    private sealed class Catalogue(ITweak levier) : ITweakCatalog
    {
        public Result<ITweak> Get(string tweakId) => tweakId == levier.Id ? Result<ITweak>.Ok(levier) : Result<ITweak>.Fail("x", "inconnu");
        public IReadOnlyList<ITweak> All() => [levier];
    }

    [Theory]
    [InlineData(ErrorCodes.GpuRemiseUsineRequise, true)]
    [InlineData("AUTRE_PANNE", false)]
    public void Le_demarrage_dit_le_refus_et_ne_remet_rien_a_l_usine(string code, bool proposee)
    {
        var journal = new FichierRevertEnAttente(_fichier);
        journal.Poser("gpu.undervolt", new TweakSnapshot("gpu.undervolt",
            PrivilegedOpCodec.Serialize(new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, 0)).Value!, "-50")).IsOk.Should().BeTrue();
        var levier = new Levier(code);

        var phrase = RevertAuDemarrage.Executer(journal, new Catalogue(levier), out var remiseUsine);

        remiseUsine.Should().Be(proposee);
        phrase.Should().Be(Docteur.T("RV.RenduEchoue", "gpu.undervolt", code), "le code, jamais le message du helper");
        levier.Rendus.Should().Be(1, "le démarrage tente le rendu une fois, sans remise à l'usine");
        journal.Lire().Value.Should().NotBeNull("rien n'est rendu : l'essai reste au journal");
    }
}
