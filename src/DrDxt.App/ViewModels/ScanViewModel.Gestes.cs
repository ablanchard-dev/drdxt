using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>Les gestes du guide que DrDXT fait lui-même, rattachés au constat qui les demande.</summary>
public sealed partial class ScanViewModel
{
    public string LabelGesteFait => _localize("Geste.Fait");
    public string LabelGesteAnnuler => _localize("Geste.Annuler");
    public string LabelGesteEchec => _localize("Geste.Echec");
    public string LabelGesteIncertain => _localize("Geste.Incertain");
    public string LabelGesteAMoitie => _localize("Geste.AMoitie");

    /// <summary>La recherche en ligne de Windows Update, hors du fil de l'interface ; une couture pour les tests.</summary>
    internal Func<Task<Result>> ChercherWindowsUpdate { get; set; }
        = () => Task.Run(() => DrDxt.Detection.Hardware.RechercheWindowsUpdate.Chercher());

    /// <summary>Les gestes d'un constat de l'audit, ou null. Sans helper, seuls les gestes sans droits sont proposés.</summary>
    private IReadOnlyList<GesteDuDocteur>? GestesDuConstat(RepairFindingViewModel r)
    {
        // Chercher les mises à jour ne demande aucun droit : pas d'op privilégiée, pas de helper, rien à défaire.
        if (r.DomaineCle == "WINDOWS UPDATE" && r.Cible == DrDxt.Detection.Hardware.MisesAJourEnAttente.CibleRecherche)
            return [new GesteDuDocteur(_localize("Geste.WindowsUpdate.Titre"), _localize("Geste.WindowsUpdate.Detail"), () => ChercherWindowsUpdate())];

        if (_helper is not { } helper) return null;
        Task<Result> Envoyer(PrivilegedOp op) => helper.ExecuteAsync(op, CancellationToken.None);

        return (r.DomaineCle, r.NomCle) switch
        {
            // Seulement quand le constat porte son remède : un test déjà passé n'en a pas, il n'y a rien à lancer.
            ("STABILITE", "test memoire Windows") => AvecLesFaits("memoire", r.Remede is null ? [] :
            [
                ("memoire|test", () => new GesteDuDocteur(_localize("Geste.TestMemoire.Titre"), _localize("Geste.TestMemoire.Detail"),
                    () => Envoyer(new ScheduleMemoryDiagnosticOp()), () => Envoyer(new CancelMemoryDiagnosticOp()))),
            ]),
            // Un geste par programme lancé au démarrage, relu à l'instant (le constat ne garde que des noms).
            ("DEMARRAGE", "programmes au démarrage") when LireDemarrage() is { IsOk: true, Value: { } entrees }
                => AvecLesFaits("demarrage", GestesDuDemarrage(entrees, Envoyer, _localize, LireCoupures())),
            // Le périphérique se relance par l'identifiant que Windows a lu, porté par le constat.
            ("PERIPHERIQUES", _) when r.Cible is { Length: > 0 } id => AvecLesFaits("peripherique",
            [
                ("peripherique|" + id, () => new GesteDuDocteur(_localize("Geste.Peripherique.Titre"), _localize("Geste.Peripherique.Detail"),
                    () => Envoyer(new RestartDeviceOp(id)))),
            ]),
            // Un geste par vieux paquet : retiré après un export, remis depuis cet export.
            ("GPU DRIVER", "anciens pilotes d'affichage gardés") => AvecLesFaits("pilote",
                (r.Cible ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => ("pilote|" + p, (Func<GesteDuDocteur>)(() => GestePilote(p))))),
            // Le jeu en DLSS : le dernier modèle, au profil pilote de ce seul jeu.
            ("LATENCE", _) when r.Cible is { } exe && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && NvApiDesJeux is { } nv && CheminDuJeu(exe) is { } chemin
                => AvecLesFaits("dlss|" + chemin, GesteDlss(nv, chemin, Envoyer, _localize,
                        (inverses, poses) => GarderLeGesteAsync(DrDxt.Application.Tweaks.LevierDeGeste.IdDlss(chemin), inverses, poses),
                        () => OublierAsync(DrDxt.Application.Tweaks.LevierDeGeste.IdDlss(chemin))) is { } geste
                    ? [("dlss|" + chemin + "|modele", () => geste)] : []),
            _ => null,
        };
    }

