using System.Diagnostics;
using DrDxt.Core;
using DrDxt.Detection.Repair;
using DrDxt.Detection.Wmi;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// -----------------------------------------------------------------------
// La question laissee ouverte au tour precedent, fermee.
//
// Hier : les cinq tests d'audit les plus lents ont ete ramenes de 18,7 s a 10,3 s en
// injectant des lecteurs de decor — et remontaient a 28,3 s en execution de toute la
// solution, **toujours a 0,2 s pres les uns des autres**. Un plateau identique qui
// s'aggrave sous contention designe une ressource systeme serialisee, et la conclusion
// ecrite hier etait : « lue par Lire() elle-meme, qui ne s'injecte pas ».
//
// La cause, trouvee en lisant plutot qu'en supposant : `RepairAuditService` porte
//
//     private readonly ManagementWmiQuery _wmi = new();
//
// C'est le seul lecteur du service sans couture. Tous les autres (services, noyau, arrets,
// test memoire, vidages) s'injectent. Celui-la est cable en dur — et c'est WMI, precisement
// la ressource que Windows serialise. `UsureDisques` l'interroge, `BiosIdentite` aussi.
//
// Ce que ce test exige : la meme couture que les autres. Aucun changement de comportement
// pour le produit (le defaut reste `ManagementWmiQuery`), et les tests cessent d'interroger
// WMI pour verifier une famille qui n'a rien a voir avec lui.
// -----------------------------------------------------------------------

/// <summary>Un WMI qui ne sait rien : reponse vide, jamais un echec.</summary>
internal sealed class WmiMuet : IWmiQuery
{
    public int Appels { get; private set; }

    public Result<IReadOnlyList<WmiRow>> Query(string wql) => QueryInNamespace("", wql);

    public Result<IReadOnlyList<WmiRow>> QueryInNamespace(string ns, string wql)
    {
        Appels++;
        return Result<IReadOnlyList<WmiRow>>.Ok(Array.Empty<WmiRow>());
    }
}

public sealed class LAuditNImposePasSaSourceWmiTests
{
    private static RepairAuditService AvecDecors(IWmiQuery wmi) =>
        new(new ServicesMuets(), new NoyauMuet(), new ArretsMuets(), new MemoireMuette(),
            new VidagesMuets(), wmi);

    [Fact]
    public void La_source_WMI_s_injecte_comme_les_autres_lecteurs()
    {
        var wmi = new WmiMuet();

        var constats = AvecDecors(wmi).Lire();

        constats.Should().NotBeEmpty("temoin : l'audit doit rendre de vrais constats");
        wmi.Appels.Should().BeGreaterThan(0,
            "c'est la source injectee qui doit etre interrogee, pas celle cablee dans le service");
    }

    /// <summary>Les lecteurs systeme encore CABLES dans `Lire()`, gelés pour qu'on sache ce
    /// qui reste. Chacun lit la machine et aucun ne s'injecte.</summary>
    private static readonly string[] EncoreCables =
        ["PowerSettingsReader", "CpuFrequencyReader", "MmcssReader",
         "VendorBiosInterfaceProbe", "NvmlHardwareProbe"];

    [Fact]
    public void CE_QUI_RESTE_A_CABLER_EST_NOMME_ET_NE_GROSSIT_PAS()
    {
        // Pas d'assertion de duree : mesure, l'audit tout injecte prend encore 5,1 s, et les cinq
        // tests lents passent de 10,3 s a 9,8 s avec la couture WMI. Elle corrige une incoherence
        // reelle (c'etait le seul lecteur sans couture) mais n'est pas la cause du plateau.
        //
        // Ce qui reste vrai et verifiable : cinq lecteurs systeme sont encore cables dans
        // `Lire()`. Les geler ici, c'est savoir ou chercher la prochaine fois, et empecher
        // la liste de grossir en silence.
        var fichier = Chemin("src", "DrDxt.Detection", "Repair", "RepairAuditService.cs");
        var texte = File.ReadAllText(fichier);

        texte.Should().Contain("_wmi = wmi ?? new ManagementWmiQuery()",
            "temoin : le crible lit bien le service, couture comprise");

        var cables = EncoreCables.Where(n => texte.Contains("new " + n + "(", StringComparison.Ordinal))
                                 .ToList();

        cables.Should().BeEquivalentTo(EncoreCables,
            "cette liste est un constat date, pas un objectif : si un lecteur en sort, c'est "
            + "qu'il a recu sa couture et la ligne doit etre mise a jour");

        System.Text.RegularExpressions.Regex
            .Matches(texte, @"new [A-Z][A-Za-z]*(Reader|Probe)\(")
            .Select(m => m.Value).Distinct()
            .Should().HaveCountLessThanOrEqualTo(EncoreCables.Length + 1,
                "un lecteur systeme cable de plus rallongerait l'attente sans que personne "
                + "ne le voie");
    }

    private static string Chemin(params string[] morceaux)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return Path.Combine(d!.FullName, Path.Combine(morceaux));
    }

    [Fact]
    public void Le_produit_garde_sa_source_reelle_par_defaut()
    {
        // Le contrepoids : une couture qui changerait le defaut ferait taire le produit
        // en croyant accelerer les tests.
        var parDefaut = new RepairAuditService(new ServicesMuets(), new NoyauMuet(),
            new ArretsMuets(), new MemoireMuette(), new VidagesMuets());

        parDefaut.Lire().Should().NotBeEmpty();
    }
}
