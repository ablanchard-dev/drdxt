using DrDxt.App.Profiles;
using DrDxt.App.ViewModels;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// « GPU tuning: tried in game by Optimize… » s'affichait en anglais dans l'App en français (vu sur une
/// capture de la carte graphique). Les notes du moteur restent en anglais ; la vue les traduit phrase par phrase.
/// </summary>
public sealed class NotesDuMoteurTraduitesTests
{
    private static string Cle(string k) => "<" + k + ">";

    [Fact]
    public void Chaque_phrase_connue_du_moteur_passe_par_sa_traduction()
    {
        var note = ScanProfileConsumer.NoteNonApplicable + "; " + ScanProfileConsumer.NoteReglageCarte;

        var t = ScanViewModel.TraduireNote(note, Cle);

        t.Should().Be("<Carte.Note.NonApplicable>; <Carte.Note.ReglageCarte>");
    }

    [Fact]
    public void Une_valeur_ramenee_dans_ses_bornes_garde_ses_deux_nombres()
    {
        var t = ScanViewModel.TraduireNote("power limit clamped 60→70% (GpuSafeBounds)", k => k + " {0}>{1}");

        t.Should().Be("Carte.Note.Borne.Puissance 60>70");
    }

    [Fact]
    public void Une_phrase_inconnue_passe_telle_quelle_plutot_que_d_etre_tue()
    {
        ScanViewModel.TraduireNote("something new", Cle).Should().Be("something new");
        ScanViewModel.TraduireNote(null, Cle).Should().BeEmpty();
    }
}
