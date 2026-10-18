using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Application.Parties;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

// La recherche de la sous-tension stable des cœurs, sur la page du processeur. Une mesure de référence à la tension d'origine,
// puis chaque palier : posé par le helper (relatif à la tension que le BIOS avait mise à ce démarrage, refusé avec SGX, relu), test
// de charge de deux minutes, erreurs matérielles du processeur relues ; un palier qui faillit arrête tout. La tension d'origine est
// rendue à la fin (décalage relatif 0), et ce retour est vérifié. Arrêtée, fermée ou en panne, la recherche est notée interrompue
// (jamais lue comme un plantage) ; un vrai plantage redémarre le PC et se lit au démarrage suivant. Le verdict se reporte au BIOS :
// DrDXT ne garde rien posé.
public partial class ScanViewModel
{
    /// <summary>Coutures de test : la mémoire, la pose par le helper, les erreurs matérielles du processeur depuis un instant
    /// (null = non lues).</summary>
    internal MemoireSousTension? MemoireSousTension { get; set; }
    internal Func<int, Task<Result>>? PoserSousTension { get; set; }
    internal Func<DateTimeOffset, int?> ErreursWheaDepuis { get; set; } = depuis =>
        DrDxt.Detection.Repair.ErreursMaterielles.Lire(1)?.Count(e => e.Quand >= depuis && e.DuProcesseur && e.Gravite is 0 or 1 or 2);

    private CancellationTokenSource? _arretSousTension;

    /// <summary>Couture de test : le fabricant de la carte mère lu au dernier scan.</summary>
    internal Func<string?>? FabricantDeCarte { get; set; }

    /// <summary>Les marques dont les menus du BIOS sont sourcés (manuels ASUS, carte MSI de référence lue, support ASRock) ;
    /// une autre marque garde le texte général plutôt qu'un chemin inventé.</summary>
    internal static string? MarqueSourcee(string? fabricant) => fabricant switch
    {
        null => null,
        _ when fabricant.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || fabricant.StartsWith("MSI", StringComparison.OrdinalIgnoreCase) => "MSI",
        _ when fabricant.StartsWith("ASUS", StringComparison.OrdinalIgnoreCase) => "ASUS",
        _ when fabricant.StartsWith("ASRock", StringComparison.OrdinalIgnoreCase) => "ASRock",
        _ => null,
    };

    [ObservableProperty] private bool _rechercheSousTensionEnCours;
    [ObservableProperty] private string? _resultatSousTension;
    [ObservableProperty] private bool _sousTensionTrouvee;
    [ObservableProperty] private string _etapeSousTension = string.Empty;

    public bool RechercheSousTensionATermine => ResultatSousTension is not null && !RechercheSousTensionEnCours;
    partial void OnResultatSousTensionChanged(string? value) => OnPropertyChanged(nameof(RechercheSousTensionATermine));
    partial void OnRechercheSousTensionEnCoursChanged(bool value) => OnPropertyChanged(nameof(RechercheSousTensionATermine));

    public string LabelSousTensionTitre => _localize("SousTension.Titre");
    public string LabelSousTensionDescription => _localize("SousTension.Description");
    public string LabelSousTensionMaintenir => _localize("SousTension.Maintenir");
    public string LabelSousTensionEnCours => _localize("SousTension.EnCours");
    public string LabelSousTensionArreter => _localize("Refroidissement.Test.Arreter");
    public string LabelSousTensionRelancer => _localize("Refroidissement.Test.Relancer");
    public string LabelCapsuleSousTension => _localize("Agir.Capsule.SousTension");
    public string LabelSousTensionPrerequis => _localize("SousTension.Prerequis");
    public string LabelSousTensionGuide => _localize("SousTension.Prerequis.Guide");

    /// <summary>Intel Core de 12e à 14e génération (i3 à i9, 12xxx à 14xxx) : le BIOS y refuse la sous-tension tant que
    /// « UnderVolt Protection » est active. Les Core Ultra et les générations d'avant ne sont pas visés.</summary>
    internal static bool PrerequisUndervoltProtection(string? modele) =>
        modele is { Length: > 0 } && modele.Contains("Intel", StringComparison.OrdinalIgnoreCase)
        && System.Text.RegularExpressions.Regex.IsMatch(modele, @"\bi[3579]-1[234]\d{2,3}");

