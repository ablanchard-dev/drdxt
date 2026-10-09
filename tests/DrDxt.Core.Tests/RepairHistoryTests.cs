using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// Les deux facons de mentir avec un diff.
///
/// Un journal des constats est la seule chose qui puisse repondre a « est-ce que quelque chose a
/// change depuis hier ? ». Mal fait, il repond faux avec autorite, ce qui est pire que de ne pas
/// repondre — et ces deux erreurs sont exactement celles que cette journee a rendues concretes :
///
///   · le 08/09/2026, l'audit a gagne quatre familles (MMCSS, services, reseau, thermique) en une
///     journee. Un diff naif aurait annonce des dizaines de constats APPARUS, comme si la machine
///     s'etait degradee du jour au lendemain ;
///   · et un constat qui n'est plus produit ressemble a une reparation autant qu'a un lecteur qui
///     a echoue. Les deux sont indistinguables, et le produit ne doit pas choisir.
/// </summary>
public class RepairHistoryTests
{
    private static RepairSnapshot Photo(string[] familles, params RepairEtat[] etats) =>
        new(DateTimeOffset.Now, familles, etats);

    private static RepairEtat E(string domaine, string nom, string? valeur,
        RepairStatus s = RepairStatus.Observed) => new(domaine, nom, valeur, s);

    [Fact]
    public void UNE_FAMILLE_NOUVELLE_N_EST_PAS_UNE_DEGRADATION_DE_LA_MACHINE()
    {
        // Le cas reel du 08/09. Sans ce garde-fou, le produit annonce une catastrophe le jour
        // ou il apprend a lire quelque chose de nouveau — et perd toute credibilite au moment
        // exact ou il devient plus capable.
        var hier = Photo(["POWER"], E("POWER", "plan", "Equilibre"));
        var aujourdhui = Photo(["POWER", "MMCSS"],
            E("POWER", "plan", "Equilibre"),
            E("MMCSS", "SystemResponsiveness", "20"));

        var d = RepairHistory.Comparer(hier, aujourdhui).Single();

        d.Quoi.Should().Be(RepairChangement.FamilleNouvelle);
        d.Phrase.Should().Contain("le produit qui a appris");
    }

    [Fact]
    public void UN_CONSTAT_NEUF_DANS_UNE_FAMILLE_DEJA_EXAMINEE_EST_UN_VRAI_APPARU()
    {
        // Le controle POSITIF : la garde ci-dessus ne doit pas rendre le diff aveugle. Quand la
        // famille etait deja lue, un constat neuf parle bien de la MACHINE.
        var hier = Photo(["POWER"], E("POWER", "plan", "Equilibre"));
        var aujourdhui = Photo(["POWER"],
            E("POWER", "plan", "Equilibre"),
            E("POWER", "parking de coeurs", "19, 20"));

        RepairHistory.Comparer(hier, aujourdhui).Single()
            .Quoi.Should().Be(RepairChangement.Apparu);
    }

    [Fact]
    public void UN_CONSTAT_DISPARU_N_EST_JAMAIS_DECLARE_REPARE()
    {
        // La seconde facon de mentir. « Disparu » et « repare » sont indistinguables : le
        // lecteur a pu echouer. Le produit doit porter l'ambiguite, pas la trancher.
        var hier = Photo(["BCD"], E("BCD", "useplatformclock", "Yes", RepairStatus.NonDefault));
        var aujourdhui = Photo(["BCD"], E("BCD", "bootstatuspolicy", "Standard"));

        var d = RepairHistory.Comparer(hier, aujourdhui)
            .Single(x => x.Quoi == RepairChangement.Disparu);

        // La premiere version de ce test bannissait le mot « repare » — et elle a echoue, a
        // juste titre. La phrase doit le prononcer, sans quoi elle ne peut pas dire que c'est une
        // des deux lectures possibles. Ce qui compte n'est pas l'absence d'un mot, c'est la
        // Presence des deux explications : « cela peut vouloir dire repare » ET « cela peut tout
        // autant vouloir dire que la lecture a echoue ».
        d.Phrase.Should().Contain("réparé", "la reparation est une des deux lectures possibles");
        d.Phrase.Should().Contain("échoué", "l'echec de lecture est l'autre, et elle est indistinguable");
        d.Phrase.Should().Contain("peut tout autant", "aucune des deux n'est privilegiee");
    }

