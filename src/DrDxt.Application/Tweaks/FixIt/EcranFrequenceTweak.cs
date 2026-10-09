using System.Text.Json;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// L-ECRAN : l'écran de jeu réglé à la plus haute fréquence que Windows propose à sa résolution courante.
//
// Un écran 144, 165 ou 240 Hz laissé à 60 Hz est le défaut gratuit le plus répandu chez un joueur. Le scan le dit
// depuis longtemps (`Finding.Display.RefreshBelowMax`) ; ce levier le répare.
//
// Sans droits : ChangeDisplaySettingsEx sans CDS_GLOBAL enregistre le mode dans le profil de l'utilisateur (Microsoft
// Learn), comme le fait la page Affichage des Paramètres. Rien ne passe par le helper élevé.
//
// L'ordre, et pourquoi :
//   1. l'original est lu avant tout ;
//   2. CDS_TEST : si Windows refuse le mode à l'essai, rien n'est écrit ;
//   3. changement dynamique, non enregistré : si la machine s'éteint là, la session suivante repart sur l'ancien mode ;
//   4. relecture : si le mode courant n'est pas la cible, l'ancien revient ;
//   5. le joueur garde le mode dans les 15 s, sinon l'ancien revient : CDS_TEST dit que le pilote accepte le mode, pas
//      que l'écran l'affiche, et un écran noir ne se confirme pas ;
//   6. seulement alors, le mode est enregistré.
//
// Honnêteté : c'est un réglage de fluidité, jugé « appliqué et relu », jamais chiffré en images. Le snapshot n'est pas
// une op privilégiée : le moteur ne sait pas le relire, donc il n'attribue aucun gain à ce levier.
// -----------------------------------------------------------------------
public sealed class EcranFrequenceTweak(IDisplayInfo ecrans, IModeEcran mode, IConfirmationEcran confirmation) : ITweak
{
    /// <summary>Le délai que Windows laisse lui-même avant de revenir à l'ancien affichage.</summary>
    public static readonly TimeSpan DelaiDeConfirmation = TimeSpan.FromSeconds(15);

    private const int ToleranceHz = 1;

    public string Id => "fixit.ecran_frequence";
    public TweakCategory Category => TweakCategory.FixIt;
    public bool RequiresElevation => false;
    public bool RequiresRestorePoint => false;
    public string SummaryKey => "tweak.fixit.ecran_frequence.confort";

    /// <summary>Seulement quand l'écran de jeu est lu sous sa fréquence max à la même résolution.</summary>
    public bool AppliesTo(HardwareInventory hw, SettingsState state) => state.DisplayRefresh?.HasHeadroom == TriState.On;

    public bool EtatNonLu(SettingsState state) => state.DisplayRefresh is not { HasHeadroom: not TriState.Unknown };

    public async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            var ecran = ecrans.GetPrimaryGameDisplay();
            if (!ecran.IsOk || ecran.Value is not { DeviceName: { } nom } d)
                return Echec(ErrorCodes.TweakSdkUnavailable, "l'écran de jeu n'a pas pu être lu.");
            // Même tolérance que le scan (DisplayRefreshHeuristic.ToleranceHz) : 143 et 144 Hz sont le même mode.
            if (d.MaxRefreshHz - d.RefreshHz <= ToleranceHz)
                return Echec(ErrorCodes.TweakNotApplicable, $"l'écran tourne déjà à sa fréquence max ({d.RefreshHz} Hz).");

            if (mode.Courant(nom) is not { } avant)
                return Echec(ErrorCodes.TweakSdkUnavailable, "le mode courant de l'écran n'a pas pu être lu.");
            var cible = avant with { Hz = d.MaxRefreshHz };

            if (mode.Tester(cible) is var essai and not 0)
                return Echec(ErrorCodes.TweakSdkUnavailable, $"Windows refuse {cible.Hz} Hz à l'essai (code {essai}) : rien n'a été écrit.");
            if (mode.Ecrire(cible, enregistrer: false) is var pose and not 0)
                return Echec(ErrorCodes.TweakSdkUnavailable, $"Windows n'a pas posé {cible.Hz} Hz (code {pose}).");

            if (mode.Courant(nom)?.Hz != cible.Hz)
                return Revenir(avant, $"la relecture ne montre pas {cible.Hz} Hz");
            if (!await confirmation.GarderAsync(cible.Hz, DelaiDeConfirmation).ConfigureAwait(false))
                return Revenir(avant, $"{cible.Hz} Hz n'a pas été gardé dans les {DelaiDeConfirmation.TotalSeconds:0} s");
            if (mode.Ecrire(cible, enregistrer: true) is var garde and not 0)
                return Revenir(avant, $"{cible.Hz} Hz n'a pas pu être enregistré (code {garde})");

            return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, JsonSerializer.Serialize(avant)));
        }
        catch (Exception ex)
        {
            return Echec(ErrorCodes.TweakSdkUnavailable, $"écran : {ex.Message}");
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
            return Task.FromResult(Result.Fail(ErrorCodes.SnapshotInvalid, "Le snapshot de l'écran n'est pas un mode lisible."));

        try
        {
            if (mode.Ecrire(avant, enregistrer: true) is var code and not 0)
                return Task.FromResult(Result.Fail(ErrorCodes.TweakSdkUnavailable, $"Windows n'a pas remis {avant.Hz} Hz (code {code})."));
            return Task.FromResult(mode.Courant(avant.Peripherique)?.Hz == avant.Hz
                ? Result.Ok()
                : Result.Fail(ErrorCodes.TweakSdkUnavailable, $"la relecture ne montre pas {avant.Hz} Hz après l'annulation."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Fail(ErrorCodes.TweakSdkUnavailable, $"écran : {ex.Message}"));
        }
    }

    private Result<TweakSnapshot> Revenir(ModeEcran avant, string pourquoi)
    {
        var retour = mode.Ecrire(avant, enregistrer: false);
        return Echec(ErrorCodes.TweakSdkUnavailable, retour == 0
            ? $"{pourquoi} : l'ancien mode ({avant.Hz} Hz) est revenu."
            : $"{pourquoi}, et l'ancien mode ({avant.Hz} Hz) n'a pas pu être remis (code {retour}) : il revient à la prochaine session.");
    }

    private static Result<TweakSnapshot> Echec(string code, string message) => Result<TweakSnapshot>.Fail(code, message);
}
