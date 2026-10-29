using DrDxt.Core;

namespace DrDxt.Detection.Settings;

/// <summary>
/// Interprète les deux valeurs de registre du Game Mode de Windows, toutes deux écrites par
/// <see cref="DrDxt.Application.Tweaks.Latency.GameModeOnTweak"/>.
/// <para>Sur Windows 11, l'interrupteur « Mode Jeu » des Paramètres écrit <c>AutoGameModeEnabled</c> ;
/// <c>AllowAutoGameMode</c> est le nom hérité de Windows 10 (documentation et guides Windows 11 concordants). Ne lire que
/// l'ancien rendrait <c>Unknown</c> pour un joueur qui l'a activé lui-même, et ferait proposer un réglage déjà mis. Lire
/// les deux est juste quelle que soit celle qui fait foi : seul l'ordre de priorité en dépend, quand elles se
/// contredisent.</para>
/// <para>Aucune des deux valeurs écrite (c'est le cas de la machine de référence : la clé <c>GameBar</c> ne porte que
/// <c>GamepadDoublePressIntervalMs</c>) : le mode Jeu n'a jamais été réglé, il est dans l'état par défaut de Windows,
/// actif : Microsoft l'active par défaut pour tout le système, et l'interrupteur des Paramètres est actif par défaut.</para>
/// </summary>
internal static class GameModeLecture
{
    internal const string CheminGameBar = @"Software\Microsoft\GameBar";

    /// <summary>Le nom écrit par l'interrupteur des Paramètres de Windows 11.</summary>
    internal const string NomModerne = "AutoGameModeEnabled";

    /// <summary>Le nom hérité de Windows 10, encore lu pour ne pas perdre les machines réglées.</summary>
    internal const string NomAncien = "AllowAutoGameMode";

    /// <summary>
    /// <c>On</c>, <c>Off</c>, <c>On</c> quand rien n'est écrit (le défaut de Windows), ou <c>Unknown</c> pour une valeur
    /// d'un type inattendu. Pur, pour être testé sans registre : c'est la partie qui peut se tromper en silence.
    /// </summary>
    internal static TriState Interpreter(object? moderne, object? ancien)
    {
        // Le moderne tranche : c'est celui que Windows 11 honore quand les deux existent.
        if (moderne is int m)
            return m == 1 ? TriState.On : TriState.Off;
        if (ancien is int a)
            return a == 1 ? TriState.On : TriState.Off;
        // Rien d'écrit : le défaut de Windows. Une valeur d'un type inattendu est un « je n'ai pas lu », jamais un
        // « c'est éteint ».
        return moderne is null && ancien is null ? TriState.On : TriState.Unknown;
    }
}
