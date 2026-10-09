using DrDxt.Core;

namespace DrDxt.Detection.Settings;

/// <summary>
/// Interprète les deux valeurs de registre qui portent le Game Mode de Windows.
///
/// <para>Pourquoi ce fichier existe. La détection ne lisait que <c>AllowAutoGameMode</c>, alors
/// que <see cref="DrDxt.Application.Tweaks.Latency.GameModeOnTweak"/> écrit les <b>deux</b>
/// valeurs — son propre commentaire désigne l'ancienne comme « the one detection reads ». Sur
/// Windows 11, l'interrupteur des Paramètres écrit <c>AutoGameModeEnabled</c> ;
/// <c>AllowAutoGameMode</c> est le nom hérité de Windows 10. Un joueur qui active le Game Mode
/// lui-même pose donc le nom moderne, que la détection ne regardait pas : elle rendait
/// <c>Unknown</c>, et le produit proposait un levier pour un réglage déjà mis.</para>
///
/// <para>Source : documentation et guides Windows 11 concordants (18/09/2026) — l'interrupteur
/// « Mode Jeu » des Paramètres correspond à <c>AutoGameModeEnabled</c>, <c>AllowAutoGameMode</c>
/// étant l'ancienne valeur encore citée par les guides Windows 10.</para>
///
/// <para><b>Lire les deux est juste quelle que soit la réponse à « laquelle fait foi ».</b>
/// Si l'une des deux est présente, on sait quelque chose ; si aucune ne l'est, on ne sait rien.
/// Cette correction ne dépend donc pas de la qualité de la source — seul l'ordre de priorité
/// en dépend, et il ne départage que le cas où les deux se contredisent.</para>
///
/// <para>Mesure du 18/09/2026 sur la machine de référence : la clé <c>GameBar</c> existe et ne
/// porte que <c>GamepadDoublePressIntervalMs</c>. Aucune des deux valeurs n'est écrite —
/// <c>Unknown</c> est la bonne réponse ici, et le reste.</para>
/// </summary>
internal static class GameModeLecture
{
    internal const string CheminGameBar = @"Software\Microsoft\GameBar";

    /// <summary>Le nom écrit par l'interrupteur des Paramètres de Windows 11.</summary>
    internal const string NomModerne = "AutoGameModeEnabled";

    /// <summary>Le nom hérité de Windows 10, encore lu pour ne pas perdre les machines réglées.</summary>
    internal const string NomAncien = "AllowAutoGameMode";

    /// <summary>
    /// <c>On</c>, <c>Off</c>, ou <c>Unknown</c> si rien de lisible n'est écrit.
    ///
    /// Pur exprès : c'est la seule partie qui peut se tromper en silence, donc la seule qui a
    /// besoin d'être testée sans registre.
    /// </summary>
    internal static TriState Interpreter(object? moderne, object? ancien)
    {
        // Le moderne tranche : c'est celui que Windows 11 honore quand les deux existent.
        if (moderne is int m)
            return m == 1 ? TriState.On : TriState.Off;
        if (ancien is int a)
            return a == 1 ? TriState.On : TriState.Off;
        // Une valeur absente ou d'un type inattendu est un « je n'ai pas lu », jamais un
        // « c'est éteint » — et jamais non plus le défaut de Windows, qu'on n'a pas mesuré.
        return TriState.Unknown;
    }
}
