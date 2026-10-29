using System.Globalization;
using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Detection.Cpu;

/// <summary>
/// Sur la machine de référence, 100 °C en partie (registres du processeur) et des 1 % low de 63 contre
/// 91 deux jours avant, avec un refroidisseur sain. La cause n'était pas le montage : la limite de puissance avait été
/// ouverte au BIOS (65 → 253 W) et le refroidisseur saturait sur les pics, alors que le jeu tirait 117 W en médiane.
/// Le docteur relie la chaleur relevée en partie et la limite de puissance lue, et propose le plafond qui garde le jeu tel
/// quel (la médiane, plus une marge) en coupant les pics. Lecture seule du relevé des parties.
/// La chaleur relevée est celle du cœur le plus chaud (sur cette machine, le cœur 0 était 15 °C plus bas), et ce qui
/// freinait était la limite de courant, pas la chaleur. Quand la partie porte son
/// freinage (<see cref="FreinageCpu"/>), le docteur juge sur le temps passé freiné et sur sa raison, plus sur le pic seul.
/// </summary>
public static class ChaleurEnPartie
{
    public const string Nom = "chaleur en partie";
    /// <summary>Les réglages BIOS du dernier scan, écrits par l'App à côté des parties.</summary>
    public const string FichierBios = "bios-lu.json";
    /// <summary>Le seuil d'Intel : 100 °C = freinage ; 95 laisse voir les pics que six captures par partie ratent.</summary>
    public const double SeuilC = 95;

    /// <summary>Le TjMax d'Intel : là, le processeur se freine, et c'est prouvé. Entre <see cref="SeuilC"/> et lui, c'est à
    /// regarder.</summary>
    public const double TjMaxIntelC = 100;
    public const int FenetreJours = 14;
    public const int PartiesMaximum = 5;

    /// <summary>Ce que le docteur lit d'une partie relevée : le reste du fichier est ignoré.</summary>
    public sealed record PartieLue(string Jeu, DateTimeOffset Fin, double? CelsiusMax, double? WattsMedian, double Low1Median,
        FreinageCpu? Freinage = null, IReadOnlyList<VentilateurEnPartie>? Ventilateurs = null);

    /// <summary>Sous 5 % du temps freiné par la chaleur, les pics ne coûtent presque pas de fréquence. **Non chiffré** :
    /// 5 % laisse passer quelques secondes par minute.</summary>
    public const double PartChaleurNegligeable = FreinageCpu.PartNegligeable;
    /// <summary>Un quart du temps à sa limite de courant : c'est elle qui bride (544 s sur 966 sur la machine de référence).
    /// **Non chiffré** : choisi sous ce cas réel, au-dessus des passages brefs.</summary>
    public const double PartCourantQuiBride = FreinageCpu.PartQuiBride;
    /// <summary>Écart moyen cœur le plus chaud / cœur 0 au-delà duquel « 100 °C » se dit d'un ou deux cœurs (15 °C relevés).</summary>
    public const double EcartCoeurFavoriC = 8;
    /// <summary>Une minute de secondes au moins pour juger le freinage.</summary>
    public const int SecondesMinimum = 60;
    /// <summary>Une seule partie ne départage pas un réglage du hasard d'une partie (1 % low 93, 43 puis 25 au même réglage).</summary>
    public const int PartiesPourJuger = 3;

    /// <summary>Les dernières parties (deux semaines, cinq au plus, la plus récente d'abord) ; vide sans fichier, sans
    /// droit ou sans forme connue : rien n'est inventé.</summary>
    public static IReadOnlyList<PartieLue> Lire(string chemin, DateTimeOffset? maintenant = null)
    {
        try
        {
            if (!File.Exists(chemin)) return [];
            var lues = JsonSerializer.Deserialize<List<PartieLue?>>(File.ReadAllText(chemin)) ?? [];
            var depuis = (maintenant ?? DateTimeOffset.UtcNow) - TimeSpan.FromDays(FenetreJours);
            return lues.OfType<PartieLue>().Where(p => p.Fin >= depuis).OrderByDescending(p => p.Fin).Take(PartiesMaximum).ToList();
        }
        catch (Exception) { return []; }
    }

