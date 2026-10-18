using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Le journal des arrêts brutaux est une requête sur le journal Système : un scan le lit une fois, et chaque famille
// (arrêts, écrans bleus, garde de stabilité, incidents datés) reprend la même lecture.
public sealed class UnScanLitLesArretsUneFoisTests
{
    private sealed class JournalCompte : IUnexpectedShutdownLog
    {
        public int Lectures;

        public Result<IReadOnlyList<DateTimeOffset>> Since(DateTimeOffset earliest)
        {
            Interlocked.Increment(ref Lectures);
            return Result<IReadOnlyList<DateTimeOffset>>.Ok([DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-60)]);
        }
    }

    [Fact]
    public void Un_scan_lit_le_journal_des_arrets_une_seule_fois()
    {
        var journal = new JournalCompte();
        var audit = new RepairAuditService(new ServicesMuets(), new NoyauMuet(), journal, new MemoireMuette(), new VidagesMuets(), new WmiMuet());

        var constats = audit.Lire();

        journal.Lectures.Should().Be(1);
        constats.Should().Contain(c => c.Nom == "arrets brutaux", "témoin : la lecture unique sert bien les constats");
    }

    [Fact]
    public void Une_fenetre_courte_ne_rend_que_ses_arrets_et_une_plus_large_relit()
    {
        var journal = new JournalCompte();
        var maintenant = DateTimeOffset.UtcNow;
        var scan = new ArretsDuScan(journal, maintenant.AddDays(-90));

        scan.Since(maintenant.AddDays(-45)).Value.Should().ContainSingle("l'arrêt d'il y a 60 jours sort d'une fenêtre de 45");
        scan.Since(maintenant.AddDays(-90)).Value.Should().HaveCount(2);
        journal.Lectures.Should().Be(1);
        scan.Since(maintenant.AddDays(-120));
        journal.Lectures.Should().Be(2, "une fenêtre plus large que la lecture gardée relit le journal");
    }

    private sealed class JournalAvecBouton : IUnexpectedShutdownLog
    {
        public Result<IReadOnlyList<DateTimeOffset>> Since(DateTimeOffset earliest) => throw new NotSupportedException();

        public Result<IReadOnlyList<ArretBrutal>> Detail(DateTimeOffset earliest) => Result<IReadOnlyList<ArretBrutal>>.Ok(
        [
            new(DateTimeOffset.UtcNow.AddDays(-1), 0, BoutonMaintenu: true, TransitionEnCours: false),
            new(DateTimeOffset.UtcNow.AddDays(-2), 0, BoutonMaintenu: false, TransitionEnCours: false),
        ]);
    }

    // Même contrat que le vrai journal : un appui long sur le bouton n'est pas un arrêt brutal pour Since.
    [Fact]
    public void Since_ecarte_l_appui_long_sur_le_bouton_comme_le_vrai_journal()
    {
        var scan = new ArretsDuScan(new JournalAvecBouton());

        scan.Since(DateTimeOffset.UtcNow.AddDays(-10)).Value.Should().ContainSingle();
        scan.Detail(DateTimeOffset.UtcNow.AddDays(-10)).Value.Should().HaveCount(2, "Detail garde tout, l'appelant décide");
    }

    [Fact]
    public void Deux_scans_relisent_le_journal()
    {
        var journal = new JournalCompte();
        var audit = new RepairAuditService(new ServicesMuets(), new NoyauMuet(), journal, new MemoireMuette(), new VidagesMuets(), new WmiMuet());

        audit.Lire();
        audit.Lire();

        journal.Lectures.Should().Be(2, "un arrêt survenu entre deux scans doit se voir au second");
    }
}
