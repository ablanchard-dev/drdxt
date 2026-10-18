using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// Les overlays ouverts en partie, et leur coût mesuré sur les parties du joueur.
public sealed class OverlaysEnPartieTests
{
    private static PartieRelevee Partie(string jeu, double fps, IReadOnlyList<string>? overlays, string? essai = null)
        => new(jeu, DateTimeOffset.Now, DateTimeOffset.Now, 3, fps, fps * 0.6, BottleneckState.CpuBoundOrAppBound, Essai: essai, Overlays: overlays);

    // Discord compte, sous le nom de ce qui se voit vraiment : Discord ouvert (son overlay vit dans le jeu).
    [Fact]
    public void Seuls_les_processus_d_overlay_connus_sont_reconnus()
        => OverlaysEnPartie.Reconnaitre(["steam", "GameOverlayUI", "rtss", "Discord", "NVIDIA Overlay", "GameOverlayUI"])
            .Should().Equal("Discord (ouvert)", "NVIDIA", "RivaTuner", "Steam");

    [Fact]
    public void Trois_parties_de_chaque_cote_donnent_l_ecart_en_fps_median()
    {
        var (ecarts, autres) = OverlaysEnPartie.Comparer([
            Partie("cod.exe", 140, ["Steam"]), Partie("cod.exe", 150, ["Steam"]), Partie("cod.exe", 120, ["Steam"]),
            Partie("cod.exe", 160, []), Partie("cod.exe", 170, []), Partie("cod.exe", 165, []),
        ]);

        ecarts.Should().ContainSingle().Which.Should().Be(new OverlaysEnPartie.Ecart("cod.exe", "Steam", 140, 165, 3, 3, VerdictEnJeu.Pire));
        autres.Should().BeEmpty();
    }

    // Deux parties contre deux, sans aucun écart réel : toutes celles avec sous toutes celles sans arrive par hasard une fois
    // sur six (1 disposition sur C(4,2) = 6). Le verdict demande le même minimum que les autres juges.
    [Fact]
    public void Deux_parties_contre_deux_ne_donnent_pas_de_verdict()
    {
        var (ecarts, autres) = OverlaysEnPartie.Comparer([
            Partie("cod.exe", 140, ["Steam"]), Partie("cod.exe", 150, ["Steam"]),
            Partie("cod.exe", 160, []), Partie("cod.exe", 170, []),
        ]);

        ecarts.Should().BeEmpty();
        autres.Should().ContainSingle().Which.Should().Be(new OverlaysEnPartie.SansComparaison("cod.exe", "Steam", 2, 2));
    }

    // Les parties d'un autre estimateur du 1 % low ne comptent d'aucun côté : sans elles, il n'y a plus trois parties sans.
    [Fact]
    public void Les_parties_d_un_autre_estimateur_ne_se_comparent_pas()
    {
        static PartieRelevee N(PartieRelevee p) => p with { Low1SurToutesLesImages = true };
        var (ecarts, autres) = OverlaysEnPartie.Comparer([
            N(Partie("cod.exe", 140, ["Steam"])), N(Partie("cod.exe", 145, ["Steam"])), N(Partie("cod.exe", 150, ["Steam"])),
            N(Partie("cod.exe", 160, [])), N(Partie("cod.exe", 165, [])), Partie("cod.exe", 170, []),
        ]);

        ecarts.Should().BeEmpty();
        autres.Should().ContainSingle().Which.Should().Be(new OverlaysEnPartie.SansComparaison("cod.exe", "Steam", 3, 2));
    }

    // Un levier posé entre les parties avec et celles sans ; son écart n'est pas celui de l'overlay.
    [Fact]
    public void Seules_les_parties_d_apres_le_dernier_changement_se_comparent()
    {
        var t = DateTimeOffset.Now;
        PartieRelevee P(double h, double fps, params string[] o) => new("cod.exe", t.AddHours(h), t.AddHours(h).AddMinutes(20), 3, fps, fps * 0.6,
            BottleneckState.CpuBoundOrAppBound, Overlays: o);
        var parties = new[] { P(-30, 80, "RivaTuner"), P(-25, 80, "RivaTuner"), P(-20, 80, "RivaTuner"), P(10, 100), P(15, 100), P(20, 100) };

        OverlaysEnPartie.Comparer(parties).Ecarts.Should().ContainSingle();
        var (ecarts, autres) = OverlaysEnPartie.Comparer(parties, [t]);
        ecarts.Should().BeEmpty();
        autres.Should().BeEmpty("après le levier, aucune partie n'a vu RivaTuner");
    }