    /// <summary>Les gestes de la session, par clé. La carte se refait souvent (un geste, un nouveau scan) : refait de zéro,
    /// un geste perdait son « fait » et son Annuler.</summary>
    private readonly Dictionary<string, GesteDuDocteur> _gestes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Les gestes que la relecture propose, gardés d'une carte à l'autre, plus les gestes faits de la même famille
    /// qu'elle ne propose plus (un programme coupé, un pilote retiré, le DLSS déjà au dernier modèle) : leur Annuler reste.</summary>
    internal IReadOnlyList<GesteDuDocteur>? AvecLesFaits(string famille, IEnumerable<(string Cle, Func<GesteDuDocteur> Creer)> frais)
    {
        var proposes = frais.Select(f => (f.Cle, Geste: _gestes.TryGetValue(f.Cle, out var g) ? g : _gestes[f.Cle] = f.Creer())).ToList();
        var cles = proposes.Select(p => p.Cle).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tous = proposes.Select(p => p.Geste)
            .Concat(_gestes.Where(kv => kv.Key.StartsWith(famille + "|", StringComparison.OrdinalIgnoreCase) && !cles.Contains(kv.Key) && kv.Value.Fait)
                .Select(kv => kv.Value))
            .ToList();
        return tous.Count > 0 ? tous : null;
    }

    private readonly Dictionary<string, string?> _cheminsDesJeux = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Le chemin d'un jeu, ou null tant qu'il n'est pas connu. Chercher dans les bibliothèques des lanceurs prend des
    /// secondes : la recherche part hors du fil de l'interface, une fois par jeu et par session, et la carte se refait quand
    /// elle aboutit.</summary>
    private string? CheminDuJeu(string exe)
    {
        if (_cheminsDesJeux.TryGetValue(exe, out var chemin)) return chemin;
        _cheminsDesJeux[exe] = null;
        var ui = SynchronizationContext.Current;
        _ = Task.Run(() => TrouverJeu(exe)).ContinueWith(t =>
        {
            void Poser()
            {
                _cheminsDesJeux[exe] = t.IsCompletedSuccessfully ? t.Result : null;
                if (_cheminsDesJeux[exe] is not null) ReconstruireCarteSurLeDomaineOuvert();
            }
            if (ui is null) Poser(); else ui.Post(_ => Poser(), null);
        }, TaskScheduler.Default);
        return null;
    }

