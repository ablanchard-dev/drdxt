using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// -----------------------------------------------------------------------
// Cinq tests unitaires lisaient la vraie machine — 18,7 s CHACUN.
//
// Mesure sur les journaux d'execution de la solution (4 447 tests, 411 s cumulees) : les
// cinq tests les plus lents durent tous **exactement** ~18,7 s. Un plateau identique n'est
// pas un calcul, c'est une ATTENTE. Ils construisent un `RepairAuditService` en injectant
// le lecteur qu'ils visent... et en laissant les autres sur leur valeur reelle :
// `WindowsServiceFailureLog`, `WerLiveKernelReportLog`, et par defaut de constructeur
// `WindowsUnexpectedShutdownLog` et `WindowsMemoryDiagnosticLog`. Le depot le savait deja :
// le resume de `IUnexpectedShutdownLog` dit qu'une lecture non bornee « aurait mis seize
// secondes dans chaque scan ».
//
// Deux couts, et le second est le pire :
//   * 12 tests = 33 % des 411 s de la suite, dont ces cinq a eux seuls ~93 s ;
//   * ces tests dependent de ce que la machine a vecu. Sur un PC avec des incidents WER,
//     ils lisent autre chose que sur un PC neuf. Un test unitaire qui change de reponse
//     selon l'historique de la machine n'est plus un test unitaire.
//
// Ce que ce fichier apporte : des lecteurs de DECOR — des reponses vides, qui sont des
// reponses legitimes du domaine (« une liste vide est une reponse ; un echec de lecture est
// un Fail »). Chaque test garde le lecteur qu'il vise et ne paie plus les autres.
//
// Ce qu'on ne perd pas : les vrais lecteurs restent couverts par leurs tests dedies
// (`LiveKernelReportLogTests`, `ServiceFailureLogTests`), qui les exercent sur une racine
// injectee au lieu de la machine.
// -----------------------------------------------------------------------

/// <summary>Un journal de services qui n'a rien a dire : reponse vide, pas un echec.</summary>
internal sealed class ServicesMuets : IServiceFailureLog
{
    public Result<IReadOnlyList<ServiceFailure>> Depuis(DateTimeOffset depuis)
        => Result<IReadOnlyList<ServiceFailure>>.Ok(Array.Empty<ServiceFailure>());
}

/// <summary>Aucun incident noyau a signaler.</summary>
internal sealed class NoyauMuet : ILiveKernelReportLog
{
    public Result<IReadOnlyList<LiveKernelReport>> Lire()
        => Result<IReadOnlyList<LiveKernelReport>>.Ok(Array.Empty<LiveKernelReport>());
}

/// <summary>Aucun arret brutal a signaler.</summary>
internal sealed class ArretsMuets : IUnexpectedShutdownLog
{
    public Result<IReadOnlyList<DateTimeOffset>> Since(DateTimeOffset earliest)
        => Result<IReadOnlyList<DateTimeOffset>>.Ok(Array.Empty<DateTimeOffset>());
}

/// <summary>Aucun test memoire a signaler.</summary>
internal sealed class MemoireMuette : IMemoryDiagnosticLog
{
    public Result<IReadOnlyList<ResultatTestMemoire>> Lire()
        => Result<IReadOnlyList<ResultatTestMemoire>>.Ok(Array.Empty<ResultatTestMemoire>());
}

/// <summary>Aucun vidage, aucun echec de creation.</summary>
internal sealed class VidagesMuets : IDumpCreationLog
{
    public Result<EtatDesVidages> Lire(DateTimeOffset depuis)
        => Result<EtatDesVidages>.Ok(new EtatDesVidages(Array.Empty<DateTimeOffset>(), 0, false));
}