    [Fact]
    public void UNE_FAMILLE_ENTIERE_MUETTE_ACCUSE_LE_LECTEUR_ET_NON_LA_MACHINE()
    {
        // Quand tous les constats d'une famille disparaissent d'un coup, l'explication « le
        // lecteur a echoue » est bien plus probable que « tout a ete repare ». Un produit qui
        // annonce cinq reparations la ou WMI a simplement refuse de repondre est dangereux.
        var hier = Photo(["RESEAU"],
            E("RESEAU", "carte", "Realtek"),
            E("RESEAU", "RSS", "actif"));
        var aujourdhui = Photo([], []);

        var d = RepairHistory.Comparer(hier, aujourdhui);

        d.Should().HaveCount(2);
        d.Should().OnlyContain(x => x.Quoi == RepairChangement.Disparu);
        d.Should().OnlyContain(x => x.Phrase.Contains("le lecteur qui a échoué"));
    }

    [Fact]
    public void UN_CHANGEMENT_DE_STATUT_PASSE_AVANT_UN_CHANGEMENT_DE_VALEUR()
    {
        // Les deux ont bouge, et c'est le JUGEMENT qui compte : « Observed → Suspicious » dit
        // quelque chose qu'une valeur differente ne dit pas.
        var hier = Photo(["CPU"], E("CPU", "bridage", "aucun", RepairStatus.ExpectedDefault));
        var aujourdhui = Photo(["CPU"], E("CPU", "bridage", "masque 0x4", RepairStatus.Suspicious));

        var d = RepairHistory.Comparer(hier, aujourdhui).Single();

        d.Quoi.Should().Be(RepairChangement.StatutChange);
        d.Avant.Should().Be("ExpectedDefault");
        d.Apres.Should().Be("Suspicious");
    }

    [Fact]
    public void DEUX_PHOTOS_IDENTIQUES_NE_PRODUISENT_AUCUN_CHANGEMENT()
    {
        // Le controle NEGATIF, et il compte : un diff qui trouve toujours quelque chose est un
        // diff qu'on cesse de lire.
        var e = E("POWER", "plan", "Equilibre");

        RepairHistory.Comparer(Photo(["POWER"], e), Photo(["POWER"], e)).Should().BeEmpty();
    }

    [Fact]
    public void UNE_VALEUR_QUI_CHANGE_SANS_LE_STATUT_EST_SIGNALEE_A_PART()
    {
        var hier = Photo(["CPU"], E("CPU", "frequence effective", "2476 MHz"));
        var aujourdhui = Photo(["CPU"], E("CPU", "frequence effective", "3630 MHz"));

        var d = RepairHistory.Comparer(hier, aujourdhui).Single();

        d.Quoi.Should().Be(RepairChangement.ValeurChangee);
        d.Avant.Should().Be("2476 MHz");
        d.Apres.Should().Be("3630 MHz");
    }

    [Fact]
    public void DEUX_CONSTATS_DE_MEME_NOM_NE_FONT_PAS_LEVER_LA_COMPARAISON()
    {
        // Le defaut trouve en executant, le 09/09/2026. Le couple (domaine, nom) n'est pas
        // unique : l'audit rend deux « BIOS / interface constructeur », une par interface WMI
        // trouvee. `ToDictionary` levait « An item with the same key has already been added ».
        //
        // Le code compilait et ses sept tests passaient — aucun ne contenait de doublon. Une
        // bibliotheque qui leve sur une donnee legitime est un defaut, quelle que soit la donnee.
        var hier = Photo(["BIOS"],
            E("BIOS", "interface constructeur", "MSI_BiosSetting"),
            E("BIOS", "interface constructeur", "MSI_BootOption"));
        var aujourdhui = Photo(["BIOS"],
            E("BIOS", "interface constructeur", "MSI_BiosSetting"),
            E("BIOS", "interface constructeur", "MSI_BootOption"));

        RepairHistory.Comparer(hier, aujourdhui).Should().BeEmpty("rien n'a bouge");
    }

