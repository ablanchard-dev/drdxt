using System.Net.Http;
using DrDxt.Core;
using DrDxt.Infrastructure.Updates;

namespace DrDxt.App.Updates;

/// <summary>Ce que la verification de mise a jour a vraiment constate — et rien de plus.</summary>
public enum EtatMiseAJour
{
    /// <summary>Aucun serveur configure : rien n'a ete tente, aucune requete n'est partie.</summary>
    NonConfigure,
    /// <summary>Serveur joint, rien de plus recent a offrir sur les deux canaux.</summary>
    AJour,
    /// <summary>Un paquet de profils signe a ete verifie et installe (sans droits admin).</summary>
    ProfilsInstalles,
    /// <summary>Une version de l'App signee et verifiee attend ; son installation n'est pas encore
    /// automatique (l'echange des binaires eleve est differe).</summary>
    NouvelleVersionVerifiee,
    /// <summary>Serveur injoignable (ou trop lent) : on reste sur la version installee.</summary>
    Injoignable,
    /// <summary>Un paquet a ete refuse : signature, empreinte ou retrogradation. A dire, jamais a taire.</summary>
    Refusee,
    /// <summary>Echec local (ecriture du staging) : on reste sur la version installee.</summary>
    Echec,
}

/// <summary>Ce que la verification a constate, et, pour une version App verifiee, de quoi l'installer.</summary>
public sealed record ResultatMiseAJour(
    EtatMiseAJour Etat, string? CheminInstalleur = null, string? Sha256 = null, string? Version = null)
{
    public bool Installable => Etat == EtatMiseAJour.NouvelleVersionVerifiee
                               && CheminInstalleur is not null && Sha256 is not null && Version is not null;
}

/// <summary>
/// PHASE 18 — la mise a jour branchee dans l'app (revue du 10/09/2026 : « un produit qui ne se met
/// pas a jour reste un prototype vu du client »).
///
/// `UpdateComposition` et `AutoUpdater` etaient construits et testes depuis M5, et appeles par
/// Personne dans l'App ; le pied de page affichait une constante. Ici : au demarrage, montage, canal
/// profils puis canal App, sous un delai borne, et le pied de page dit ce qui a ete constate.
///
/// L'URL du serveur : <see cref="UrlParDefaut"/>, ou la variable <see cref="VariableUrl"/> qui la remplace (« off » = coupée). Avant le 09/10/2026 : aucun serveur
/// n'est deploye (geste du mainteneur), et une URL ecrite dans le code serait un chiffre publie a la main.
/// Sans elle : aucun reseau touche, « aucun serveur configure ».
///
/// Transport : HTTPS exige par le montage, contenu garanti par la signature Ed25519 du manifeste.
/// L'epinglage SPKI du serveur de mise a jour se posera avec son deploiement (ses empreintes
/// n'existent pas encore).
/// </summary>
public static class VerificationMiseAJour
{
    public const string VariableUrl = "DRDXT_UPDATE_URL";

    /// <summary>09/10/2026 (audit de pérennité) : sans adresse écrite, aucune copie installée n'aurait jamais pu recevoir de
    /// mise à jour, et l'installeur ne posait pas la variable. Les manifestes signés vivent sur le site GitHub Pages du dépôt
    /// public. Tant qu'il n'est pas publié, GitHub répond 404 : « joignable, rien à offrir », jamais une erreur.</summary>
    public const string UrlParDefaut = "https://ablanchard-dev.github.io/drdxt";

    /// <summary>La variable gagne quand elle existe ; « off » coupe toute vérification (aucune requête ne part).
    /// 09/10/2026 (revue) : pas « vide » : sous Windows, une variable posée vide n'existe pas pour le processus.</summary>
    public const string Coupee = "off";

    public static string? UrlEffective(string? variable) => string.IsNullOrWhiteSpace(variable) ? UrlParDefaut
        : string.Equals(variable.Trim(), Coupee, StringComparison.OrdinalIgnoreCase) ? null : variable;

    /// <summary>Au-dela, « injoignable » : une verification ne doit jamais tenir le demarrage.</summary>
    public static readonly TimeSpan DelaiParDefaut = TimeSpan.FromSeconds(15);

    /// <param name="verifierDeTest">Pour les TESTS seulement. null = clef de production (SEC5, 09/10/2026 : embarquée ;
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

    public static async Task<ResultatMiseAJour> VerifierAsync(
        Func<Result<AutoUpdater>> monter, TimeSpan delai, CancellationToken ct)
    {
        // 27/09/2026 : la borne couvre aussi le montage (cles, crypto, dossiers). Il tournait avant elle :
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
            var profils = await monte.Value.UpdateProfilesAsync(borne.Token).ConfigureAwait(false);
            var app = await monte.Value.UpdateAppAsync(borne.Token).ConfigureAwait(false);
            var etat = Classer(profils, app);
            // Le bouton « Installer » n'a de sens qu'avec ce qui a ete verifie : chemin, empreinte, version.
            return etat == EtatMiseAJour.NouvelleVersionVerifiee && monte.Value.DerniereVersionApp is { } m
                ? new(etat, monte.Value.StagedAppPayloadPath, m.Sha256, m.Version)
                : new(etat);
        }
        catch (OperationCanceledException) { return new(EtatMiseAJour.Injoignable); }
        catch (Exception) { return new(EtatMiseAJour.Echec); }
    }

    /// <summary>Deux resultats de canal, un etat. La nouvelle version de l'App passe d'abord ; un
    /// REFUS passe avant « injoignable » : un paquet rejete est une information de securite.</summary>
    public static EtatMiseAJour Classer(Result<UpdateOutcome> profils, Result<UpdateOutcome> app)
    {
        if (app is { IsOk: true, Value: UpdateOutcome.AppStaged }
            || app.Error?.Code == UpdateErrorCodes.ReplacerUnavailable)
            return EtatMiseAJour.NouvelleVersionVerifiee;

        var erreurs = new[] { profils, app }.Where(r => !r.IsOk).Select(r => r.Error?.Code).ToList();
        if (erreurs.Any(c => c is UpdateErrorCodes.SignatureInvalid or UpdateErrorCodes.HashMismatch
                                 or UpdateErrorCodes.Downgrade))
            return EtatMiseAJour.Refusee;
        if (erreurs.Any(c => c == UpdateErrorCodes.Offline)) return EtatMiseAJour.Injoignable;
        if (erreurs.Count > 0) return EtatMiseAJour.Echec;

        return profils.Value == UpdateOutcome.ProfilesInstalled ? EtatMiseAJour.ProfilsInstalles : EtatMiseAJour.AJour;
    }

    public static string CleResx(EtatMiseAJour e) => e switch
    {
        EtatMiseAJour.NonConfigure => "UI.Footer.Updates.NotConfigured",
        EtatMiseAJour.AJour => "UI.Footer.Updates.UpToDate",
        EtatMiseAJour.ProfilsInstalles => "UI.Footer.Updates.ProfilesInstalled",
        EtatMiseAJour.NouvelleVersionVerifiee => "UI.Footer.Updates.NewVersion",
        EtatMiseAJour.Injoignable => "UI.Footer.Updates.Offline",
        EtatMiseAJour.Refusee => "UI.Footer.Updates.Rejected",
        _ => "UI.Footer.Updates.Failed",
    };
}