    /// <summary>Le dernier modèle DLSS au profil pilote de ce jeu, ou null (profil absent ou illisible, provenance
    /// inconnue, déjà au dernier modèle). Les deux réglages partent dans l'ordre ; si le second est refusé, le premier est
    /// défait. Ce qui reste posé (un premier réglage qui ne se défait pas, une annulation refusée en partie) rend
    /// <see cref="ErrorCodes.TweakPartiallyApplied"/> : le geste se dit à moitié, et Annuler reste.</summary>
    /// <param name="garder">Le geste posé (même à moitié) est gardé comme un levier : ses inverses, dans l'ordre où ils se
    /// rejouent, et ce qui a été posé.</param>
    /// <param name="oublier">Le geste rendu en entier sort du journal des leviers et du manifeste.</param>
    internal static GesteDuDocteur? GesteDlss(DrDxt.Application.Tweaks.Hardware.INvApi nv, string chemin,
        Func<PrivilegedOp, Task<Result>> envoyer, Func<string, string> localiser,
        Func<IReadOnlyList<PrivilegedOp>, IReadOnlyList<PrivilegedOp>, Task>? garder = null, Func<Task>? oublier = null)
    {
        if (DrDxt.Application.Tweaks.Gpu.SurcoucheDlss.Planifier(nv, chemin) is not { IsOk: true } plan) return null;
        var (faire, annuler) = plan.Value;
        return new GesteDuDocteur(localiser("Geste.Dlss.Titre"), localiser("Geste.Dlss.Detail"), Poser, Rendre);

        async Task<Result> Poser()
        {
            for (var i = 0; i < faire.Count; i++)
                if (await envoyer(faire[i]).ConfigureAwait(true) is { IsOk: false } refus)
                {
                    var defait = true;
                    for (var j = i - 1; j >= 0; j--)
                        if (!(await envoyer(annuler[j]).ConfigureAwait(true)).IsOk) defait = false;
                    if (defait) return refus;
                    if (garder is not null) await garder(annuler.Take(i).Reverse().ToList(), faire.Take(i).ToList()).ConfigureAwait(true);
                    return Result.Fail(ErrorCodes.TweakPartiallyApplied, "The first setting could not be undone.");
                }
            if (garder is not null) await garder(annuler.AsEnumerable().Reverse().ToList(), faire).ConfigureAwait(true);
            return Result.Ok();
        }

        // Chaque réglage est tenté : un refus n'empêche pas de rendre les autres.
        async Task<Result> Rendre()
        {
            var (rendus, refus) = (0, default(Result));
            for (var j = annuler.Count - 1; j >= 0; j--)
                if (await envoyer(annuler[j]).ConfigureAwait(true) is { IsOk: false } r) refus ??= r;
                else rendus++;
            if (refus is null && oublier is not null) await oublier().ConfigureAwait(true);
            return refus is null ? Result.Ok()
                : rendus > 0 ? Result.Fail(ErrorCodes.TweakPartiallyApplied, "Part of the gesture was not undone.")
                : refus;
        }
    }

