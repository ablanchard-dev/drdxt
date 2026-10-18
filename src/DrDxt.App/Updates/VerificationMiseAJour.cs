using System.Net.Http;
using DrDxt.Core;
using DrDxt.Infrastructure.Updates;

namespace DrDxt.App.Updates;

/// <summary>Ce que la verification de mise a jour a vraiment constate, et rien de plus.</summary>
public enum EtatMiseAJour
{
    /// <summary>Aucun serveur configure : rien n'a ete tente, aucune requete n'est partie.</summary>
    NonConfigure,
    /// <summary>Serveur joint, rien de plus recent a offrir.</summary>
    AJour,
    /// <summary>Une version de l'App signee et verifiee est annoncee ; son installeur ne se telecharge qu'au clic.</summary>
    NouvelleVersionVerifiee,
    /// <summary>Serveur injoignable (ou trop lent) : on reste sur la version installee.</summary>
    Injoignable,
    /// <summary>Un paquet a ete refuse : signature, empreinte ou retrogradation. A dire, jamais a taire.</summary>
    Refusee,
    /// <summary>Echec local (ecriture du staging) : on reste sur la version installee.</summary>
    Echec,
}

/// <summary>Ce que la verification a constate, et, pour une version App verifiee, de quoi la telecharger au clic
/// (rend le chemin de l'installeur verifie).</summary>
public sealed record ResultatMiseAJour(
    EtatMiseAJour Etat, string? Sha256 = null, string? Version = null,
    Func<CancellationToken, Task<Result<string>>>? Telecharger = null)
{
    public bool Installable => Etat == EtatMiseAJour.NouvelleVersionVerifiee
                               && Telecharger is not null && Sha256 is not null && Version is not null;
}

/// <summary>
/// La mise a jour branchee dans l'app : un produit qui ne se met pas a jour reste un prototype.
///
/// Au demarrage : montage de `UpdateComposition` et `AutoUpdater`, canal App, sous un
/// delai borne, et le pied de page dit ce qui a ete constate.
///
/// L'URL du serveur : <see cref="UrlParDefaut"/> ; la variable <see cref="VariableUrl"/> la coupe (« off »), et ne la
/// remplace qu'en compilation Debug.
/// Coupée : aucun reseau touche, « aucun serveur configure ».
///
/// Transport : HTTPS exige par le montage, contenu garanti par la signature Ed25519 du manifeste.
/// L'epinglage SPKI du serveur de mise a jour se posera avec son deploiement (ses empreintes
/// n'existent pas encore).
/// </summary>
public static class VerificationMiseAJour
{
    public const string VariableUrl = "DRDXT_UPDATE_URL";

    /// <summary>Sans adresse écrite, aucune copie installée n'aurait jamais pu recevoir de
    /// mise à jour, et l'installeur ne posait pas la variable. Les manifestes signés vivent sur le site GitHub Pages du dépôt
    /// public. Tant qu'il n'est pas publié, GitHub répond 404 : « joignable, rien à offrir », jamais une erreur.</summary>
    public const string UrlParDefaut = "https://ablanchard-dev.github.io/drdxt";

    /// <summary>La variable gagne quand elle existe ; « off » coupe toute vérification (aucune requête ne part).
    /// Pas « vide » : sous Windows, une variable posée vide n'existe pas pour le processus.</summary>
    public const string Coupee = "off";

    /// <summary>Une autre adresse n'est prise qu'en compilation Debug (un serveur d'essai). Livrée, l'App ne lit que la sienne :
    /// une variable posée par un autre programme ne détourne pas la vérification ; « off » coupe toujours.</summary>
#if DEBUG
    public const bool AdresseRemplacable = true;
#else
    public const bool AdresseRemplacable = false;
#endif

    public static string? UrlEffective(string? variable, bool adresseRemplacable = AdresseRemplacable)
        => string.IsNullOrWhiteSpace(variable) ? UrlParDefaut
        : string.Equals(variable.Trim(), Coupee, StringComparison.OrdinalIgnoreCase) ? null
        : adresseRemplacable ? variable : UrlParDefaut;

    /// <summary>Au-dela, « injoignable » : une verification ne doit jamais tenir le demarrage. Elle borne le montage et
    /// le manifeste ; le téléchargement, au clic, a sa propre borne.</summary>
    public static readonly TimeSpan DelaiParDefaut = TimeSpan.FromSeconds(15);

    /// <summary>Le téléchargement de la charge, au clic : 49 Mo en 15 min passent à 0,5 Mbit/s.</summary>
    public static readonly TimeSpan DelaiTelechargement = TimeSpan.FromMinutes(15);

    /// <param name="verifierDeTest">Pour les tests seulement. null = clef de production (embarquée ;
    /// la graine privée est hors dépôt ; sans URL, « aucun serveur configuré »).</param>
    public static Result<AutoUpdater> Monter(string? url, HttpClient client, string versionInstallee,
        IUpdateVerifier? verifierDeTest = null) =>
        UpdateComposition.TryCreate(
            url,
            client,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppContext.BaseDirectory,
            versionInstallee,
            verifierDeTest);

