using System.IO;
using System.Text.Json;
using DrDxt.App.ViewModels;
using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// L'essai en alternance quand le joueur joue l'App fermée : c'est le relevé de fond qui voit les parties finir. Les données
/// sont celles de la machine de référence (essai-alternance.json et parties.json) : gpu.power_limit,
/// origine éteinte ; relevé sur la machine : débuté le 10/10, éteint depuis le démarrage du 23/10 07:56, seize parties de JeuB marquées.
/// </summary>
public sealed partial class EssaiEnAlternanceTests
{
    private const string JeuB = "JeuBClient-Win64-Shipping.exe";

    // Les seize parties marquées de l'anneau : « on » les 16/10 et 18/10, « off » toutes les autres.
    private static readonly (string Debut, bool Pose)[] PartiesDeLEssai =
    [
        ("2026-10-15T00:19:00+02:00", false), ("2026-10-16T01:48:00+02:00", true), ("2026-10-17T02:03:00+02:00", false),
        ("2026-10-18T08:52:00+02:00", true), ("2026-10-19T01:15:00+02:00", false), ("2026-10-19T03:14:00+02:00", false),
        ("2026-10-19T08:16:00+02:00", false), ("2026-10-19T10:32:00+02:00", false), ("2026-10-20T07:35:00+02:00", false),
        ("2026-10-21T07:53:00+02:00", false), ("2026-10-21T17:09:00+02:00", false), ("2026-10-22T09:28:00+02:00", false),
        ("2026-10-23T11:35:00+02:00", false), ("2026-10-23T13:37:00+02:00", false), ("2026-10-24T13:35:00+02:00", false),
        ("2026-10-25T10:36:00+01:00", false),
    ];

    /// <summary>Le ViewModel tel que le relevé de fond le monte (App.ReleverEnFond) : l'essai, les parties, le seul levier de
    /// l'essai et la question « le helper tourne-t-il déjà ? ».</summary>
    private ScanViewModel ReleveDeFondSurLesDonneesReelles(bool helperLance)
    {
        var vm = Vm();
        vm.LevierParId = null;
        vm.LevierDeLEssaiParId = demande => demande == Id ? _levier : null;
        vm.HelperJoignableSansUac = () => Task.FromResult(helperLance);
        vm.JeuOuvertPourLeTest = () => false;
        var essai = new EssaiEnAlternance(Id, PoseALOrigine: false, PoseMaintenant: false,
            DateTimeOffset.Parse("2026-10-10T18:49:46+02:00"), EnPause: false, DerniereBascule: DateTimeOffset.Parse("2026-10-23T07:56:34+02:00"));
        vm.MemoireEssai!.Ecrire(essai).IsOk.Should().BeTrue();
        var parties = PartiesDeLEssai.Select(p =>
        {
            var debut = DateTimeOffset.Parse(p.Debut);
            return new PartieRelevee(JeuB, debut, debut.AddMinutes(90), 4, 103, 70, BottleneckState.CpuBoundOrAppBound,
                Essai: AlternanceDesEssais.Marque(Id, p.Pose));
        }).OrderByDescending(p => p.Debut).ToList();
        File.WriteAllText(Path.Combine(_dossier, "parties.json"), JsonSerializer.Serialize(parties));
        _demarrage = DateTimeOffset.Parse("2026-10-26T09:00:00+01:00");
        _maintenant = DateTimeOffset.Parse("2026-10-29T20:00:00+01:00");
        return vm;
    }

    private async Task JouerLAppFermee(ScanViewModel vm, int parties)
    {
        for (var i = 0; i < parties; i++)
        {
            _maintenant = _maintenant.AddHours(3);
            vm.EnregistrerPartie(new PartieRelevee(JeuB, _maintenant.AddMinutes(-120), _maintenant, 4, 103, 70,
                BottleneckState.CpuBoundOrAppBound));
            await vm.DerniereAvanceeEssai;
        }
    }

    // Les blocs A-B-B-A demandent « avec » pour la partie suivante (seize parties jouées : rang 0). Le joueur ne pense pas à
    // l'essai : il joue, l'App fermée, le helper déjà lancé par autre chose. L'essai avance quand même, sans invite Windows.
    [Fact]
    public async Task L_essai_avance_quand_le_joueur_joue_l_App_fermee()
    {
        var vm = ReleveDeFondSurLesDonneesReelles(helperLance: true);
        var sansGeste = new List<bool>();
        _levier.When(l => l.ApplyAsync()).Do(_ => sansGeste.Add(SansGesteDuJoueur.Actif));
        AlternanceDesEssais.Suivante(vm.MemoireParties!.Lire(), Id, poseALOrigine: false,
            DateTimeOffset.Parse("2026-10-10T18:49:46+02:00")).Pose.Should().BeTrue("le plan demande la partie suivante « avec »");

        await JouerLAppFermee(vm, 4);

        _poses.Should().BeGreaterThan(0, "quatre parties finies, jeu fermé : le levier aurait dû être posé au moins une fois");
        sansGeste.Should().NotBeEmpty().And.OnlyContain(actif => actif, "personne n'a cliqué : le helper n'est jamais lancé");
        vm.MemoireParties!.Lire().Count(p => p.Essai == AlternanceDesEssais.Marque(Id, true)).Should().BeGreaterThan(2,
            "les parties jouées après la pose comptent « avec »");
    }

    // L'inverse : le helper ne tourne pas. Rien ne se pose, le helper n'est pas sollicité (aucune invite Windows), et le joueur
    // est prévenu une fois que l'essai attend l'App.
    [Fact]
    public async Task Helper_absent_le_releve_ne_pose_rien_et_previent()
    {
        var vm = ReleveDeFondSurLesDonneesReelles(helperLance: false);
        var notes = new List<string>();
        vm.NotifierEssaiEnAttente = notes.Add;

        await JouerLAppFermee(vm, 2);

        (_poses, _rendus).Should().Be((0, 0), "sans helper lancé, le levier n'est jamais appelé : aucune invite Windows");
        await _levier.DidNotReceive().ApplyAsync();
        notes.Should().ContainSingle().Which.Should().Be("Essai.Notification");
        vm.EssaiEnAttente.Should().BeTrue();
    }
}

public sealed partial class EssaiEnAlternanceTests
{
    // Posé jeu fermé, puis le joueur enchaîne les parties d'un autre jeu encore ouvert : les blocs demandent « sans », mais rien
    // ne s'écrit sans clic pendant qu'un jeu tourne, pas plus un retrait qu'une pose.
    [Fact]
    public async Task Rien_ne_se_rend_sans_clic_pendant_qu_un_jeu_tourne()
    {
        var vm = ReleveDeFondSurLesDonneesReelles(helperLance: true);
        await JouerLAppFermee(vm, 1);
        _poses.Should().Be(1, "témoin : posé jeu fermé");
        vm.JeuOuvertPourLeTest = () => true;

        await JouerLAppFermee(vm, 8);

        _rendus.Should().Be(0, "un jeu tourne : le levier ne se retire pas sans clic");
        vm.MemoireEssai!.Lire()!.PoseMaintenant.Should().BeTrue("l'essai garde son état, la partie suivante le fera avancer");
    }
}