    // La règle des autres juges : séparation nette du 1 % low, sinon pas de différence nette.
    [Fact]
    public void Le_verdict_ne_tombe_que_sur_une_separation_nette_du_1_pour_cent_low()
    {
        var (ecarts, _) = OverlaysEnPartie.Comparer([
            Partie("cod.exe", 140, ["Steam"]), Partie("cod.exe", 145, ["Steam"]), Partie("cod.exe", 170, ["Steam"]),
            Partie("cod.exe", 150, []), Partie("cod.exe", 155, []), Partie("cod.exe", 160, []),
        ]);
        ecarts.Single().Verdict.Should().Be(VerdictEnJeu.PasDeDifference, "170 avec passe au-dessus de 150 sans");

        var (mieux, _) = OverlaysEnPartie.Comparer([
            Partie("cod.exe", 170, ["Steam"]), Partie("cod.exe", 175, ["Steam"]), Partie("cod.exe", 180, ["Steam"]),
            Partie("cod.exe", 150, []), Partie("cod.exe", 155, []), Partie("cod.exe", 160, []),
        ]);
        mieux.Single().Verdict.Should().Be(VerdictEnJeu.Mieux);
    }

    [Fact]
    public void Une_seule_partie_sans_l_overlay_ne_donne_pas_de_chiffre()
    {
        var (ecarts, autres) = OverlaysEnPartie.Comparer([
            Partie("cod.exe", 140, ["RivaTuner"]), Partie("cod.exe", 150, ["RivaTuner"]), Partie("cod.exe", 170, []),
        ]);

        ecarts.Should().BeEmpty();
        autres.Should().ContainSingle().Which.Should().Be(new OverlaysEnPartie.SansComparaison("cod.exe", "RivaTuner", 2, 1));
    }

    [Fact]
    public void Ni_un_autre_jeu_ni_une_partie_non_lue_ni_une_partie_en_essai_ne_comptent_sans_l_overlay()
    {
        var (ecarts, autres) = OverlaysEnPartie.Comparer([
            Partie("cod.exe", 140, ["Steam"]), Partie("cod.exe", 150, ["Steam"]),
            Partie("JeuB.exe", 300, []), Partie("JeuB.exe", 310, []),
            Partie("cod.exe", 200, null), Partie("cod.exe", 210, [], essai: "gpu.power_limit"),
        ]);

        ecarts.Should().BeEmpty();
        autres.Should().ContainSingle().Which.PartiesSans.Should().Be(0);
    }

    [Fact]
    public void La_partie_garde_tout_overlay_vu_sur_une_capture_et_rien_si_aucun_releve_n_a_ete_lu()
    {
        CaptureEnPartie C(IReadOnlyList<string>? o) => new(DateTimeOffset.Now, 140, 90, BottleneckState.CpuBoundOrAppBound, Overlays: o);

        ReleveDesParties.Resumer("cod.exe", [C(["Steam"]), C(null), C(["RivaTuner", "Steam"])])!.Overlays
            .Should().Equal("RivaTuner", "Steam");
        ReleveDesParties.Resumer("cod.exe", [C(null), C(null)])!.Overlays.Should().BeNull();
        ReleveDesParties.Resumer("cod.exe", [C([]), C(null)])!.Overlays.Should().BeEmpty("lu et rien vu, ce n'est pas « pas lu »");
    }

    [Fact]
    public async Task La_veille_releve_les_overlays_a_chaque_capture()
    {
        var etats = new Queue<(string, int)?>([("cod.exe", 42), ("cod.exe", 42), null]);
        var lus = new Queue<IReadOnlyList<string>?>([["Steam"], ["NVIDIA"]]);
        var enregistrees = new List<PartieRelevee>();
        using var fin = new CancellationTokenSource();

        var veille = new VeilleDesParties(
            jeu: () => etats.Count > 0 ? etats.Dequeue() : null,
            capturer: (_, _, _) => Task.FromResult<BenchmarkResult?>(new BenchmarkResult(140, 98, 70, [], null, 1, 1)),
            classer: _ => BottleneckState.CpuBoundOrAppBound,
            enregistrer: p => { enregistrees.Add(p); fin.Cancel(); },
            patienter: (_, _) => Task.CompletedTask,
            overlays: () => lus.Dequeue());

        await veille.VeillerAsync(fin.Token);

        enregistrees[0].Overlays.Should().Equal("NVIDIA", "Steam");
    }
}