    /// <summary>Les réglages BIOS lus au dernier scan, avec leur heure (le scan les lit par le helper et les
    /// écrit à côté des parties ; le docteur n'a pas de helper). Le scan réécrit le fichier à chaque lecture, vide quand il
    /// n'a rien lu (BIOS mis à jour hors de la matrice, autre carte) : un vieux relevé ne survit pas à un flash.</summary>
    public sealed record BiosLu(DateTimeOffset Le, IReadOnlyList<BiosSettingReading> Reglages);

    /// <summary>Le relevé, seulement s'il date de cette session de Windows (un réglage du BIOS ne change qu'au démarrage) ;
    /// sinon, sans fichier ou illisible, null : le conseil reste générique.</summary>
    public static BiosLu? LireBios(string chemin, DateTimeOffset demarrage)
    {
        try
        {
            return File.Exists(chemin) && JsonSerializer.Deserialize<BiosLu>(File.ReadAllText(chemin)) is { } lu && lu.Le >= demarrage
                ? lu : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Pur : quoi faire de la limite de courant, d'après ce que le BIOS dit d'elle. Réglée à la main : la remettre
    /// en Auto. Déjà en Auto : c'est la tension qui pousse le courant au plafond (Lite Load bas), ou la carte elle-même.</summary>
    public static string ConseilCourant(IReadOnlyList<BiosSettingReading>? bios)
    {
        var limite = bios?.FirstOrDefault(r => r.Setting == "CPU Current Limit");
        var liteLoad = bios?.FirstOrDefault(r => r.Setting == "CPU Lite Load");
        if (limite is not null && limite.Libelle != "Auto")
            return Docteur.T("CH.CourantMain", limite.Libelle);
        if (limite is not null)
            return Docteur.T("CH.CourantDejaAuto")
                   + (liteLoad is { } ll && ll.Libelle != "Auto" ? Docteur.T("CH.LiteLoadMain", ll.Libelle) : Docteur.T("CH.LiteLoadBas"))
                   + Docteur.T("CH.Guide537");
        // BIOS non lu (carte hors de la table), les noms MSI ne sont que des exemples.
        return Docteur.T("CH.CourantGenerique");
    }

    /// <summary>Pur : le plafond de puissance qui garde la partie telle quelle : la médiane plus 35 %, arrondie à la
    /// dizaine, jamais sous 125 W (le profil de base d'Intel pour les K).</summary>
    public static int Plafond(double wattsMedian) => Math.Max(125, (int)Math.Ceiling(wattsMedian * 1.35 / 10) * 10);

    /// <summary>Pur : la température la plus haute relevée sur ces parties, ou null si aucune ne l'a lue.</summary>
    public static double? MaxC(IReadOnlyList<PartieLue> parties)
        => parties.Select(p => p.CelsiusMax).OfType<double>().DefaultIfEmpty().Max() is var m and > 0 ? m : null;

    /// <summary>Pur : le plafond que demande la dernière partie freinée (les parties qui ne l'ont pas été, celles jouées
    /// sous le plafond justement, ne l'effacent pas : sinon il s'enlèverait dès qu'il marche, et la montée reviendrait),
    /// quand elle l'a été par la chaleur et que sa puissance médiane est lue ; sinon null (le courant d'abord).</summary>
    public static double? PlafondThermique(IReadOnlyList<PartieLue> parties)
        => parties.Where(p => p.Freinage is { Secondes: >= SecondesMinimum } f && RaisonDe(f) != RaisonDuFreinage.PasFreine)
               .OrderByDescending(p => p.Fin).FirstOrDefault() is { } derniere
           && RaisonDe(derniere.Freinage!) == RaisonDuFreinage.Chaleur && derniere.WattsMedian is { } w && w > 0
            ? Plafond(w) : null;

    /// <summary>Pur : la règle du cœur (<see cref="FreinageCpu.Raison"/>), la même que la carte des parties.</summary>
    public static RaisonDuFreinage RaisonDe(FreinageCpu f) => f.Raison();

    /// <summary>Pur : le constat. Rien sans température relevée. Avec les secondes, le verdict de la dernière partie (un
    /// réglage corrigé se voit dès la suivante), confirmé par les parties du même jeu qui disent pareil ; sans : le pic.</summary>
    /// <param name="bios">Le BIOS lu dans cette session de Windows (voir <see cref="LireBios"/>).</param>
    /// <param name="demarrage">Le démarrage de cette session : le BIOS lu ne vaut pour la partie jugée que si elle a été
    /// jouée depuis, sinon le réglage a pu changer entre les deux.</param>
    /// <param name="intel">Le seuil de 95 °C et « à 100 °C il se freine » sont ceux d'Intel. Hors
    /// Intel (un Ryzen 7000 vit à 95 °C par conception, un 5800X3D se freine vers 90 °C), sans secondes de freinage relevées,
    /// la température seule ne juge rien.</param>
    /// <param name="portable">Sur un portable, les limites de puissance et de courant sont celles du fabricant (batterie,
    /// chauffe) : elles se regardent, elles ne se corrigent pas.</param>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<PartieLue> parties, LimiteDePuissanceLue? limite,
        BiosLu? bios = null, DateTimeOffset? demarrage = null, bool intel = true, bool portable = false)
    {
        if (MaxC(parties) is not { } max) yield break;
        var fr = CultureInfo.InvariantCulture;
        var jugees = parties.Where(p => p.Freinage is { Secondes: >= SecondesMinimum }).OrderByDescending(p => p.Fin).ToList();
        if (jugees.Count > 0)
        {
            var derniere = jugees[0];
            var f = derniere.Freinage!;
            var raison = RaisonDe(f);
            var memes = jugees.Count(p => p.Jeu == derniere.Jeu && RaisonDe(p.Freinage!) == raison);
            var confirmer = memes < PartiesPourJuger
                ? Docteur.T("CH.AConfirmer", memes, PartiesPourJuger)
                : "";
            var tete = Docteur.T("CH.Tete", derniere.Jeu, derniere.Fin.ToLocalTime()) + Lecture(f, fr);
            switch (raison)
            {
                case RaisonDuFreinage.Courant:
                    // À regarder : rien ne dit que la limite n'est pas celle du profil d'Intel (Intel Default Settings), qui pose
                    // un nombre au BIOS comme un réglage à la main ; sur un portable, c'est celle du fabricant.
                    yield return new RepairFinding("CPU", Nom, Docteur.T("CH.Val.Courant", Pourcent(f.Part(f.LimiteCourant))),
                        Docteur.T("CH.Val.Sous", Pourcent(PartCourantQuiBride)), RepairStatus.NonDefault,
                        tete + Docteur.T("CH.Courant") + (portable ? Docteur.T("CH.LimitePortable") : "")
                        + (f.Part(f.Chaleur) >= PartChaleurNegligeable ? Docteur.T("CH.ChaleurAussi") : "")
                        + confirmer, RepairAction.Recommandation,
                        Remede: ConseilCourant(bios is not null && demarrage is { } d && derniere.Fin >= d ? bios.Reglages : null));
                    break;
                case RaisonDuFreinage.Prochot:
                    yield return new RepairFinding("CPU", Nom, Docteur.T("CH.Val.Prochot", Pourcent(f.Part(f.Prochot))),
                        Docteur.T("CH.Val.Sous", Pourcent(PartChaleurNegligeable)), RepairStatus.Suspicious,
                        tete + Docteur.T("CH.Prochot") + confirmer, RepairAction.Recommandation, Remede: Docteur.T("CH.Prochot.Remede"));
                    break;
                case RaisonDuFreinage.Puissance:
                    // À corriger seulement sur un PC fixe dont la PL1 lue est sous la puissance de base d'Intel : là, elle bride
                    // démontrablement. À la valeur d'Intel, au-dessus, non lue, ou sur un portable : à regarder, et dit.
                    var sousIntel = !portable && limite is { SansLimite: false, NominaleW: > 0 } li && li.Pl1W < li.NominaleW - 1;
                    var aIntel = limite is { SansLimite: false, NominaleW: > 0 } la && Math.Abs(la.Pl1W - la.NominaleW) <= 1;
                    yield return new RepairFinding("CPU", Nom, Docteur.T("CH.Val.Puissance", Pourcent(f.Part(f.LimiteW))),
                        Docteur.T("CH.Val.Sous", Pourcent(PartCourantQuiBride)), sousIntel ? RepairStatus.Suspicious : RepairStatus.NonDefault,
                        tete + Docteur.T("CH.Puissance")
                        + (limite is { SansLimite: false } lp ? Docteur.T("CH.Pl1Lue", lp.Pl1W.ToString("0", fr)) : "")
                        + (portable ? Docteur.T("CH.LimitePortable") : aIntel ? Docteur.T("CH.PuissanceIntel") : "")
                        + Docteur.T("CH.PuissanceSuite")
                        + confirmer, RepairAction.Recommandation);
                    break;
                case RaisonDuFreinage.PasFreine:
                    yield return new RepairFinding("CPU", Nom, Docteur.T("CH.Val.JusquA", f.ChaudMaxC.ToString("0", fr)), null, RepairStatus.Observed,
                        tete + Docteur.T("CH.PasFreine"));
                    break;
                default:
                    yield return new RepairFinding("CPU", Nom, Docteur.T("CH.Val.Chaleur", Pourcent(f.Part(f.Chaleur))),
                        Docteur.T("CH.Val.Sous", Pourcent(PartChaleurNegligeable)), RepairStatus.Suspicious,
                        tete + confirmer, RepairAction.Recommandation,
                        Remede: (Ventilateur(f, fr) + SuiteChaleur(derniere.WattsMedian, limite, fr)).Trim());
                    break;
            }
            yield break;
        }

        var n = parties.Count(p => p.CelsiusMax is not null);
        if (!intel)
        {
            yield return new RepairFinding("CPU", Nom, Docteur.T("CH.Val.JusquA", max.ToString("0", fr)), null, RepairStatus.Observed,
                Docteur.T("CH.NonIntel", n, max.ToString("0", fr)));
            yield break;
        }
        var chaude = parties.Where(p => p.CelsiusMax is { } c && c >= SeuilC).OrderByDescending(p => p.CelsiusMax).FirstOrDefault();
        if (chaude is null)
        {
            yield return new RepairFinding("CPU", Nom, Docteur.T("CH.Val.JusquA", max.ToString("0", fr)), null, RepairStatus.Observed,
                Docteur.T("CH.SousSeuil", n, SeuilC.ToString("0", fr)));
            yield break;
        }
        var avertir = n < PartiesPourJuger
            ? Docteur.T("CH.Avertir", PartiesPourJuger) : "";
        yield return new RepairFinding("CPU", Nom, Docteur.T("CH.Val.JusquA", max.ToString("0", fr)),
            Docteur.T("CH.Val.SousC", SeuilC.ToString("0", fr)), chaude.CelsiusMax >= TjMaxIntelC ? RepairStatus.Suspicious : RepairStatus.NonDefault,
            Docteur.T("CH.Chaude", chaude.CelsiusMax!.Value.ToString("0", fr), chaude.Jeu, chaude.Fin.ToLocalTime(), n)
            + avertir, RepairAction.Recommandation, Remede: SuiteChaleur(chaude.WattsMedian, limite, fr).Trim());
    }

    /// <summary>Sous ce cycle pendant les secondes freinées par la chaleur, le ventilateur du processeur ne pousse pas : sa courbe
    /// est trop douce. **Non chiffré** : un ventilateur qui laisse le processeur se freiner devrait être près de son maximum.</summary>
    public const double VentiloQuiPousse = 85;
    /// <summary>Dix secondes chaudes au moins avec le ventilateur lu pour le juger.</summary>
    public const int SecondesVentiloMinimum = 10;

    /// <summary>Pur, 12/10/2026 : le ventilateur du processeur pendant les secondes freinées par la chaleur, le geste le moins
    /// cher d'abord. Rien quand il n'a pas été lu (carte non lue, autre puce) : rien n'est deviné.</summary>
    private static string Ventilateur(FreinageCpu f, IFormatProvider fr)
    {
        if (f.SecondesVentiloChaud < SecondesVentiloMinimum || f.VentiloPwmChaud is not { } pwm || f.VentiloTrMinChaud is not { } tr)
            return "";
        var lu = Docteur.T("CH.VentiloLu", pwm.ToString("0", fr), tr.ToString("0", fr));
        if (tr < 200)
            return Docteur.T("CH.VentiloArrete", tr.ToString("0", fr));
        if (pwm < VentiloQuiPousse)
            return Docteur.T("CH.VentiloDoux", lu);
        return Docteur.T("CH.VentiloAuBout", lu);
    }

    /// <summary>Pur : quoi faire de la chaleur : le plafond qui garde le jeu tel quel quand la limite lue laisse passer
    /// plus, sinon le refroidissement.</summary>
    private static string SuiteChaleur(double? wattsMedian, LimiteDePuissanceLue? limite, IFormatProvider fr)
    {
        var refroidissement = Docteur.T("CH.Refroidissement", SeuilC.ToString("0", fr));
        if (wattsMedian is not { } wm || wm <= 0)
            return Refroidissement(limite, fr);
        var cap = Plafond(wm);
        var medianeLue = Docteur.T("CH.Mediane", wm.ToString("0", fr));
        // Limite non lue (AMD, Intel sans PawnIO) : ni « sans limite » affirmé, ni Optimiser promis
        // (le levier lit la limite pour la poser).
        if (limite is null)
            return medianeLue + Docteur.T("CH.LimiteNonLue", cap) + refroidissement;
        if (!limite.SansLimite && limite.Pl2W <= cap + 5)
            return Refroidissement(limite, fr);
        // Optimiser ne se promet que là où le levier pose (PC fixe Intel, registre ni « sans limite » ni PL1 au-dessus de PL2).
        var optimiser = !limite.SansLimite && limite.Pl1W <= limite.Pl2W
            ? Docteur.T("CH.Optimiser", cap)
            : "";
        return medianeLue
               + (limite.SansLimite
                   ? Docteur.T("CH.SansLimite")
                   : Docteur.T("CH.LimiteLaissePasser", limite.Pl1W.ToString("0", fr), limite.Pl2W.ToString("0", fr)))
               + optimiser
               + Docteur.T("CH.Pl1EgalPl2", cap)
               + refroidissement;
    }

    private static string Refroidissement(LimiteDePuissanceLue? limite, IFormatProvider fr)
        => (limite is { SansLimite: false } t
               ? Docteur.T("CH.LimiteTenue", t.Pl1W.ToString("0", fr), t.Pl2W.ToString("0", fr))
               : "")
           + Docteur.T("CH.RefroidissementPasSuit");

    /// <summary>Pur : ce que les secondes de la partie disent, en une ou deux phrases.</summary>
    public static string Lecture(FreinageCpu f, IFormatProvider fr)
    {
        var texte = Docteur.T("CH.Lecture", Pourcent(f.Part(f.Chaleur)), Pourcent(f.Part(f.LimiteW)), Pourcent(f.Part(f.LimiteCourant)));
        if (f.Coeur0MoyenC is { } c0 && f.ChaudMoyenC - c0 >= EcartCoeurFavoriC)
            texte += Docteur.T("CH.CoeurFavori", f.ChaudMoyenC.ToString("0", fr), c0.ToString("0", fr));
        return texte;
    }

    private static string Pourcent(double part) => Docteur.T("CH.Pourcent", Math.Round(part * 100).ToString("0", CultureInfo.InvariantCulture));
}
