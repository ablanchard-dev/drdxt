using System.Runtime.Versioning;
using DrDxt.Core;
using Microsoft.Win32;

namespace DrDxt.ElevatedHelper.Execution;

/// <summary>
/// <c>PagingFiles</c> vide, c'est plus de fichier d'échange au redémarrage suivant : la limite de mémoire est atteinte, jeux
/// et système gèlent ou plantent. L'allowlist l'admettait (l'Annuler de <c>fixit.sane_pagefile</c> rend une origine vide),
/// et un appelant compromis, ou un journal d'annulation forgé, pouvait donc couper le fichier d'échange. Le vide n'est écrit
/// que si l'origine lue était vide et que le réglage de DrDXT est encore en place : quand il écrit par-dessus une valeur vide,
/// le helper note la valeur qu'il pose (<c>HKLM\SOFTWARE\DrDXT\Helper\PagingFilesOrigineVide</c>), et le vide ne passe
/// que si la valeur actuelle est exactement celle-là. Le joueur l'a changée depuis : le vide est refusé et la note retirée. La
/// note survit au redémarrage, qui est justement quand l'Annuler arrive. Une entrée sur un lecteur nommé doit viser un lecteur
/// fixe qui existe.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GardeDuFichierDEchange(RegistryKey? racine = null, string? cheminMemoire = null, string? cheminHelper = null,
    Func<char, bool>? lecteurFixe = null)
{
    public const string CheminMemoire = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    public const string Nom = "PagingFiles";
    private const string Note = "PagingFilesOrigineVide";

    private RegistryKey Racine => racine ?? Registry.LocalMachine;
    private string Memoire => cheminMemoire ?? CheminMemoire;
    private string Helper => cheminHelper ?? @"SOFTWARE\DrDXT\Helper";
    private readonly Func<char, bool> _lecteurFixe = lecteurFixe ?? LecteurFixeQuiExiste;

    public static bool Vise(RegistryHiveKind hive, string path, string name)
        => hive == RegistryHiveKind.LocalMachine && string.Equals(name, Nom, StringComparison.OrdinalIgnoreCase)
           && string.Equals(path.Trim('\\'), CheminMemoire, StringComparison.OrdinalIgnoreCase);

    /// <summary>Avant l'écriture : la raison du refus, ou null. Une écriture non vide par-dessus une valeur vide note la valeur
    /// posée (refusée si la note ne s'écrit pas : l'Annuler ne pourrait plus rendre le vide).</summary>
    public string? Avant(string valeur)
    {
        var entrees = Entrees(valeur);
        if (entrees.Any(e => e.Length >= 2 && e[1] == ':' && e[0] != '?' && !_lecteurFixe(char.ToUpperInvariant(e[0]))))
            return "Page file entry names a drive that is not an existing fixed drive.";
        string actuel;
        try
        {
            using var cle = Racine.OpenSubKey(Memoire);
            actuel = Normal(cle?.GetValue(Nom) is string[] lu ? string.Join('\n', lu) : null);
        }
        catch (Exception) { return "The current page file setting could not be read."; }
        var note = LireNote();
        // Le réglage de DrDXT n'est plus en place (le joueur a réglé son fichier d'échange) : l'origine vide ne se rend plus.
        if (note is not null && actuel.Length > 0 && actuel != note) { Noter(null); note = null; }
        if (entrees.Length == 0)
            return actuel.Length == 0 || note is not null ? null : "An empty page file setting is only written back when it was the original.";
        if ((actuel.Length == 0 || note is not null) && !Noter(Normal(valeur)))
            return "The empty original page file setting could not be kept.";
        return null;
    }

    /// <summary>Après une écriture réussie : l'origine vide rendue, la note s'efface.</summary>
    public void Apres(string valeur)
    {
        if (Entrees(valeur).Length == 0) Noter(null);
    }

    private static string Normal(string? valeur) => string.Join('\n', Entrees(valeur));

    private static string[] Entrees(string? valeur)
        => (valeur ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

    private string? LireNote()
    {
        try
        {
            using var cle = Racine.OpenSubKey(Helper);
            return cle?.GetValue(Note) as string is { Length: > 0 } v ? v : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>La valeur posée par DrDXT par-dessus le vide d'origine, ou null pour retirer la note.</summary>
    private bool Noter(string? posee)
    {
        try
        {
            using var cle = Racine.CreateSubKey(Helper, writable: true);
            if (posee is not null) cle.SetValue(Note, posee, RegistryValueKind.String);
            else cle.DeleteValue(Note, throwOnMissingValue: false);
            return true;
        }
        catch (Exception) { return false; }
    }

    private static bool LecteurFixeQuiExiste(char lettre)
    {
        try { return new DriveInfo(lettre + @":\") is { IsReady: true, DriveType: DriveType.Fixed }; }
        catch (Exception) { return false; }
    }
}
