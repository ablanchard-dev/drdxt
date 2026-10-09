using DrDxt.Application.Network;
using DrDxt.Measurement.Network;
using FluentAssertions;

namespace DrDxt.Application.Tests.Network;

public class VeilleDuPingTests
{
    // Une fenetre de jeu ou l'envoi sature et le ping monte de 30 ms : 25 au repos, 25 en charge.
    private static IReadOnlyList<EchantillonEnvoi> FenetreSaturee(int n = 25)
        => Enumerable.Range(0, n).Select(_ => new EchantillonEnvoi(1, 10))
            .Concat(Enumerable.Range(0, n).Select(_ => new EchantillonEnvoi(100, 40))).ToList();

    // L'envoi sature mais le ping ne bouge pas : rien a corriger.
    private static IReadOnlyList<EchantillonEnvoi> FenetreSaine()
        => Enumerable.Range(0, 25).Select(_ => new EchantillonEnvoi(1, 10))
            .Concat(Enumerable.Range(0, 25).Select(_ => new EchantillonEnvoi(100, 12))).ToList();

    private static VeilleDuPing Veille(Func<string?> jeu, Queue<IReadOnlyList<EchantillonEnvoi>> fenetres, List<string>? journal = null)
        => new(jeu,
            (_, _) =>
            {
                journal?.Add("collecte");
                return Task.FromResult(fenetres.Count > 0 ? fenetres.Dequeue() : throw new OperationCanceledException());
            },
            TimeSpan.FromSeconds(20),
            (_, _) => { journal?.Add("attente"); return Task.CompletedTask; });

    [Fact]
    public async Task Un_bufferbloat_vu_en_partie_donne_le_jeu_et_un_plafond_mesure()
    {
        var veille = Veille(() => "cod.exe", new(new[] { FenetreSaturee() }));

        var (exe, verdict) = await veille.AttendreUnBufferbloatAsync(CancellationToken.None);

        exe.Should().Be("cod.exe");
        verdict.Detecte.Should().BeTrue();
        verdict.PlafondConseilleBps.Should().Be(50_000_000, "50 % du plus fort envoi vu : le meme juge que le scan");
    }

    [Fact]
    public async Task Hors_partie_rien_n_est_mesure()
    {
        var journal = new List<string>();
        var enJeu = 0;
        var veille = Veille(() => enJeu++ < 3 ? null : "cod.exe", new(new[] { FenetreSaturee() }), journal);

        await veille.AttendreUnBufferbloatAsync(CancellationToken.None);

        journal.Take(3).Should().AllBe("attente", "sans jeu, la veille attend sans collecter");
        journal.Should().Contain("collecte");
    }

    [Fact]
    public async Task Un_envoi_sain_ne_pose_rien_et_la_veille_continue()
    {
        var veille = Veille(() => "cod.exe", new(new[] { FenetreSaine() }));

        var attente = () => veille.AttendreUnBufferbloatAsync(CancellationToken.None);

        await attente.Should().ThrowAsync<OperationCanceledException>("aucun verdict detecte : elle n'a rien rendu");
        veille.DernierVerdict!.Detecte.Should().BeFalse();
        veille.DernierVerdict.PlafondConseilleBps.Should().Be(0, "jamais de plafond sans preuve");
    }

    [Fact]
    public async Task Les_parties_s_additionnent_jusqu_au_verdict()
    {
        // Chaque partie ne voit que 12 echantillons de chaque cote, sous le minimum de 20 : seules, elles
        // ne prouvent rien ; ensemble, elles suffisent.
        var veille = Veille(() => "cod.exe", new(new[] { FenetreSaturee(12), FenetreSaturee(12) }));

        var (_, verdict) = await veille.AttendreUnBufferbloatAsync(CancellationToken.None);

        verdict.NRepos.Should().Be(24);
        veille.EchantillonsRetenus.Should().Be(48);
    }

    [Fact]
    public async Task Changer_de_jeu_repart_de_zero()
    {
        var journal = new List<string>();
        var jeux = new Queue<string>(new[] { "a.exe", "a.exe", "b.exe", "b.exe", "b.exe", "b.exe" });
        var veille = Veille(() => jeux.Count > 1 ? jeux.Dequeue() : jeux.Peek(),
            new(new[] { FenetreSaturee(12), FenetreSaturee(12), FenetreSaturee(12) }), journal);

        var (exe, verdict) = await veille.AttendreUnBufferbloatAsync(CancellationToken.None);

        exe.Should().Be("b.exe");
        journal.Count(e => e == "collecte").Should().Be(3,
            "les echantillons de a.exe ne comptent pas pour b.exe : il faut deux fenetres de b pour conclure");
        verdict.NRepos.Should().Be(24);
    }

    [Fact]
    public async Task Une_fenetre_ou_le_jeu_s_est_ferme_ne_compte_pas()
    {
        // Le jeu est vu avant la collecte, plus apres : la fenetre (saturee) est jetee.
        var vus = new Queue<string?>(new[] { "cod.exe", null, "cod.exe", "cod.exe" });
        var veille = Veille(() => vus.Count > 1 ? vus.Dequeue() : vus.Peek(),
            new(new[] { FenetreSaturee(), FenetreSaturee(12), FenetreSaturee(12) }));

        var (_, verdict) = await veille.AttendreUnBufferbloatAsync(CancellationToken.None);

        verdict.NRepos.Should().Be(24, "la premiere fenetre decrivait un jeu ferme");
    }
}