    /// <summary>Retirer un vieux paquet de pilote d'affichage (après un export que le helper garde), et le remettre. Retiré, il est
    /// gardé comme un levier (<see cref="DrDxt.Application.Tweaks.LevierDeGeste"/>) : « Ce que DrDXT a posé » le remet après une
    /// relance, quand ce geste n'a plus de carte.</summary>
    internal GesteDuDocteur GestePilote(string paquet)
    {
        var id = DrDxt.Application.Tweaks.LevierDeGeste.IdPilote(paquet);
        Task<Result> Envoyer(PrivilegedOp op) => _helper!.ExecuteAsync(op, CancellationToken.None);
        return new GesteDuDocteur(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, _localize("Geste.Pilote.Titre"), paquet),
            string.Format(System.Globalization.CultureInfo.CurrentCulture, _localize("Geste.Pilote.Detail"), paquet),
            async () =>
            {
                var r = await Envoyer(new RemoveDriverPackageOp(paquet)).ConfigureAwait(true);
                if (r.IsOk) await GarderLeGesteAsync(id, [new RestoreDriverPackageOp(paquet)], [new RemoveDriverPackageOp(paquet)]).ConfigureAwait(true);
                return r;
            },
            async () =>
            {
                var r = await Envoyer(new RestoreDriverPackageOp(paquet)).ConfigureAwait(true);
                if (r.IsOk) await OublierAsync(id).ConfigureAwait(true);
                return r;
            });
    }

    /// <summary>Un geste posé entre au journal des leviers (son Annuler) et, si son origine se rend à la désinstallation, au
    /// manifeste signé.</summary>
    private async Task GarderLeGesteAsync(string id, IReadOnlyList<PrivilegedOp> inverses, IReadOnlyList<PrivilegedOp> poses)
    {
        var rapport = new ApplyReport([new ApplyOutcome(id, true, false, DrDxt.Application.Tweaks.LevierDeGeste.Snapshot(id, inverses, poses), null)], null);
        _journalLeviers?.Enregistrer(rapport);
        if (_manifestMapper.ToRevertOnly(rapport) is { Count: > 0 } gardes)
            await _desiredStateStore.CommitAsync(new DesiredState(Array.Empty<AppliedTweak>(), gardes)).ConfigureAwait(true);
        ChargerLeviersPoses();
    }

    /// <summary>L'export d'un paquet de pilote retiré est-il encore sous la racine de confiance du helper ? Lu, jamais écrit ;
    /// illisible, le helper en décide. Une couture pour les tests.</summary>
    internal Func<string, bool> ExportDuPiloteExiste { get; set; } = paquet =>
    {
        try
        {
            var racine = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DrDxt", "pilotes-retires");
            return !System.IO.Directory.Exists(racine)
                || System.IO.Directory.GetDirectories(racine, System.IO.Path.GetFileNameWithoutExtension(paquet) + "-*").Length > 0;
        }
        catch (Exception) { return true; }
    };

    /// <summary>Le pilote NVIDIA lu au profil d'un jeu ; null = pas de carte NVIDIA, aucun geste DLSS.</summary>
    internal DrDxt.Application.Tweaks.Hardware.INvApi? NvApiDesJeux { get; set; }

    /// <summary>Le chemin complet d'un exécutable de jeu par son nom ; une couture pour les tests.</summary>
    internal Func<string, string?> TrouverJeu { get; set; } = DrDxt.Detection.Hardware.DossiersDeJeux.CheminDeLExecutable;

    /// <summary>La lecture des programmes au démarrage ; une couture pour les tests.</summary>
    internal Func<Result<IReadOnlyList<DrDxt.Detection.Repair.ProgrammesAuDemarrage.Entree>>> LireDemarrage { get; set; }
        = DrDxt.Detection.Repair.ProgrammesAuDemarrage.Lire;

    /// <summary>Les programmes que DrDXT a coupés au démarrage, notés avant l'écriture (TraceDesCoupures) ; aucun dans les
    /// tests qui ne les portent pas.</summary>
    internal Func<IReadOnlyList<DrDxt.ElevatedHelper.Execution.CoupureDuDemarrage>> LireCoupures { get; set; } = () => [];

    /// <summary>Couper chaque programme actif comme le Gestionnaire des tâches ; Annuler le réactive. La sécurité de Windows,
    /// un antivirus ou un contrôle parental n'ont pas de geste (le helper refuserait de les couper).
    /// Un programme que DrDXT a coupé dans une session précédente revient, déjà fait, avec son Annuler : seulement si la
    /// valeur de la machine est encore exactement celle que DrDXT a écrite. Changée ailleurs, la coupure n'est plus la sienne.</summary>
    internal static IReadOnlyList<(string Cle, Func<GesteDuDocteur> Creer)> GestesDuDemarrage(
        IReadOnlyList<DrDxt.Detection.Repair.ProgrammesAuDemarrage.Entree> entrees, Func<PrivilegedOp, Task<Result>> envoyer,
        Func<string, string> localiser, IReadOnlyList<DrDxt.ElevatedHelper.Execution.CoupureDuDemarrage>? coupures = null)
        => entrees
            .Where(e => !ProtegesAuDemarrage.Est(e.Nom) && !ProtegesAuDemarrage.Est(e.Cle))
            .Where(e => e.Actif || CoupeParDrDxt(e, coupures))
            .Select(e => ($"demarrage|{e.Ruche}|{e.Liste}|{e.Cle ?? e.Nom}", (Func<GesteDuDocteur>)(() => new GesteDuDocteur(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, localiser("Geste.Demarrage.Titre"), e.Nom),
                string.IsNullOrWhiteSpace(e.Commande) ? localiser("Geste.Demarrage.Detail") : e.Commande,
                () => envoyer(e.Geste(false)), () => envoyer(e.Geste(true)), dejaFait: !e.Actif))))
            .ToList();

    private static bool CoupeParDrDxt(DrDxt.Detection.Repair.ProgrammesAuDemarrage.Entree e,
        IReadOnlyList<DrDxt.ElevatedHelper.Execution.CoupureDuDemarrage>? coupures)
        => e.Octets is { } octets && coupures?.Any(c => c.Ruche == e.Ruche
            && string.Equals(c.Liste, e.Liste, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Nom, e.Cle ?? e.Nom, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Octets, octets, StringComparison.OrdinalIgnoreCase)) == true;
}
