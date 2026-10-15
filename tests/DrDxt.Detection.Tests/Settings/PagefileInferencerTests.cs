using DrDxt.Core;
using DrDxt.Detection.Settings;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Settings;

// Fiabilité de la RAM. Un fichier d'échange désactivé est un vrai risque de fiabilité (limite de
// commit → gel ou plantage ; Microsoft, « Introduction to the page file »). On l'infère de la REG_MULTI_SZ brute
// HKLM\...\Memory Management\PagingFiles : valeur vide = fichier d'échange désactivé. Un fichier présent
// (géré par le système « C:\pagefile.sys 0 0 » ou taille personnalisée) est un choix valide, peut-être
// délibéré → On, jamais remis en cause. Illisible ou null → Unknown (jamais un mensonge).
public class PagefileInferencerTests
{
    [Fact]
    public void Empty_list_means_pagefile_disabled_off()
    {
        PagefileInferencer.Infer(Array.Empty<string>()).Should().Be(TriState.Off);
    }

    [Fact]
    public void Only_blank_entries_means_disabled_off()
    {
        PagefileInferencer.Infer(new[] { "", "   " }).Should().Be(TriState.Off);
    }

    [Fact]
    public void System_managed_entry_is_on()
    {
        PagefileInferencer.Infer(new[] { @"C:\pagefile.sys 0 0" }).Should().Be(TriState.On);
    }

    [Fact]
    public void Custom_fixed_size_is_on_not_second_guessed()
    {
        // Un fichier d'échange personnalisé présent est un choix valide et délibéré : on ne signale que désactivé.
        PagefileInferencer.Infer(new[] { @"C:\pagefile.sys 4096 8192" }).Should().Be(TriState.On);
    }

    [Fact]
    public void Automatically_managed_wildcard_form_is_on_not_a_false_disabled()
    {
        // L'état par défaut « géré automatiquement » stocke une entrée sans tailles (souvent le
        // joker « ?:\pagefile.sys »). C'est un fichier d'échange présent et sain → On, jamais un faux
        // « désactivé » sur une machine saine (ne jamais inventer un problème). Encodage vérifié :
        // auto = entrée sans tailles, désactivé = valeur vide.
        PagefileInferencer.Infer(new[] { @"?:\pagefile.sys" }).Should().Be(TriState.On);
    }

    [Fact]
    public void Any_real_entry_among_blanks_is_on()
    {
        PagefileInferencer.Infer(new[] { "", @"D:\pagefile.sys 0 0" }).Should().Be(TriState.On);
    }

    [Fact]
    public void Null_is_unknown_not_a_lie()
    {
        PagefileInferencer.Infer(null).Should().Be(TriState.Unknown);
    }
}
