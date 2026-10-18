using CommunityToolkit.Mvvm.Input;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

// Le pied de page : la version, ce que la vérification de mise à jour a constaté, et le bouton « Installer ».
public sealed partial class ScanViewModel
{
    // Pied de page.
    public string LabelFooterVersion => string.Format(System.Globalization.CultureInfo.InvariantCulture, _localize("UI.Footer.Version"),
        typeof(ScanViewModel).Assembly.GetName().Version?.ToString(3) ?? "?");
    // Ce que la verification de mise a jour a constate au demarrage. Tant qu'elle n'a pas
    // rendu : « jamais verifiees », jamais une coche.
    private string? _etatMisesAJour;
    public string LabelFooterUpdates => (_etatMisesAJour ?? _localize("UI.Footer.Updates")) + (_etatDonnees is { } d ? " · " + d : "");

    private string? _etatDonnees;

    /// <summary>Les tables en service (radar des régressions), quand elles viennent d'un paquet signé, ou un paquet
    /// refusé : à dire, comme une mise à jour refusée. Les tables livrées avec l'App ne s'annoncent pas.</summary>
    public void AfficherDonnees(DrDxt.Core.TablesDeDonnees tables, bool refuse)
    {
        _etatDonnees = refuse ? _localize("UI.Footer.Data.Rejected")
            : tables.Version > 0 ? string.Format(System.Globalization.CultureInfo.CurrentUICulture, _localize("UI.Footer.Data"),
                tables.Version, tables.Publie?.ToString("d", System.Globalization.CultureInfo.CurrentUICulture) ?? "?")
            : null;
        OnPropertyChanged(nameof(LabelFooterUpdates));
    }

    private DrDxt.App.Updates.ResultatMiseAJour? _miseAJour;

    public string LabelInstallUpdate => _localize("UI.Footer.Install");

    private bool _telechargementMiseAJour;

    /// <summary>Le bouton n'existe que pour une version App verifiee dont on connait empreinte et version, et se tait
    /// pendant son téléchargement. Depuis une App élevée, il se tait aussi : le setup hériterait du jeton administrateur, et
    /// le téléchargement écrirait en administrateur dans le dossier du compte.</summary>
    public bool CanInstallUpdate => _miseAJour?.Installable == true && !_telechargementMiseAJour && _estEleve != true;

    private string VersionNommee(string v)
        => string.Format(System.Globalization.CultureInfo.CurrentUICulture, _localize("UI.Footer.Updates.NewVersionNamed"), v)
           + (_estEleve == true ? " · " + Docteur.T("MAJ.InstalleurEleve") : "");

    /// <summary>La version signée et vérifiée qui attend le clic ; null sinon.</summary>
    public string? VersionProposee => _miseAJour is { Installable: true } m ? m.Version : null;

    public void AfficherMiseAJour(DrDxt.App.Updates.ResultatMiseAJour resultat)
    {
        _miseAJour = resultat;
        // Une version vérifiée est nommée : « une nouvelle version » ne dirait pas laquelle.
        _etatMisesAJour = VersionProposee is { } v
            ? VersionNommee(v)
            : _localize(DrDxt.App.Updates.VerificationMiseAJour.CleResx(resultat.Etat));
        OnPropertyChanged(nameof(LabelFooterUpdates));
        OnPropertyChanged(nameof(VersionProposee));
        OnPropertyChanged(nameof(CanInstallUpdate));
        InstallUpdateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Télécharge l'installeur annoncé (signature et empreinte vérifiées), puis le lance (empreinte revérifiée
    /// sur le fichier même). L'UAC et la fermeture de l'App appartiennent a l'installeur.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdate()
    {
        if (_miseAJour is not { Installable: true } m || _telechargementMiseAJour) return;
        if (_estEleve == true)
        {
            _etatMisesAJour = VersionNommee(m.Version!);
            SignalerMiseAJour();
            return;
        }
        _telechargementMiseAJour = true;
        _etatMisesAJour = _localize("UI.Footer.Updates.Downloading");
        SignalerMiseAJour();
        try
        {
            Result<string> dl;
            using (var borne = new CancellationTokenSource(DrDxt.App.Updates.VerificationMiseAJour.DelaiTelechargement))
            {
                try { dl = await Task.Run(() => m.Telecharger!(borne.Token)); }
                catch (OperationCanceledException) { dl = Result<string>.Fail(DrDxt.Infrastructure.Updates.UpdateErrorCodes.Offline, "délai"); }
            }
            if (!dl.IsOk)
            {
                var etat = DrDxt.App.Updates.VerificationMiseAJour.ClasserErreur(dl.Error?.Code);
                _etatMisesAJour = _localize(DrDxt.App.Updates.VerificationMiseAJour.CleResx(etat));
                // Un paquet refusé n'est plus proposé ; un réseau coupé laisse réessayer.
                if (etat == DrDxt.App.Updates.EtatMiseAJour.Refusee) _miseAJour = m with { Etat = etat };
                return;
            }
            // Le jeton connu de l'App décide ; inconnu, Lancer lit celui du processus. La production passe toujours un jeton
            // connu : inconnu, le téléchargement aurait déjà eu lieu avant ce refus, qui n'est qu'une défense en profondeur.
            var lance = await Task.Run(() => DrDxt.App.Updates.InstallationMiseAJour.Lancer(dl.Value!, m.Sha256!, m.Version!, _lancerInstalleur,
                _estEleve is { } e ? () => e : null));
            if (lance.Error?.Code == DrDxt.App.Updates.InstallationMiseAJour.CodeEleve)
            {
                // Jeton inconnu de l'App, élevé au lancement. Rien n'est retiré : relancer l'App normalement suffit.
                _etatMisesAJour = VersionNommee(m.Version!) + " · " + RaisonLisible.Dire(lance, _localize);
                return;
            }
            _etatMisesAJour = _localize(lance.IsOk ? "UI.Footer.Updates.InstallerLaunched" : "UI.Footer.Updates.InstallerRefused");
            // Un installeur refusé (fichier changé depuis la vérification) n'est plus proposé.
            if (!lance.IsOk) _miseAJour = m with { Etat = DrDxt.App.Updates.EtatMiseAJour.Refusee };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Une panne imprévue (client libéré, disque plein) remontait jusqu'au fil de l'écran. Elle se dit au pied de
            // page, et la version reste proposée : un nouveau clic réessaie.
            System.Diagnostics.Trace.TraceError("Update install failed: {0}", ex);
            _etatMisesAJour = $"{_localize(DrDxt.App.Updates.VerificationMiseAJour.CleResx(DrDxt.App.Updates.EtatMiseAJour.Echec))}{Docteur.DeuxPoints}{ex.Message}";
        }
        finally
        {
            _telechargementMiseAJour = false;
            SignalerMiseAJour();
        }
    }

    private void SignalerMiseAJour()
    {
        OnPropertyChanged(nameof(LabelFooterUpdates));
        OnPropertyChanged(nameof(VersionProposee));
        OnPropertyChanged(nameof(CanInstallUpdate));
        InstallUpdateCommand.NotifyCanExecuteChanged();
    }
}