public sealed class LecteursDeDecorTests
{
    [Fact]
    public void Un_decor_rend_une_REPONSE_vide_et_jamais_un_echec()
    {
        // La difference decide de ce que le produit a le droit de dire : « rien a signaler »
        // n'est pas « je n'ai pas pu lire ». Un decor qui renverrait Fail ferait passer les
        // tests voisins pour des tests de panne.
        new ServicesMuets().Depuis(DateTimeOffset.UtcNow.AddDays(-7)).IsOk.Should().BeTrue();
        new NoyauMuet().Lire().IsOk.Should().BeTrue();
        new ArretsMuets().Since(DateTimeOffset.UtcNow.AddDays(-7)).IsOk.Should().BeTrue();
        new MemoireMuette().Lire().IsOk.Should().BeTrue();
        new VidagesMuets().Lire(DateTimeOffset.UtcNow.AddDays(-7)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Un_decor_ne_lit_rien_et_ne_coute_donc_rien()
    {
        // Le temoin de la raison d'etre de ce fichier : le decor doit etre instantane.
        // Seuil tres large a dessein — ce qu'on refuse, c'est la lecture machine a 18 s,
        // pas une milliseconde de plus ou de moins sur une machine chargee.
        var debut = DateTime.UtcNow;

        for (var i = 0; i < 50; i++)
        {
            new ServicesMuets().Depuis(DateTimeOffset.UtcNow.AddDays(-7));
            new ArretsMuets().Since(DateTimeOffset.UtcNow.AddDays(-7));
        }

        (DateTime.UtcNow - debut).Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Le_detail_d_un_arret_muet_n_invente_pas_d_ecran_bleu()
    {
        // `Detail` a une implementation par defaut qui derive des dates. Sur un decor vide
        // elle doit rendre une liste vide, pas fabriquer un incident.
        IUnexpectedShutdownLog muet = new ArretsMuets();
        var detail = muet.Detail(DateTimeOffset.UtcNow.AddDays(-7));

        detail.IsOk.Should().BeTrue();
        detail.Value.Should().BeEmpty();
    }

    [Fact]
    public void Aucun_test_de_ce_dossier_ne_branche_un_lecteur_REEL_en_decor()
    {
        // Ce que ce fichier a reellement obtenu, et qu'il fige ici : les tests d'audit ne
        // lisent plus le journal d'evenements ni le dossier WER de la machine. Ils ne
        // changent donc plus de reponse selon ce que ce PC a vecu.
        //
        // Ce qu'il n'a pas obtenu : la vitesse. Les cinq tests visaient ~18,7 s en
        // execution de toute la solution ; apres les decors, le seul projet Detection les
        // ramene a ~10,3 s, mais la solution complete les remonte a ~28,3 s -- toujours a
        // 0,2 s pres les uns des autres. Un plateau identique sous contention designe une
        // ressource systeme serialisee (WMI), lue par `RepairAuditService.Lire()` elle-meme
        // pour les familles CPU / STOCKAGE / MMCSS, qui ne s'injectent pas. La cause est
        // la, elle n'est pas fermee, et la mesure de vitesse ne sera concluante qu'a charge
        // egale et repetee.
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "DrDXT.sln")))
            dossier = dossier.Parent;

        var repair = Path.Combine(dossier!.FullName, "tests", "DrDxt.Detection.Tests", "Repair");
        // Ce fichier-ci porte les deux noms dans son propre crible : s'accuser soi-meme
        // serait une fausse accusation, et un crible qui accuse a tort finit desactive.
        var fichiers = Directory.EnumerateFiles(repair, "*.cs")
            .Where(f => Path.GetFileName(f) != "LecteursDeDecorTests.cs")
            .ToList();

        fichiers.Should().HaveCountGreaterThan(3, "temoin : le crible doit lire de vrais fichiers");

        var fautifs = fichiers
            .Where(f => File.ReadAllText(f).Contains("new WindowsServiceFailureLog()", StringComparison.Ordinal)
                     || File.ReadAllText(f).Contains("new WerLiveKernelReportLog()", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        fautifs.Should().BeEmpty(
            "un lecteur reel branche en decor rend le test dependant de l'historique de la " +
            "machine ; leurs tests dedies les exercent sur une racine injectee");
    }
}
