using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

// 29/09/2026, photo (processeur) : sous « Pas pu lire », la température du processeur tenait quatre lignes de prose
// technique d'un bloc (zone ACPI, machine de test, pilote noyau), alors que chaque constat se lit déjà en une phrase
// avec la suite sous « Pourquoi ? ». Une ligne non lue suit la même règle.
public sealed class LigneNonLueSeReplieTests
{
    [Fact]
    public void Une_ligne_non_lue_montre_sa_premiere_phrase_et_replie_la_suite()
    {
        var entree = new EntreeCarte("processeur", Severity.Info, "Température du processeur",
            "Une zone thermique répond, mais elle ne suit pas la charge. Sans pilote noyau, la température reste hors de portée.",
            "", "", NonLu: true);

        var vus = StaApplication.Run(() =>
        {
            var vue = new DrDxt.App.Views.ScanView();
            var ligne = new ContentPresenter { Content = entree, ContentTemplate = (DataTemplate)vue.Resources["LigneNonLue"] };
            ligne.Measure(new Size(800, 600));
            ligne.Arrange(new Rect(0, 0, 800, 600));
            ligne.UpdateLayout();
            var textes = new List<string>();
            void Parcourir(DependencyObject d)
            {
                if (d is UIElement u && u.Visibility != Visibility.Visible) return;
                if (d is TextBlock t && !string.IsNullOrEmpty(t.Text)) textes.Add(t.Text);
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Parcourir(VisualTreeHelper.GetChild(d, i));
            }
            Parcourir(ligne);
            return textes;
        });

        vus.Should().Contain("Température du processeur");
        vus.Should().Contain(t => t.StartsWith("Une zone thermique répond"), "la première phrase reste à l'écran");
        vus.Should().NotContain(t => t.Contains("pilote noyau"), "la suite attend « Pourquoi ? »");
    }
}
