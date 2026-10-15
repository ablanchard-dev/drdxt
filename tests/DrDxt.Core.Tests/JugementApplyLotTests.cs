using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Le veto des arrets brutaux, sur la surface ecrite — et le lot qu'il laisse.
///
/// Le veto des arrêts fermait sur les grandeurs DECLAREES (`gpu.`, `cpu.`, `ram.`) ; trois
/// leviers qui ecrivent l'etat du pilote NVIDIA se declaraient « latence » et passaient. Un second
/// veto propre a la campagne les avait ecartes de `tools/optimize`, pas de l'App. Le veto ferme
/// maintenant sur <see cref="SurfaceTouchee"/>, une seule definition, lue par l'App et la campagne.
///
/// Les tests enumerent le CATALOGUE entier plutot qu'une liste de noms : un levier ajoute demain
/// sans surface declaree est refuse, et le test qui l'exige rougit s'il passe.
/// </summary>
public class JugementApplyLotTests
{
    private static IReadOnlyList<ArretClasse> ArretsRecents(int n) =>
        Enumerable.Range(1, n).Select(j => ArretClasse.SansDetail(DateTimeOffset.UtcNow.AddDays(-j))).ToList();

    private static DiagnosticMachine Diagnostic(IReadOnlyList<ArretClasse>? arrets) =>
        DiagnosticDeScan.Construire(goulot: null, mesure: null, instabiliteGpu: null, arretsBrutaux: arrets);

    private static readonly SurfaceTouchee[] Fermees =
    {
        SurfaceTouchee.NonDeclaree, SurfaceTouchee.Processeur, SurfaceTouchee.Memoire,
        SurfaceTouchee.CarteGraphique, SurfaceTouchee.PiloteGraphique, SurfaceTouchee.Affichage,
    };

    // Un reglage de confort se pose a part, sans banc et sans jeu ; l'autopilote ne le tente jamais.
    [Fact]
    public void L_ecran_se_pose_sans_mesure_meme_sans_jeu_et_jamais_en_session()
    {
        var j = JugementApply.Juger(Diagnostic(null), ["fixit.ecran_frequence"], jeuEnCours: false);

        j.PosesSansMesure.Should().Equal("fixit.ecran_frequence");
        j.LotAChaud.Should().BeEmpty();
        j.AuRedemarrage.Should().BeEmpty();
        j.RienATenter.Should().BeFalse();
    }

    [Fact]
    public void Chaque_levier_du_catalogue_DECLARE_ce_qu_il_ecrit()
        => CatalogueDeCandidats.Ids.Select(id => (id, CatalogueDeCandidats.Decrire(id)!.Surface))
            .Should().NotContain(x => x.Surface == SurfaceTouchee.NonDeclaree,
                "un levier sans surface serait refuse partout : l'oubli doit se voir ici, pas en production");

    [Fact]
    public void Catalogue_entier_et_arrets_secs_repetes_rien_n_est_ferme_par_les_arrets()
    {
        // Les arrêts ne ferment plus le processeur ni la mémoire, ni la carte ni le pilote graphique. La garde d'incident rend ce qui précède un arrêt.
        var ids = CatalogueDeCandidats.Ids.ToList();
        var j = JugementApply.Juger(Diagnostic(ArretsRecents(3)), ids, jeuEnCours: true);

        foreach (var id in ids)
            j.Appreciations.Single(x => x.Id == id).Motifs.Should().NotContain(
                m => m.Contains("arrêts brutaux hors veille") || m.Contains("serait inattribuable"), $"{id} : les arrets ne refusent plus");
        j.VetoDesArrets.Should().Contain("sous garde");
    }

    [Fact]
    public void Les_trois_leviers_du_pilote_trouves_au_rejeu_du_11_09_passent_sous_garde()
    {
        // Fermés par les arrêts (ils écrivent le pilote). La garde d'incident les rend après un
        // arrêt ; les arrêts ne les ferment plus.
        var j = JugementApply.Juger(Diagnostic(ArretsRecents(7)),
            new[] { "latency.reflex", "latency.render_queue_cap", "latency.vrr_vsync_cap" }, jeuEnCours: true);

        j.Appreciations.Should().OnlyContain(x => !x.Motifs.Any(m => m.Contains("serait inattribuable")));
    }

    [Fact]
    public void Un_levier_SANS_surface_declaree_est_refuse_par_les_arrets()
    {
        // Le levier de demain, oublie dans la table : il se dit « latence » comme les trois d'hier.
        var demain = new CandidateDescriptor("latency.demain", CandidateKind.AutoHot, 1, false,
            new[] { CandidateSelector.GrandeurLatence });

        var a = CandidateSelector.Evaluer(Diagnostic(ArretsRecents(3)), new[] { demain }).Single();

        a.Retenu.Should().BeFalse("surface absente = refus, jamais laisse passer");
        a.Motifs.Should().Contain(m => m.Contains("arrêts brutaux"));
    }

    [Fact]
    public void Au_redemarrage_les_corrections_OS_utilisateur_HAGS_et_le_fichier_d_echange_restent()
    {
        var j = JugementApply.Juger(Diagnostic(ArretsRecents(3)),
            new[] { "latency.gameModeOn", "latency.gameDvrBackgroundOff", "latency.vrrFlipModelOn",
                    "latency.hagsOn", "fixit.sane_pagefile" }, jeuEnCours: true);

        // Le fichier d'échange (mémoire) passe sous garde ; 09/10/2026 : HAGS (pilote graphique) aussi.
        j.AuRedemarrage.Should().BeEquivalentTo("latency.gameModeOn", "latency.gameDvrBackgroundOff", "latency.vrrFlipModelOn",
            "latency.hagsOn", "fixit.sane_pagefile");
    }

