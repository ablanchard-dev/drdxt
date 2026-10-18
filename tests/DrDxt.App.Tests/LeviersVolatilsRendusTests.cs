using DrDxt.Tests.Shared;
using System.IO;
using DrDxt.App.ViewModels;
using DrDxt.Application.Parties;
using DrDxt.Application.Tweaks;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

// Un réglage volatil sans lecture (la limite de la carte) posé avant le dernier démarrage n'est plus en place :
// il sort du journal. Sinon « Ce que DrDXT a posé » dirait la limite de la carte à 90 % après un redémarrage qui
// l'a remise à 100 %.
public sealed class LeviersVolatilsRendusTests : IDisposable
{
    private readonly string _dossier = Directory.CreateTempSubdirectory("drdxt-volatils-").FullName;
    public void Dispose() => Directory.Delete(_dossier, true);

    private static readonly DateTimeOffset Demarrage = new(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);

    private (ScanViewModel Vm, JournalDesLeviersAppliques Leviers) Vm(string id, DateTimeOffset? posele)
    {
        var chemin = Path.Combine(_dossier, "leviers.json");
        File.WriteAllText(chemin, System.Text.Json.JsonSerializer.Serialize(new[] { new TweakSnapshot(id, InversesDeTest.Pour(id, "100"), "90") }));
        var leviers = new JournalDesLeviersAppliques(chemin);
        var changements = new JournalDesChangements(Path.Combine(_dossier, "changements.json"));
        if (posele is { } quand) changements.Ajouter(new Changement(quand, id, null, "pose:" + id));
        var vm = ScanViewModelTestSeams.Build(Substitute.For<IHardwareDetector>(), Substitute.For<ISettingsStateReader>(),
            Substitute.For<IBenchmarkRunner>(), Substitute.For<IScanScorer>(), Substitute.For<IStorageDetector>(), k => k,
            journalLeviers: leviers);
        vm.JournalChangements = changements;
        vm.LevierParId = demande => { var t = Substitute.For<ITweak>(); t.Id.Returns(demande); return t; };
        vm.DemarrageDuPc = () => Demarrage;
        return (vm, leviers);
    }

    [Fact]
    public void La_limite_de_la_carte_posee_avant_le_demarrage_sort_du_journal()
    {
        var (vm, leviers) = Vm("gpu.power_limit", Demarrage.AddDays(-1));
        leviers.Lire().Should().NotBeEmpty("témoin : le journal porte bien le réglage avant le nettoyage");
        vm.RetirerLeviersVolatilsRendus();
        leviers.Lire().Should().BeEmpty("le redémarrage l'a remise à sa valeur");
    }

    [Fact]
    public void Posee_depuis_le_demarrage_ou_sans_date_ou_durable_elle_reste()
    {
        var (apres, l1) = Vm("gpu.power_limit", Demarrage.AddHours(2));
        apres.RetirerLeviersVolatilsRendus();
        l1.Lire().Should().ContainSingle("posée après le démarrage : toujours en place");

        Dispose(); Directory.CreateDirectory(_dossier);
        var (sansDate, l2) = Vm("gpu.power_limit", null);
        sansDate.RetirerLeviersVolatilsRendus();
        l2.Lire().Should().ContainSingle("sans date de pose, rien n'est conclu");

        Dispose(); Directory.CreateDirectory(_dossier);
        var (durable, l3) = Vm("fixit.power_plan", Demarrage.AddDays(-1));
        durable.RetirerLeviersVolatilsRendus();
        l3.Lire().Should().ContainSingle("un plan d'alimentation survit au redémarrage");
    }
}