    /// <summary>Couture de test. La condition du helper pour poser la tension, lue par les mêmes fonctions que lui : PawnIO
    /// installé et un processeur Intel. Ailleurs, la recherche ne se propose pas.</summary>
    internal Func<bool> SousTensionPossibleSurLaMachine { get; set; } =
        () => DrDxt.GpuNative.PawnIo.PawnIoIntel.Installe() && DrDxt.GpuNative.PawnIo.PawnIoIntel.ProcesseurIntel();

    public bool SousTensionPossible => SousTensionPossibleSurLaMachine();

    /// <summary>La ligne de prérequis se montre avant la recherche, sur le processeur lu au dernier scan.</summary>
    public bool SousTensionPrerequisVisible => PrerequisUndervoltProtection(_lastScan?.Hw.Cpu.Model);
    public string LabelSousTensionEtat => _localize(SousTensionTrouvee ? "SousTension.Etat.Trouvee" : "SousTension.Etat.NonTrouvee");
    partial void OnSousTensionTrouveeChanged(bool value) => OnPropertyChanged(nameof(LabelSousTensionEtat));

    private Task<Result> Poser(int mv) => PoserSousTension?.Invoke(mv)
        ?? (_helper is { } h ? h.ExecuteAsync(new SetCpuVoltageOffsetOp(mv), CancellationToken.None)
            : Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, _localize("SousTension.Raison.SansHelper"))));

    [RelayCommand]
    private async Task LancerRechercheSousTension()
    {
        if (RechercheSousTensionEnCours || TestRefroidissementEnCours || MemoireSousTension is not { } memoire) return;
        if ((JeuOuvertPourLeTest ?? _gameLocator.JeuOuvert)())
        {
            SousTensionTrouvee = false;
            ResultatSousTension = _localize("Refroidissement.Test.JeuOuvert");
            return;
        }
        RechercheSousTensionEnCours = true;
        ResultatSousTension = null;
        _arretSousTension = new CancellationTokenSource();
        var jeton = _arretSousTension.Token;
        var pose = false;
        RechercheSousTension? r = null;
        try
        {
            // Un essai d'écriture, rendu aussitôt, avant toute charge : un BIOS qui refuse (Undervolt Protection) se sait en une
            // seconde, au lieu de deux minutes de charge pour rien. Noté avant d'écrire : un plantage à cet instant se rattache
            // au premier palier, et la tension d'origine se rend au retour (ReprendreRechercheSousTensionAsync).
            var debutEssai = DateTimeOffset.Now;
            memoire.Ecrire(RechercheSousTension.Nouvelle(debutEssai) with { PalierCommenceLe = debutEssai });
            var essai = await Poser(RechercheSousTension.Paliers[0]);
            if (!essai.IsOk)
            {
                if (essai.Error?.Code == ErrorCodes.TensionNonRendue) pose = true;
                // Refusé avant tout palier pour une autre raison que le BIOS : la sous-tension n'est pas disponible ici, et le
                // message du helper, en anglais, ne se colle pas dans la phrase.
                r = RechercheSousTension.Nouvelle(DateTimeOffset.Now).Apres(IssuePalier.Refuse(essai.Error?.Code switch
                {
                    ErrorCodes.TensionRefusee => ErrorCodes.TensionRefusee,
                    ErrorCodes.TensionNonRendue => RaisonLisible.Dire(essai, _localize),
                    _ => NonDisponible,
                }));
                return;
            }
            pose = true;
            if (!(await Poser(0)).IsOk)
            {
                r = RechercheSousTension.Nouvelle(DateTimeOffset.Now).Interrompre(_localize("SousTension.Raison.SansHelper"));
                return;
            }
            pose = false;
            // L'origine est rendue : la marque tombe, un arrêt pendant la mesure de référence ne se rattache à aucun palier.
            memoire.Ecrire(RechercheSousTension.Nouvelle(debutEssai));
            EtapeSousTension = "0 mV";
            var reference = await Mesurer(jeton);
            r = RechercheSousTension.Nouvelle(DateTimeOffset.Now) with { Reference = reference.Palier };
            // Déjà au seuil de sécurité à la tension d'origine : aucun palier ne peut se juger, et ce n'est pas une lecture ratée.
            if (reference.ArretSecurite)
                r = r.Interrompre(_localize("SousTension.Raison.TropChaudAuDepart"));
            else if (reference.Palier is null)
                r = r.Interrompre(_localize("SousTension.Raison.NonLu"));
            while (!r.Finie && r.PalierEnCours is { } mv)
            {
                if (jeton.IsCancellationRequested) { r = r.Interrompre(_localize("SousTension.Raison.Interrompue")); break; }
                EtapeSousTension = mv.ToString(CultureInfo.InvariantCulture) + " mV";
                var debut = DateTimeOffset.Now;
                memoire.Ecrire(r with { PalierCommenceLe = debut });
                var posee = await Poser(mv);
                if (!posee.IsOk)
                {
                    // Un plan laissé changé après un refus : le retour à l'origine se tente à la fin.
                    if (posee.Error?.Code == ErrorCodes.TensionNonRendue) pose = true;
                    r = r.Apres(IssuePalier.Refuse(posee.Error?.Code == ErrorCodes.TensionRefusee
                        ? ErrorCodes.TensionRefusee : RaisonLisible.Dire(posee, _localize)));
                    break;
                }
                pose = true;
                var mesure = await Mesurer(jeton);
                if (mesure.Interrompue) { r = r.Interrompre(_localize("SousTension.Raison.Interrompue")); break; }
                if (mesure.ArretSecurite) { r = r.Apres(IssuePalier.Instable(_localize("SousTension.Raison.ArretSecurite"))); break; }
                if (mesure.Palier is not { } tenu) { r = r.Interrompre(_localize("SousTension.Raison.NonLu")); break; }
                // Non lues n'est pas aucune : sans le journal des erreurs, la stabilité ne se juge pas.
                if (ErreursWheaDepuis(debut) is not { } erreurs) { r = r.Interrompre(_localize("SousTension.Raison.ErreursNonLues")); break; }
                r = erreurs > 0
                    ? r.Apres(IssuePalier.Instable(string.Format(CultureInfo.InvariantCulture, _localize("SousTension.Raison.Erreurs"), erreurs)))
                    : r.Apres(IssuePalier.Stable(tenu.MaxC, tenu.FreinePourcent));
            }
        }
        catch (Exception ex)
        {
            r = (r ?? RechercheSousTension.Nouvelle(DateTimeOffset.Now)).Interrompre(ex.Message);
        }
        finally
        {
            // La tension d'origine, toujours, et vérifiée (un retour raté se dit, avec le geste qui la rend : redémarrer).
            var rendue = !pose || (await RendreOrigineSure()).IsOk;
            if (r is not null)
            {
                // Non gardée, la recherche ne se reprendra pas après un plantage ; la tension d'origine, elle, est déjà rendue.
                try { memoire.Ecrire(r); }
                catch (Exception ex) { System.Diagnostics.Trace.TraceError("Undervolt search state not saved: {0}", ex); }
                Dire(r, rendue);
            }
            EtapeSousTension = string.Empty;
            RechercheSousTensionEnCours = false;
        }
    }

    private async Task<Result> RendreOrigineSure()
    {
        try { return await Poser(0); }
        catch (Exception ex) { return Result.Fail(ErrorCodes.HelperOpFailed, ex.Message); }
    }

    [RelayCommand]
    private void ArreterRechercheSousTension() => _arretSousTension?.Cancel();

    /// <summary>Le secours d'OnExit, qui bloque le fil d'interface où la recherche finirait son finally : la recherche est
    /// arrêtée, l'origine rendue directement, hors de ce fil, et la recherche notée interrompue tout de suite. Windows qui
    /// redémarre ne laisse pas le finally tourner : sans cette note, le palier se lirait « plantage » au démarrage suivant.</summary>
    internal async Task RendreOrigineSansLeFilAsync()
    {
        if (!RechercheSousTensionEnCours) return;
        _arretSousTension?.Cancel();
        await RendreOrigineSure().ConfigureAwait(false);
        try
        {
            if (MemoireSousTension is { } memoire && memoire.Lire() is { Finie: false } enCours)
                memoire.Ecrire(enCours.Interrompre(_localize("SousTension.Raison.Interrompue")));
        }
        catch (Exception) { /* la tension d'origine est rendue juste avant ; seule la mention « interrompue » manque */ }
    }

    /// <summary>À la fermeture de l'App : une recherche en cours est arrêtée, et la tension d'origine rendue avant de partir.</summary>
    internal async Task InterrompreRechercheSousTensionAsync()
    {
        if (!RechercheSousTensionEnCours) return;
        _arretSousTension?.Cancel();
        while (RechercheSousTensionEnCours) await Task.Delay(50);
    }

    private sealed record Mesure(PalierStable? Palier, bool ArretSecurite, bool Interrompue);

    /// <summary>Un test de charge. Coupé avant la fin (arrêt, durée non tenue) : interrompu, rien ne compte ; arrêt de sécurité à
    /// 99 °C : à part ; état non lu : pas de palier.</summary>
    private async Task<Mesure> Mesurer(CancellationToken jeton)
    {
        var test = FabriqueTestRefroidissement();
        var duree = DureeDuTestRefroidissement;
        var res = await Task.Run(() => test.LancerAsync(duree, jeton), CancellationToken.None);
        if (res.ArretSecurite) return new Mesure(null, true, false);
        if (jeton.IsCancellationRequested || (!res.LectureePerdue && res.Secondes < (int)duree.TotalSeconds)) return new Mesure(null, false, true);
        return new Mesure(res.Freinage is { } f && !res.LectureePerdue
            ? new PalierStable(0, f.ChaudMaxC, Math.Round(100 * f.Part(f.Chaleur))) : null, false, false);
    }

    /// <summary>Au démarrage de l'App : une recherche coupée par un plantage du PC se lit (palier fautif dit). Coupée sur CE démarrage
    /// (l'App tuée en plein palier, sans fermeture ni finally), le palier est encore posé : l'origine est rendue et vérifiée, et la
    /// recherche notée interrompue. Sans helper déjà en marche, aucune invite d'élévation sans geste : redémarrer la rend, c'est dit.</summary>
    internal async Task ReprendreRechercheSousTensionAsync()
    {
        // Au lancement, sans clic : aucune invite Windows (SansGesteDuJoueur).
        using var sansGeste = SansGesteDuJoueur.Ouvrir();
        if (MemoireSousTension?.Lire() is not { Finie: false } r) return;
        var demarrage = DemarrageDuPc();
        var apres = r.ApresRedemarrage(demarrage);
        if (apres != r)
        {
            MemoireSousTension.Ecrire(apres);
            Dire(apres, rendue: true);
            return;
        }
        if (r.PalierCommenceLe is not { } commence || commence < demarrage) return;
        var joignable = HelperJoignableSansUac is not { } j || await j();
        var rendue = joignable && (await RendreOrigineSure()).IsOk;
        var interrompue = r.Interrompre(_localize("SousTension.Raison.Interrompue"));
        MemoireSousTension.Ecrire(interrompue);
        Dire(interrompue, rendue);
    }

    private const string NonDisponible = "SousTension.NonDisponible";

    private void Dire(RechercheSousTension r, bool rendue)
    {
        var fr = CultureInfo.InvariantCulture;
        var raison = (r.Raison ?? "").Replace(RechercheSousTension.RaisonPlantage, _localize(RechercheSousTension.RaisonPlantage), StringComparison.Ordinal);
        // Le refus du BIOS se dit en mots, avec le chemin de la marque de la carte, qu'un palier ait tenu avant ou non.
        var refus = raison == ErrorCodes.TensionRefusee
            ? _localize(MarqueSourcee((FabricantDeCarte ?? (() => _lastScan?.Hw.Board.Manufacturer))()) is { } marque
                ? "SousTension.Refusee." + marque : "SousTension.Refusee")
            : raison == NonDisponible ? _localize(NonDisponible)
            : null;
        // Le retour se dit tel qu'il a eu lieu : « rendue » seulement s'il a réussi, jamais à côté d'un échec ; refusée dès
        // le premier palier, rien n'a été posé, rien n'est à rendre.
        var suite = !rendue ? _localize("SousTension.NonRendue")
            : refus is not null && r.Stables.Count == 0 ? ""
            : _localize("SousTension.Rendue");
        if (r.Verdict is { } mv && r.Stables.LastOrDefault(s => s.Mv == mv) is { } tenu)
        {
            SousTensionTrouvee = true;
            // Quelques minutes de charge ne prouvent pas une sous-tension en jeu : le texte dit ce qui a été mesuré, et le
            // conseil BIOS garde un palier de marge, le tenu juste au-dessus du plus profond (le seul tenu, faute d'autre).
            var marge = r.Stables.Count >= 2;
            var auBios = marge ? r.Stables[^2].Mv : mv;
            ResultatSousTension = string.Format(fr, _localize(marge ? "SousTension.Verdict" : "SousTension.VerdictUnPalier"), mv, tenu.MaxC, r.Reference?.MaxC.ToString(fr) ?? "?",
                tenu.FreinePourcent, r.Reference?.FreinePourcent.ToString("0", fr) ?? "?",
                DureeDuTestRefroidissement.TotalMinutes.ToString("0.##", fr), auBios) + " (" + (refus ?? raison) + ")" + suite;
            return;
        }
        SousTensionTrouvee = false;
        ResultatSousTension = (refus ?? string.Format(fr, _localize("SousTension.Aucune"), raison)) + suite;
    }
}