    [Fact]
    public void Sans_arrets_le_lot_porte_plusieurs_candidats_et_l_App_n_en_prend_que_le_premier()
    {
        var j = JugementApply.Juger(Diagnostic(null),
            new[] { "fixit.power_plan", "fixit.x3d_ccd", "latency.render_queue_cap" }, jeuEnCours: true);

        // Goulot non etabli : aucune de ces familles ne passe devant, l'ordre du plan tient.
        j.LotAChaud.Select(d => d.Id).Should().Equal("fixit.power_plan", "fixit.x3d_ccd", "latency.render_queue_cap");
        j.VetoDesArrets.Should().BeNull();
        j.AChaud!.Id.Should().Be("fixit.power_plan", "l'Optimiser de l'App ne tente que le premier du lot");
    }

    // ── Le veto d'instabilite GRAPHIQUE ferme sur la surface, lui aussi ────────────────

    private static readonly SurfaceTouchee[] FermeesParLeNoyauGraphique =
        { SurfaceTouchee.NonDeclaree, SurfaceTouchee.CarteGraphique, SurfaceTouchee.PiloteGraphique };

    /// <summary>Des incidents du noyau graphique REPETES et recents, et ZERO arret brutal (lu, vide) :
    /// seul le veto graphique peut fermer quoi que ce soit.</summary>
    private static DiagnosticMachine IncidentsGraphiquesSansArrets() => DiagnosticDeScan.Construire(
        goulot: null, mesure: null,
        instabiliteGpu: new InstabiliteGpuObservee(3, DateTimeOffset.UtcNow.AddDays(-2),
            "0x193 VIDEO_DXGKRNL_LIVEDUMP (parametre 1 : 0x80e)"),
        arretsBrutaux: Array.Empty<ArretClasse>());

    // Les incidents du pilote ne ferment plus
    // la carte ni le pilote ; la garde d'incident surveille les vidages à chaud dans la semaine après une pose et rend seule.
    [Fact]
    public void Catalogue_entier_incidents_graphiques_et_zero_arret_rien_n_est_ferme_sauf_une_surface_non_declaree()
    {
        var ids = CatalogueDeCandidats.Ids.ToList();
        var j = JugementApply.Juger(IncidentsGraphiquesSansArrets(), ids, jeuEnCours: true);

        j.VetoDesArrets.Should().BeNull("aucun arret brutal");
        foreach (var id in ids)
            j.Appreciations.Single(x => x.Id == id).Motifs
                .Should().NotContain(m => m.Contains("incidents noyau graphique"), $"{id} passe sous garde, la garde rend après un vidage à chaud");
    }

    [Fact]
    public void Incidents_graphiques_sans_arrets_les_leviers_du_pilote_passent_sous_garde()
    {
        var j = JugementApply.Juger(IncidentsGraphiquesSansArrets(),
            new[] { "latency.reflex", "latency.render_queue_cap", "latency.vrr_vsync_cap", "latency.hagsOn",
                    "fixit.power_plan", "latency.gameModeOn" }, jeuEnCours: true);

        j.Appreciations.Should().OnlyContain(a => !a.Motifs.Any(m => m.Contains("incidents noyau graphique")));
        j.AuRedemarrage.Should().Contain("latency.hagsOn");
    }

    [Fact]
    public void Un_levier_SANS_surface_declaree_est_refuse_par_les_incidents_graphiques()
    {
        var demain = new CandidateDescriptor("latency.demain", CandidateKind.AutoHot, 1, false,
            new[] { CandidateSelector.GrandeurLatence });

        CandidateSelector.Evaluer(IncidentsGraphiquesSansArrets(), new[] { demain }).Single()
            .Motifs.Should().Contain(m => m.Contains("incidents noyau graphique"));
    }

    [Fact]
    public void Un_arret_isole_n_est_pas_une_condition_et_ne_ferme_rien()
    {
        // Le seuil du selecteur (2 dans la fenetre) : un seul arret est un accident.
        var j = JugementApply.Juger(Diagnostic(ArretsRecents(1)), new[] { "fixit.power_plan", "latency.reflex" }, jeuEnCours: true);

        j.LotAChaud.Select(d => d.Id).Should().Equal("fixit.power_plan", "latency.reflex");
        j.VetoDesArrets.Should().BeNull();
    }
    // Changer le mode d'écran sort un jeu plein écran exclusif et cache la question
    // « garder ou revenir ». Jeu lancé : refusé, avec son motif ; jeu fermé : posé au clic.
    [Fact]
    public void L_ecran_se_regle_jeu_ferme_jamais_en_partie()
    {
        var d = DiagnosticDeScan.Construire(null, null, null, null);

        var enPartie = JugementApply.Juger(d, ["fixit.ecran_frequence"], jeuEnCours: true);
        enPartie.PosesSansMesure.Should().BeEmpty();
        enPartie.Appreciations.Single().Motifs.Should().Contain(m => m.Contains("jeu fermé"));

        JugementApply.Juger(d, ["fixit.ecran_frequence"], jeuEnCours: false).PosesSansMesure.Should().Equal("fixit.ecran_frequence");
    }
}
