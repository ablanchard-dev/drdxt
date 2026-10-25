using DrDxt.Core;
using DrDxt.ElevatedHelper.Journal;
using DrDxt.Reconciler.Desinstallation;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.ReconcilerHost.Tests;

/// <summary>Le rejeu du journal au démarrage du service : il ne retient jamais le démarrage.</summary>
public sealed class RejeuAuDemarrageTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "drdxt-rejeu-service-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dossier, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    [Fact]
    public async Task Sans_journal_le_service_demarre_sans_rien_ecrire()
    {
        var ecriture = Substitute.For<IElevatedClient>();

        var rejeu = new RejeuAuDemarrage(new JournalDuService(null), Substitute.For<ILectureDesCibles>(), ecriture, jeuEnCours: () => false);
        await rejeu.StartAsync(CancellationToken.None);
        await rejeu.Differe;

        ecriture.ReceivedCalls().Should().HaveCount(0);
    }

    // Une lecture qui lève pendant le rejeu : le service démarre quand même, et le dit.
    [Fact]
    public async Task Un_rejeu_qui_leve_ne_retient_pas_le_demarrage()
    {
        var journal = new JournalDEcritureAnticipee(Path.Combine(_dossier, "journal.json"));
        journal.Ajouter(new SetRegistryValueOp(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
            "HwSchMode", RegistryValueType.DWord, "2"), "1", new AppelantDuJournal("S-1-5-21-1", 4242, DateTimeOffset.UnixEpoch),
            DateTimeOffset.UtcNow).IsOk.Should().BeTrue();
        var lecture = Substitute.For<ILectureDesCibles>();
        lecture.CibleExiste(Arg.Any<PrivilegedOp>()).Returns(_ => throw new InvalidOperationException("pilote"));
        lecture.Lire(Arg.Any<PrivilegedOp>()).Returns(_ => throw new InvalidOperationException("pilote"));
        var diagnostics = Substitute.For<IDiagnosticsLog>();

        var rejeu = new RejeuAuDemarrage(new JournalDuService(journal), lecture, Substitute.For<IElevatedClient>(), diagnostics, jeuEnCours: () => false);
        var demarrer = async () => { await rejeu.StartAsync(CancellationToken.None); await rejeu.Differe; };

        await demarrer.Should().NotThrowAsync();
        diagnostics.ReceivedCalls().Should().NotBeEmpty();
        journal.EnAttente().Value.Should().ContainSingle("une écriture non rendue reste pour le rejeu suivant");
    }

    private sealed class DelaiCompte : IDelay
    {
        public int Attentes;
        public Task DelayAsync(TimeSpan duration, CancellationToken ct) { Attentes++; return Task.CompletedTask; }
    }

    // Le service démarre aussi pendant une partie (tâche cochée à l'installation, récupération après un plantage, sc start) :
    // le rejeu attend la fin du jeu, comme la passe quotidienne du Worker, sans retenir le démarrage.
    [Fact]
    public async Task Un_jeu_lance_au_demarrage_du_service_retarde_le_rejeu_jusqu_a_sa_fin()
    {
        var journal = new JournalDEcritureAnticipee(Path.Combine(_dossier, "journal.json"));
        journal.Ajouter(new SetRegistryValueOp(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
            "HwSchMode", RegistryValueType.DWord, "2"), "1", new AppelantDuJournal("S-1-5-21-1", 4242, DateTimeOffset.UnixEpoch),
            DateTimeOffset.UtcNow).IsOk.Should().BeTrue();
        // La machine porte la valeur posée ; l'écriture acceptée s'y voit à la relecture, comme sur la vraie : le rendu n'est dit
        // fait que relu à l'origine.
        var valeur = "2";
        var lecture = Substitute.For<ILectureDesCibles>();
        lecture.Lire(Arg.Any<PrivilegedOp>()).Returns(_ => Result<string?>.Ok(valeur));
        lecture.CibleExiste(Arg.Any<PrivilegedOp>()).Returns(Result<bool>.Ok(true));
        var ecriture = Substitute.For<IElevatedClient>();
        ecriture.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            if (c.Arg<PrivilegedOp>() is SetRegistryValueOp s) valeur = s.Value;
            return Result.Ok();
        });
        var enJeu = new Queue<bool>([true, true, false]);
        var delai = new DelaiCompte();
        var rejeu = new RejeuAuDemarrage(new JournalDuService(journal), lecture, ecriture, jeuEnCours: () => enJeu.Count > 0 && enJeu.Dequeue(), delai: delai);

        await rejeu.StartAsync(CancellationToken.None);
        await rejeu.Differe.WaitAsync(TimeSpan.FromSeconds(10));

        delai.Attentes.Should().Be(2, "une attente par passage en jeu, comme le Worker");
        await ecriture.Received(1).ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
        journal.EnAttente().Value.Should().HaveCount(0);
    }

    // Le service voit un jeu par son seul nom de processus : un « cod.exe » qui ne finit jamais (lancé par n'importe quel
    // programme du compte) retiendrait le rejeu pour toujours sans plafond. Passé le plafond, le rejeu part quand même.
    [Fact]
    public async Task Un_jeu_qui_ne_finit_jamais_ne_retient_le_rejeu_que_jusqu_au_plafond()
    {
        var journal = new JournalDEcritureAnticipee(Path.Combine(_dossier, "journal.json"));
        journal.Ajouter(new SetRegistryValueOp(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
            "HwSchMode", RegistryValueType.DWord, "2"), "1", new AppelantDuJournal("S-1-5-21-1", 4242, DateTimeOffset.UnixEpoch),
            DateTimeOffset.UtcNow).IsOk.Should().BeTrue();
        // L'écriture acceptée se voit à la relecture, comme sur la vraie machine.
        var valeur = "2";
        var lecture = Substitute.For<ILectureDesCibles>();
        lecture.Lire(Arg.Any<PrivilegedOp>()).Returns(_ => Result<string?>.Ok(valeur));
        lecture.CibleExiste(Arg.Any<PrivilegedOp>()).Returns(Result<bool>.Ok(true));
        var ecriture = Substitute.For<IElevatedClient>();
        ecriture.ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            if (c.Arg<PrivilegedOp>() is SetRegistryValueOp s) valeur = s.Value;
            return Result.Ok();
        });
        var delai = new DelaiCompte();
        var rejeu = new RejeuAuDemarrage(new JournalDuService(journal), lecture, ecriture, jeuEnCours: () => true, delai: delai);

        await rejeu.StartAsync(CancellationToken.None);
        await rejeu.Differe.WaitAsync(TimeSpan.FromSeconds(10));

        delai.Attentes.Should().Be((int)(Worker.AttenteEnPartieMax / Worker.AttenteEnPartie));
        await ecriture.Received(1).ExecuteAsync(Arg.Any<PrivilegedOp>(), Arg.Any<CancellationToken>());
        journal.EnAttente().Value.Should().BeEmpty("rendu, et relu à l'origine");
    }

    /// <summary>Une partie qui ne finit pas : l'attente tient jusqu'à l'arrêt.</summary>
    private sealed class DelaiReel : IDelay
    {
        public Task DelayAsync(TimeSpan duration, CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    // Le gestionnaire de services borne le démarrage à quelques dizaines de secondes : une partie de deux heures ne le retient
    // pas, et un arrêt pendant l'attente l'annule sans exception.
    [Fact]
    public async Task Une_partie_en_cours_ne_retient_ni_le_demarrage_ni_l_arret()
    {
        var ecriture = Substitute.For<IElevatedClient>();
        var rejeu = new RejeuAuDemarrage(new JournalDuService(null), Substitute.For<ILectureDesCibles>(), ecriture,
            jeuEnCours: () => true, delai: new DelaiReel());

        await rejeu.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        rejeu.Differe.IsCompleted.Should().BeFalse("la partie continue");

        var arreter = () => rejeu.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await arreter.Should().NotThrowAsync();
        rejeu.Differe.IsCompleted.Should().BeTrue();
        ecriture.ReceivedCalls().Should().HaveCount(0);
    }
}
