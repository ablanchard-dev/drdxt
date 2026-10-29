using System.Text.Json;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// L'écran de jeu à la plus haute fréquence que Windows propose à sa résolution : un écran 144, 165 ou 240 Hz laissé à
// 60 Hz, le défaut gratuit le plus répandu (`Finding.Display.RefreshBelowMax`). Sans droits : ChangeDisplaySettingsEx
// sans CDS_GLOBAL enregistre le mode dans le profil de l'utilisateur (Microsoft Learn), comme la page Affichage.
// L'ordre :
//   1. l'original est lu avant tout ;
//   2. CDS_TEST : si Windows refuse le mode à l'essai, rien n'est écrit ;
//   3. changement dynamique, non enregistré : si la machine s'éteint là, la session suivante repart sur l'ancien mode ;
//   4. relecture : si le mode courant n'est pas la cible, l'ancien revient ;
//   5. le joueur garde le mode dans les 15 s, sinon l'ancien revient : CDS_TEST dit que le pilote accepte le mode, pas
//      que l'écran l'affiche, et un écran noir ne se confirme pas ;
//   6. seulement alors, le mode est enregistré.
// Jugé « appliqué et relu », jamais chiffré en images : le snapshot n'est pas une op privilégiée, le moteur ne le relit
// pas et n'attribue aucun gain.
/// <summary>Règle l'écran de jeu sur la plus haute fréquence que Windows propose à sa résolution, sans droits élevés.
/// Proposé quand le scan lit une marge de fréquence ; gardé seulement si le joueur confirme l'image dans les 15 s.</summary>
public sealed class EcranFrequenceTweak(IDisplayInfo ecrans, IModeEcran mode, IConfirmationEcran confirmation,
    ISourceDAlimentation? alimentation = null) : ITweak
{
    /// <summary>Le délai que Windows laisse lui-même avant de revenir à l'ancien affichage.</summary>
    public static readonly TimeSpan DelaiDeConfirmation = TimeSpan.FromSeconds(15);

    private const int ToleranceHz = 1;

    public string Id => "fixit.ecran_frequence";
    public TweakCategory Category => TweakCategory.FixIt;
    public bool RequiresElevation => false;
    public bool RequiresRestorePoint => false;
    public string SummaryKey => "tweak.fixit.ecran_frequence.confort";

    /// <summary>Seulement quand l'écran de jeu est lu sous sa fréquence max à la même résolution, et pas sur batterie : un
    /// portable y est souvent mis à 60 Hz exprès, pour l'autonomie.</summary>
    public bool AppliesTo(HardwareInventory hw, SettingsState state)
        => state.DisplayRefresh?.HasHeadroom == TriState.On && alimentation?.SurBatterie() != true;

    public bool EtatNonLu(SettingsState state) => state.DisplayRefresh is not { HasHeadroom: not TriState.Unknown };

    public async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            if (alimentation?.SurBatterie() == true)
                return Echec(ErrorCodes.TweakNotApplicable, Docteur.T("EF.SurBatterie"));
            var ecran = ecrans.GetPrimaryGameDisplay();
            if (!ecran.IsOk || ecran.Value is not { DeviceName: { } nom } d)
                return Echec(ErrorCodes.TweakSdkUnavailable, Docteur.T("EF.EcranIllisible"));
            // Même tolérance que le scan (DisplayRefreshHeuristic.ToleranceHz) : 143 et 144 Hz sont le même mode.
            if (d.MaxRefreshHz - d.RefreshHz <= ToleranceHz)
                return Echec(ErrorCodes.TweakNotApplicable, Docteur.T("EF.DejaMax", d.RefreshHz));

            if (mode.Courant(nom) is not { } avant)
                return Echec(ErrorCodes.TweakSdkUnavailable, Docteur.T("EF.ModeIllisible"));
            var cible = avant with { Hz = d.MaxRefreshHz };

            if (mode.Tester(cible) is var essai and not 0)
                return Echec(ErrorCodes.TweakSdkUnavailable, Docteur.T("EF.RefusEssai", cible.Hz, essai));
            if (mode.Ecrire(cible, enregistrer: false) is var pose and not 0)
                return Echec(ErrorCodes.TweakSdkUnavailable, Docteur.T("EF.NonPose", cible.Hz, pose));

            if (mode.Courant(nom)?.Hz != cible.Hz)
                return Revenir(avant, Docteur.T("EF.RelectureSans", cible.Hz));
            if (!await confirmation.GarderAsync(cible.Hz, DelaiDeConfirmation).ConfigureAwait(false))
                return Revenir(avant, Docteur.T("EF.NonGarde", cible.Hz, DelaiDeConfirmation.TotalSeconds));
            if (mode.Ecrire(cible, enregistrer: true) is var garde and not 0)
                return Revenir(avant, Docteur.T("EF.NonEnregistre", cible.Hz, garde));

            // Le mode posé voyage avec l'original : l'annulation ne remet l'original que si l'écran porte encore celui-ci.
            return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, JsonSerializer.Serialize(avant), JsonSerializer.Serialize(cible)));
        }
        catch (Exception ex)
        {
            return Echec(ErrorCodes.TweakSdkUnavailable, Docteur.T("EF.Exception", ex.Message));
        }
    }

    public Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
        ModeEcran? avant;
        try
        {
            avant = JsonSerializer.Deserialize<ModeEcran>(snapshot.OriginalValueJson);
        }
        catch (JsonException)
        {
            avant = null;
        }
        if (avant is null || avant.Hz <= 0 || string.IsNullOrEmpty(avant.Peripherique))
            return Task.FromResult(Result.Fail(ErrorCodes.SnapshotInvalid, Docteur.T("EF.SnapshotIllisible")));

        try
        {
            // Le joueur a pu choisir une autre fréquence depuis : seule celle de DrDXT se rend. Un snapshot d'avant ce champ
            // n'a pas de mode posé : l'original se remet comme avant.
            if (Posee(snapshot) is { } posee)
            {
                if (mode.Courant(avant.Peripherique) is not { } courant)
                    return Task.FromResult(Result.Fail(ErrorCodes.TweakSdkUnavailable, Docteur.T("EF.ModeIllisible")));
                // Le nom \\.\DISPLAYn passe à l'écran branché à sa place : une autre définition, c'est un autre moniteur.
                if ((courant.Largeur, courant.Hauteur) != (posee.Largeur, posee.Hauteur))
                    return Task.FromResult(Result.Fail(ErrorCodes.TweakNotApplicable,
                        Docteur.T("EF.AutreEcran", posee.Largeur, posee.Hauteur, courant.Largeur, courant.Hauteur)));
                if (courant.Hz != posee.Hz)
                    return Task.FromResult(Result.Fail(ErrorCodes.TweakNotApplicable, Docteur.T("EF.ChangeDepuis", courant.Hz, posee.Hz)));
            }
            if (mode.Ecrire(avant, enregistrer: true) is var code and not 0)
                return Task.FromResult(Result.Fail(ErrorCodes.TweakSdkUnavailable, Docteur.T("EF.NonRemis", avant.Hz, code)));
            return Task.FromResult(mode.Courant(avant.Peripherique)?.Hz == avant.Hz
                ? Result.Ok()
                : Result.Fail(ErrorCodes.TweakSdkUnavailable, Docteur.T("EF.RelectureApresAnnulation", avant.Hz)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Fail(ErrorCodes.TweakSdkUnavailable, Docteur.T("EF.Exception", ex.Message)));
        }
    }

    private static ModeEcran? Posee(TweakSnapshot s)
    {
        if (string.IsNullOrEmpty(s.AppliedValueJson)) return null;
        try { return JsonSerializer.Deserialize<ModeEcran>(s.AppliedValueJson) is { Hz: > 0 } m ? m : null; }
        catch (JsonException) { return null; }
    }

    private Result<TweakSnapshot> Revenir(ModeEcran avant, string pourquoi)
    {
        var retour = mode.Ecrire(avant, enregistrer: false);
        // Pas revenu : l'écran reste au mode posé pour la session ; le lot n'est pas défait.
        return Echec(retour == 0 ? ErrorCodes.TweakSdkUnavailable : ErrorCodes.TweakPartiallyApplied, retour == 0
            ? Docteur.T("EF.Revenu", pourquoi, avant.Hz)
            : Docteur.T("EF.NonRevenu", pourquoi, avant.Hz, retour));
    }

    private static Result<TweakSnapshot> Echec(string code, string message) => Result<TweakSnapshot>.Fail(code, message);
}
