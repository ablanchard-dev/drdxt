using DrDxt.Application.Autopilot;
using DrDxt.Application.Tweaks;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Autopilot;

/// <summary>
/// Le scénario d'un joueur qui avait son propre réglage Adrenalin : origine 2500 MHz, sous-tension gardée par le pilote au
/// redémarrage, l'usine rend 2700. Rendre l'origine dépasse la bande du helper ; sur « Annuler », et seulement là, la carte
/// est remise à l'usine. Réussie, la remise vaut « rendu » : l'entrée quitte le journal, et un second « Annuler » ne la
/// relance pas (avant, chaque clic remettait à l'usine et rendait un échec, sans fin).
/// </summary>
public sealed class AnnulationParClicTests : IDisposable
{
    private readonly string _chemin = Path.Combine(Path.GetTempPath(), $"drdxt-annuler-{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_chemin); } catch { /* fichier temporaire du test */ } }

    private sealed class Helper : IElevatedClient
    {
        public int Remises;
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            if (op is ApplyAdlxTuningOp { Kind: AdlxTuningKind.RemiseUsine }) { Remises++; return Task.FromResult(Result.Ok()); }
            // L'origine (2500) est au-delà de la bande autour de la fréquence du démarrage (2700, l'usine) : refusée.
            return Task.FromResult(Result.Fail(ErrorCodes.GpuRemiseUsineRequise, Docteur.T("ADLX.UsineRequise", 2500)));
        }
    }

    private sealed class Levier(IElevatedClient helper) : ITweak
    {
        public string Id => "gpu.undervolt";
        public TweakCategory Category => TweakCategory.GpuTuning;
        public bool RequiresElevation => true;
        public bool RequiresRestorePoint => true;
        public string SummaryKey => "x";
        public bool AppliesTo(HardwareInventory hw, SettingsState state) => true;
        public Task<Result<TweakSnapshot>> ApplyAsync() => throw new NotSupportedException();
        public Task<Result> RevertAsync(TweakSnapshot s)
            => helper.ExecuteAsync(PrivilegedOpCodec.Deserialize(s.OriginalValueJson).Value!, CancellationToken.None);
    }

    // Ce que fait l'écran : rendre, puis retirer l'entrée du journal sur un succès.
    private static async Task<AnnulationParClic.Issue?> Annuler(JournalDesLeviersAppliques journal, ITweak levier, IElevatedClient helper)
    {
        if (journal.Lire().FirstOrDefault(s => s.TweakId == levier.Id) is not { } snapshot) return null;
        var issue = await AnnulationParClic.AnnulerAsync(levier, snapshot, helper);
        if (issue.Resultat.IsOk) journal.Retirer(levier.Id);
        return issue;
    }

    [Fact]
    public async Task Une_remise_a_l_usine_reussie_vaut_rendu_et_ne_se_relance_pas()
    {
        var helper = new Helper();
        var journal = new JournalDesLeviersAppliques(_chemin);
        var origine = PrivilegedOpCodec.Serialize(new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, 2500)).Value!;
        journal.Enregistrer(new ApplyReport([new ApplyOutcome("gpu.undervolt", true, false, new TweakSnapshot("gpu.undervolt", origine), null)], null));

        var premier = await Annuler(journal, new Levier(helper), helper);

        premier!.Resultat.IsOk.Should().BeTrue();
        premier.Message.Should().Be(Docteur.T("ADLX.UsineRemise"), "le joueur lit que la mémoire, le ventilateur et la puissance reviennent aussi");
        journal.Lire().Should().BeEmpty();

        (await Annuler(journal, new Levier(helper), helper)).Should().BeNull("plus rien à rendre");
        helper.Remises.Should().Be(1, "un second Annuler ne relance pas la remise à l'usine");
    }

    [Fact]
    public async Task Une_annulation_ordinaire_ne_remet_jamais_a_l_usine()
    {
        var helper = new HelperOk();
        var snapshot = new TweakSnapshot("gpu.undervolt", PrivilegedOpCodec.Serialize(new ApplyAdlxTuningOp(0, AdlxTuningKind.Undervolt, 2700)).Value!);

        var issue = await AnnulationParClic.AnnulerAsync(new Levier(helper), snapshot, helper);

        issue.Resultat.IsOk.Should().BeTrue();
        issue.Message.Should().BeNull();
        helper.Remises.Should().Be(0);
    }

    private sealed class HelperOk : IElevatedClient
    {
        public int Remises;
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        {
            if (op is ApplyAdlxTuningOp { Kind: AdlxTuningKind.RemiseUsine }) Remises++;
            return Task.FromResult(Result.Ok());
        }
    }
}
