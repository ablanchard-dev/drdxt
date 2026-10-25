using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>
/// Un geste du guide que DrDXT sait faire lui-même, posé sous « Comment corriger » du constat qui le demande :
/// le joueur lit les étapes, ou laisse DrDXT les faire. <paramref name="annuler"/> absent : rien à défaire
/// (relancer un périphérique, par exemple), aucun « Annuler » n'est proposé. <paramref name="dejaFait"/> : un geste fait
/// dans une session précédente, dont seul l'Annuler reste à proposer.
/// </summary>
public sealed partial class GesteDuDocteur(string titre, string detail, Func<Task<Result>> faire, Func<Task<Result>>? annuler = null,
    bool dejaFait = false)
    : ObservableObject
{
    public string Titre { get; } = titre;
    public string Detail { get; } = detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeutFaire), nameof(PeutAnnuler))]
    private bool _fait = dejaFait;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeutFaire), nameof(PeutAnnuler))]
    private bool _enCours;

    // L'échec se dit sans le message du helper (anglais, technique) : la ligne dit que rien n'a été fait.
    [ObservableProperty]
    private bool _aEchoue;

    // Une partie seulement a pris (TweakPartiallyApplied) : quelque chose reste posé, le geste se dit à moitié et Annuler
    // reste possible.
    [ObservableProperty]
    private bool _aMoitie;

    // La réponse du helper n'est pas arrivée après l'envoi : le geste a peut-être été fait. « Rien n'a été changé » serait
    // faux ; il se dit incertain.
    [ObservableProperty]
    private bool _incertain;

    public bool PeutFaire => !Fait && !EnCours;
    public bool PeutAnnuler => Fait && !EnCours && annuler is not null;

    [RelayCommand]
    private Task Faire() => Jouer(faire, faitApres: true);

    [RelayCommand]
    private Task Annuler() => annuler is null ? Task.CompletedTask : Jouer(annuler, faitApres: false);

    private async Task Jouer(Func<Task<Result>> action, bool faitApres)
    {
        if (EnCours) return;
        EnCours = true;
        AEchoue = false;
        AMoitie = false;
        Incertain = false;
        try
        {
            var r = await action();
            if (r.IsOk) Fait = faitApres;
            else if (r.Error?.Code == ErrorCodes.TweakPartiallyApplied) (AMoitie, Fait) = (true, true);
            else if (r.Error?.Code is ErrorCodes.HelperNoReply or ErrorCodes.HelperNoReplyJournalisee) Incertain = true;
            else AEchoue = true;
        }
        catch (Exception)
        {
            AEchoue = true;
        }
        finally
        {
            EnCours = false;
        }
    }
}