    /// <summary>La vérification du démarrage, et le client par lequel le bouton « Installer » téléchargera plus tard. Ce
    /// client n'est jamais libéré ici : libéré à la fin de la vérification, il faisait échouer le clic (ObjectDisposedException
    /// au téléchargement).</summary>
    /// <param name="transport">Pour les tests seulement. null = le transport de l'App : les identifiants du compte vont au
    /// seul proxy configuré (<see cref="DrDxt.App.Platform.ProxyDuCompte"/>) ; derrière un proxy d'entreprise manuel qui
    /// les demande, sans eux il répondait 407 et la mise à jour restait « injoignable » pour toujours.</param>
    public static async Task<(ResultatMiseAJour Etat, HttpClient Client)> VerifierAuDemarrageAsync(
        Func<HttpClient, Result<AutoUpdater>> monter, HttpMessageHandler? transport = null)
    {
        var client = new HttpClient(transport ?? DrDxt.App.Platform.ProxyDuCompte.Transport())
        {
            // Les bornes de VerifierAsync et du clic décident ; les 100 s par défaut coupaient un installeur sous 4 Mbit/s.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var etat = await VerifierAsync(() => monter(client), DelaiParDefaut, CancellationToken.None).ConfigureAwait(false);
        return (etat, client);
    }

    public static async Task<ResultatMiseAJour> VerifierAsync(
        Func<Result<AutoUpdater>> monter, TimeSpan delai, CancellationToken ct)
    {
        // La borne couvre aussi le montage (cles, crypto, dossiers). Il tournait avant elle :
        // la CI a mesure 3,3 puis 7,7 s pour une borne de 200 ms. Un montage trop lent est « injoignable »,
        // comme un serveur trop lent ; il finit en arriere-plan sans que personne l'attende.
        using var borne = CancellationTokenSource.CreateLinkedTokenSource(ct);
        borne.CancelAfter(delai);
        Result<AutoUpdater> monte;
        try { monte = await Task.Run(monter, CancellationToken.None).WaitAsync(borne.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(EtatMiseAJour.Injoignable); }
        catch (Exception) { return new(EtatMiseAJour.Echec); }
        if (!monte.IsOk || monte.Value is null)
            return new(monte.Error?.Code == UpdateErrorCodes.NotConfigured ? EtatMiseAJour.NonConfigure : EtatMiseAJour.Echec);

        try
        {
            // Le manifeste signé seul : l'installeur (des dizaines de Mo) ne part qu'au clic « Installer ».
            var updater = monte.Value;
            var annonce = await updater.AnnoncerAsync(borne.Token).ConfigureAwait(false);
            if (!annonce.IsOk) return new(ClasserErreur(annonce.Error?.Code));
            return annonce.Value is { } m
                ? new(EtatMiseAJour.NouvelleVersionVerifiee, m.Sha256, m.Version, jeton => updater.TelechargerAsync(m, jeton))
                : new(EtatMiseAJour.AJour);
        }
        catch (OperationCanceledException) { return new(EtatMiseAJour.Injoignable); }
        catch (Exception) { return new(EtatMiseAJour.Echec); }
    }

    /// <summary>Le resultat du canal, un etat. Un refus passe avant « injoignable » : un paquet rejete est une
    /// information de securite.</summary>
    public static EtatMiseAJour Classer(Result<UpdateOutcome> app)
    {
        if (app is { IsOk: true, Value: UpdateOutcome.AppStaged }
            || app.Error?.Code == UpdateErrorCodes.ReplacerUnavailable)
            return EtatMiseAJour.NouvelleVersionVerifiee;

        return app.IsOk ? EtatMiseAJour.AJour : ClasserErreur(app.Error?.Code);
    }

    internal static EtatMiseAJour ClasserErreur(string? code)
    {
        return code switch
        {
            UpdateErrorCodes.SignatureInvalid or UpdateErrorCodes.HashMismatch or UpdateErrorCodes.Downgrade => EtatMiseAJour.Refusee,
            UpdateErrorCodes.Offline => EtatMiseAJour.Injoignable,
            _ => EtatMiseAJour.Echec,
        };
    }

    public static string CleResx(EtatMiseAJour e) => e switch
    {
        EtatMiseAJour.NonConfigure => "UI.Footer.Updates.NotConfigured",
        EtatMiseAJour.AJour => "UI.Footer.Updates.UpToDate",
        EtatMiseAJour.NouvelleVersionVerifiee => "UI.Footer.Updates.NewVersion",
        EtatMiseAJour.Injoignable => "UI.Footer.Updates.Offline",
        EtatMiseAJour.Refusee => "UI.Footer.Updates.Rejected",
        _ => "UI.Footer.Updates.Failed",
    };
}