    [Fact]
    public void DANS_UN_GROUPE_DE_MEME_NOM_ON_NE_COMPARE_QUE_L_ENTREE_ET_LA_SORTIE()
    {
        // Ces constats n'ont pas d'identite individuelle : les APPARIER par ordre serait un
        // tirage, et un simple changement d'ordre ferait croire a deux modifications. On ne parle
        // donc que de ce qui entre et de ce qui sort.
        var hier = Photo(["BIOS"],
            E("BIOS", "interface", "A"),
            E("BIOS", "interface", "B"));
        var aujourdhui = Photo(["BIOS"],
            E("BIOS", "interface", "B"),
            E("BIOS", "interface", "C"));

        var d = RepairHistory.Comparer(hier, aujourdhui);

        d.Should().HaveCount(2);
        d.Should().ContainSingle(x => x.Quoi == RepairChangement.Apparu && x.Apres == "C");
        d.Should().ContainSingle(x => x.Quoi == RepairChangement.Disparu && x.Avant == "A");
    }

    [Fact]
    public void UN_GROUPE_DE_MEME_NOM_DANS_LE_MEME_ORDRE_INVERSE_NE_CHANGE_RIEN()
    {
        // Le controle qui prouve que l'ordre n'est pas une identite.
        var hier = Photo(["BIOS"], E("BIOS", "interface", "A"), E("BIOS", "interface", "B"));
        var aujourdhui = Photo(["BIOS"], E("BIOS", "interface", "B"), E("BIOS", "interface", "A"));

        RepairHistory.Comparer(hier, aujourdhui).Should().BeEmpty();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    // La troisieme facon de mentir : accuser la machine pour un changement de conditions.
    //
    // Vu en vrai le 09/09/2026. Le journal a annonce « RAM / mode memoire : ExpectedDefault →
    // Unknown », ce qui se lit comme « la machine a perdu son double canal ». Elle n'avait rien
    // perdu : le debit etait tombe a 18,7 GB/s parce que des compilations tournaient, donc la
    // borne qui prouve le multi-canal ne tenait plus. Constat juste, lecture fausse.
    // ══════════════════════════════════════════════════════════════════════════════════════

    // ══════════════════════════════════════════════════════════════════════════════════════
    // La quatrieme : accuser la machine pour un changement du produit.
    //
    // Vu en vrai le 16/09/2026. Entre deux photos, le lecteur d'arrets a appris a exclure les
    // appuis longs sur le bouton : « arrets brutaux 11 → 10 ». Le journal le lisait comme une
    // machine qui s'arrange. C'est le produit qui avait change sa facon de lire.
    // ══════════════════════════════════════════════════════════════════════════════════════

    private static RepairSnapshot PhotoDuLecteur(string? empreinte) =>
        new(DateTimeOffset.Now, ["STABILITE"], [E("STABILITE", "arrets brutaux", "11 sur 90 jours")], null, empreinte);

    [Fact]
    public void DEUX_PHOTOS_PRISES_PAR_DEUX_VERSIONS_DU_LECTEUR_SONT_SIGNALEES()
        => RepairHistory.AvertissementDeLecteur(PhotoDuLecteur("a1"), PhotoDuLecteur("b2"))
            .Should().NotBeNull().And.Subject.ToString()!.Should().Contain("mis à jour");

    [Fact]
    public void LE_MEME_LECTEUR_NE_DECLENCHE_RIEN()
        => RepairHistory.AvertissementDeLecteur(PhotoDuLecteur("a1"), PhotoDuLecteur("a1")).Should().BeNull();

    [Theory]
    [InlineData(null, "b2")]
    [InlineData("a1", null)]
    [InlineData(null, null)]
    public void UNE_EMPREINTE_ABSENTE_NE_DECLENCHE_RIEN(string? avant, string? apres)
        => RepairHistory.AvertissementDeLecteur(PhotoDuLecteur(avant), PhotoDuLecteur(apres)).Should().BeNull();

    // La cinquieme : accuser la machine pour une difference de droits (vu le 16/09/2026).
    // Photo d'avant prise en administrateur, photo d'apres sans : « toute la famille BCD a cesse de
    // repondre », « DPC/ISR Suspicious → Unknown ». La machine n'avait rien fait.

    private static RepairSnapshot PhotoDroits(bool? eleve) =>
        new(DateTimeOffset.Now, ["BCD"], [E("BCD", "bootstatuspolicy", "IgnoreAllFailures")], null, null, eleve);

    [Fact]
    public void DEUX_PHOTOS_PRISES_AVEC_DES_DROITS_DIFFERENTS_SONT_SIGNALEES()
        => RepairHistory.AvertissementDeDroits(PhotoDroits(true), PhotoDroits(false))
            .Should().NotBeNull().And.Subject.ToString()!.Should().Contain("administrateur");

    // 29/09/2026, photo de la section Santé : « DrDXT a été mis à jour entre les deux photos ». « Photo » est le
    // mot du code pour un relevé ; le joueur, lui, a lancé deux scans. Les trois avertissements qu'il lit le disent.
    [Fact]
    public void LES_AVERTISSEMENTS_PARLENT_DE_SCANS_PAS_DE_PHOTOS()
    {
        var lus = new[]
        {
            RepairHistory.AvertissementDeLecteur(PhotoDuLecteur("a1"), PhotoDuLecteur("b2")),
            RepairHistory.AvertissementDeDroits(PhotoDroits(true), PhotoDroits(false)),
            RepairHistory.AvertissementDeDroits(PhotoDroits(false), PhotoDroits(true)),
        };

        lus.Should().OnlyContain(t => t != null && t.Contains("scan") && !t.Contains("photo"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    [InlineData(true, null)]
    public void DES_DROITS_IDENTIQUES_OU_INCONNUS_NE_DECLENCHENT_RIEN(bool? avant, bool? apres)
        => RepairHistory.AvertissementDeDroits(PhotoDroits(avant), PhotoDroits(apres)).Should().BeNull();

    private static RepairSnapshot PhotoChargee(double? charge, params RepairEtat[] etats) =>
        new(DateTimeOffset.Now, ["RAM"], etats, new ConditionsDeMesure(charge));

    [Fact]
    public void DEUX_PHOTOS_PRISES_SOUS_DES_CHARGES_TRES_DIFFERENTES_SONT_SIGNALEES()
    {
        // Le cas reel : machine au repos, puis machine qui compile.
        var a = PhotoChargee(4, E("RAM", "mode memoire", "2 canaux PROUVES"));
        var b = PhotoChargee(86, E("RAM", "mode memoire", null, RepairStatus.Unknown));

        RepairHistory.AvertissementDeConditions(a, b)
            .Should().NotBeNull().And.Subject.ToString()!.Should().Contain("4 %").And.Contain("86 %");
    }

    [Fact]
    public void UN_ECART_DE_CHARGE_ORDINAIRE_NE_DECLENCHE_RIEN()
    {
        // Le controle NEGATIF : un avertissement qui s'affiche a chaque passe est un
        // avertissement qu'on cesse de lire — exactement le defaut corrige une heure plus tot
        // sur les valeurs mesurees.
        RepairHistory.AvertissementDeConditions(PhotoChargee(8), PhotoChargee(15))
            .Should().BeNull();
    }

    [Theory]
    [InlineData(null, 90.0)]
    [InlineData(5.0, null)]
    [InlineData(null, null)]
    public void UNE_CHARGE_NON_LUE_NE_DECLENCHE_RIEN(double? avant, double? apres)
    {
        // « On ne sait pas » n'est pas « c'etait different ». Comparer un nombre a une absence
        // fabriquerait un avertissement a partir d'un trou de mesure — la meme faute que de rendre
        // zero pour un compteur illisible.
        RepairHistory.AvertissementDeConditions(PhotoChargee(avant), PhotoChargee(apres))
            .Should().BeNull();
    }

    [Fact]
    public void UNE_PHOTO_SANS_CONDITIONS_RESTE_COMPARABLE()
    {
        // Les photos ecrites avant que le produit sache mesurer la charge n'ont pas de conditions.
        // Elles doivent rester lisibles : un journal qu'une mise a jour rend inutilisable ne sert
        // a rien.
        var ancienne = new RepairSnapshot(DateTimeOffset.Now, ["RAM"],
            [E("RAM", "mode memoire", "2 canaux PROUVES")]);
        var neuve = PhotoChargee(50, E("RAM", "mode memoire", "2 canaux PROUVES"));

        RepairHistory.AvertissementDeConditions(ancienne, neuve).Should().BeNull();
        RepairHistory.Comparer(ancienne, neuve).Should().BeEmpty();
    }

    private static RepairSnapshot PhotoBanc(double? dispersion) =>
        new(DateTimeOffset.Now, ["RAM"], [E("RAM", "mode memoire", "2 canaux")],
            new ConditionsDeMesure(10, dispersion));

    [Fact]
    public void UN_BANC_MEMOIRE_AGITE_SUFFIT_A_AVERTIR_MEME_A_CHARGE_IDENTIQUE()
    {
        // Le cas qui a revele que la charge processeur ne suffit pas. Deux passes a 19 % et
        // 15 % — « comparables » selon ce seul critere — ont fait basculer `RAM / mode memoire` de
        // Unknown a ExpectedDefault. Le banc memoire est limite par la BANDE PASSANTE : un autre
        // programme peut saturer la memoire sans faire monter la charge CPU. Sa dispersion, elle,
        // le voit.
        // 17/09/2026 : 12,6 % est une vraie photo agitee du journal ; 8,2 % (l'ancien exemple) est dans
        // la plage d'une machine calme (max mesure 8,7 %) et n'avertit plus.
        RepairHistory.AvertissementDeConditions(PhotoBanc(1.4), PhotoBanc(12.6))
            .Should().NotBeNull().And.Subject.ToString()!.Should().Contain("12,6").And.Contain("agité");
    }

    [Fact]
    public void LA_DISPERSION_ORDINAIRE_D_UNE_MACHINE_CALME_N_AVERTIT_PAS()
    {
        // 27 photos reelles : aucune passe calme n'a depasse 8,7 %. A 5 %, l'avertissement sortait sur
        // 15 photos sur 27 et ne voulait plus rien dire.
        RepairHistory.AvertissementDeConditions(PhotoBanc(3.9), PhotoBanc(8.7)).Should().BeNull();
    }

    [Fact]
    public void LA_PHRASE_RESTE_EN_FRANCAIS_SUR_UN_WINDOWS_ANGLAIS()
    {
        // 12/09/2026 : la CI (runner en-US) rendait « 8.2 % » dans une phrase francaise. Meme
        // defaut que KeepPolicy et PlafondGpu (`ad1e8b8`) : la culture du poste fuyait dans le texte.
        var avant = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            RepairHistory.AvertissementDeConditions(PhotoBanc(1.4), PhotoBanc(12.6))
                .Should().Contain("12,6 %").And.NotContain("12.6");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = avant; }
    }

    [Fact]
    public void UNE_SEULE_PASSE_AGITEE_SUFFIT_MEME_SI_L_AUTRE_ETAIT_PARFAITE()
    {
        // La dispersion n'est pas une DIFFERENCE entre photos, c'est la QUALITE de chacune. Une
        // passe agitee rend le diff douteux meme si l'autre etait impeccable — exiger que les deux
        // soient mauvaises raterait tous les cas reels.
        RepairHistory.AvertissementDeConditions(PhotoBanc(21.7), PhotoBanc(1.1))
            .Should().NotBeNull();
    }

    [Fact]
    public void DEUX_BANCS_CALMES_N_AVERTISSENT_PAS()
    {
        // Le controle NEGATIF : le banc rend habituellement 1 a 3 % sur cette machine.
        RepairHistory.AvertissementDeConditions(PhotoBanc(1.4), PhotoBanc(2.9)).Should().BeNull();
    }

    [Fact]
    public void UNE_DISPERSION_NON_LUE_N_AVERTIT_PAS()
    {
        // « Non lu » n'est pas « agite ». Les photos ecrites avant que le produit sache la mesurer
        // n'en ont pas, et elles doivent rester comparables.
        RepairHistory.AvertissementDeConditions(PhotoBanc(null), PhotoBanc(null)).Should().BeNull();
    }

    // ── 17/09/2026 (D21) : la photo de l'outil et celle de l'appli ne se comparent pas ──
    // Vu sur la machine de reference : l'appli se comparait à une photo prise par l'outil de
    // développement, qui lit aussi la RAM, le réseau et le BIOS. L'écran annonçait seize lignes
    // « toute cette famille a cessé de répondre » alors que rien n'avait bougé.

    private static RepairSnapshot PhotoDe(string? lecteur, int minutes, params string[] familles) =>
        new(new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero).AddMinutes(minutes), familles, [],
            Lecteur: lecteur);

    [Fact]
    public void La_photo_de_reference_est_la_derniere_du_meme_lecteur()
    {
        var appliAvant = PhotoDe("appli", 0, "POWER");
        var outil = PhotoDe("outil", 10, "POWER", "RAM", "RESEAU");
        var appliMaintenant = PhotoDe("appli", 20, "POWER");

        RepairHistory.PhotoComparable([appliAvant, outil], appliMaintenant).Should().Be(appliAvant);
    }

    [Fact]
    public void Une_ancienne_photo_sans_lecteur_qui_lit_des_familles_inconnues_de_l_appli_est_ecartee()
    {
        var ancienneDeLOutil = PhotoDe(null, 0, "POWER", "RAM", "RESEAU");
        var appli = PhotoDe("appli", 20, "POWER");

        RepairHistory.PhotoComparable([ancienneDeLOutil], appli).Should().BeNull(
            "ses familles RAM et RESEAU ne sont pas lues par l'appli : ce serait comparer deux lecteurs");
    }

    [Fact]
    public void Une_ancienne_photo_sans_lecteur_aux_memes_familles_reste_comparable()
    {
        var ancienne = PhotoDe(null, 0, "POWER", "BCD");
        var appli = PhotoDe("appli", 20, "POWER", "BCD", "STOCKAGE");

        RepairHistory.PhotoComparable([ancienne], appli).Should().Be(ancienne,
            "l'historique d'avant le champ ne doit pas être perdu quand il vient du même lecteur");
    }

    [Fact]
    public void Aucune_photo_ne_donne_aucune_reference()
    {
        RepairHistory.PhotoComparable([], PhotoDe("appli", 0, "POWER")).Should().BeNull();
    }

    [Fact]
    public void Une_ancienne_photo_admin_reste_comparable_quand_seuls_les_droits_ont_baisse()
    {
        var admin = new RepairSnapshot(new DateTimeOffset(2026, 9, 16, 20, 0, 0, TimeSpan.Zero), ["POWER", "BCD"], [], Eleve: true);
        var sansDroits = new RepairSnapshot(new DateTimeOffset(2026, 9, 16, 21, 0, 0, TimeSpan.Zero), ["POWER"], [], Eleve: false, Lecteur: "appli");

        RepairHistory.PhotoComparable([admin], sansDroits).Should().Be(admin);
    }
}
