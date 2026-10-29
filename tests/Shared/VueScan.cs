using System.IO;
using System.Text.RegularExpressions;

namespace DrDxt.Tests.Shared;

/// <summary>
/// La vue de scan telle qu'un lecteur la lisait d'un seul fichier : ScanView.xaml, chaque partie de Views/Scan remise à
/// la place de son &lt;scan:Nom/&gt;, et les ressources partagées à la place de leur fusion. Les gardes qui lisent le XAML
/// en texte voient ainsi l'écran entier, dans l'ordre où il s'affiche, quel que soit le fichier qui porte chaque bloc.
/// </summary>
internal static class VueScan
{
    private static readonly Regex Emplacement = new(@"^[ \t]*<scan:(\w+)/>[ \t]*\r?$", RegexOptions.Multiline);
    private static readonly Regex Fusion = new(@"^[ \t]*<ResourceDictionary Source=""(?:Scan/)?Ressources\.xaml""/>[ \t]*\r?$", RegexOptions.Multiline);

    public static string Texte(string cheminDeScanView)
    {
        var dossier = Path.Combine(Path.GetDirectoryName(cheminDeScanView)!, "Scan");
        return Recomposer(File.ReadAllText(cheminDeScanView), dossier, Interieur(File.ReadAllText(Path.Combine(dossier, "Ressources.xaml")), "ResourceDictionary"));
    }

    private static string Recomposer(string texte, string dossier, string ressources)
    {
        texte = Fusion.Replace(texte, _ => ressources, 1);
        return Emplacement.Replace(texte, m =>
            Recomposer(Interieur(File.ReadAllText(Path.Combine(dossier, m.Groups[1].Value + ".xaml")), "UserControl"), dossier, ressources));
    }

    // Le contenu d'une partie, sans son en-tête : après ses ressources pour un UserControl, entre les balises sinon.
    private static string Interieur(string texte, string racine)
    {
        var debut = racine == "UserControl"
            ? texte.IndexOf("</UserControl.Resources>", StringComparison.Ordinal) + "</UserControl.Resources>".Length
            : texte.IndexOf('>', texte.IndexOf("<" + racine, StringComparison.Ordinal)) + 1;
        var fin = texte.LastIndexOf("</" + racine + ">", StringComparison.Ordinal);
        if (debut <= 0 || fin < debut) throw new InvalidOperationException("Partie de la vue de scan illisible : " + racine);
        return texte[debut..fin].Trim('\r', '\n');
    }
}
