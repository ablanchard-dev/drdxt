using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>Un réglage nommé dont la valeur n'est plus celle d'usine : le texte du menu, maintenant et à l'usine.</summary>
public sealed record EcartAUsine(string Reglage, string Valeur, string Usine);

/// <summary>
/// Les réglages du BIOS qui s'écartent des valeurs d'usine, comparés octet par octet au magasin « StdDefaults » du même
/// firmware. Seuls les écarts que la table sait nommer sont montrés ; les autres octets changés sont comptés, pas
/// devinés (une partie de « Setup » porte de l'état que le firmware tient lui-même, pas un choix du joueur).
/// </summary>
public sealed record EcartsAUsine(IReadOnlyList<EcartAUsine> Nommes, int AutresOctets, int OctetsCompares)
{
    /// <summary>
    /// Pur. Tout ou rien, comme la lecture des réglages : une variable que la table nomme et qui manque au magasin, ou
    /// dont la taille diffère de celle que la table déclare, rend null. Toutes les variables nommées sont comparées.
    /// </summary>
    public static EcartsAUsine? Comparer(IReadOnlyList<(string Reglage, NvramLocation Emplacement)> table,
        IReadOnlyDictionary<(string Nom, string Guid), byte[]> vivantes, IReadOnlyList<NvarAmi.Entree> usine)
    {
        var nommes = new List<EcartAUsine>();
        var autres = 0;
        var compares = 0;
        foreach (var variable in table.GroupBy(t => (t.Emplacement.VarName, t.Emplacement.VendorGuid)))
        {
            var taille = variable.First().Emplacement.VarSize;
            if (!vivantes.TryGetValue(variable.Key, out var vivante) || vivante.Length != taille) return null;
            if (NvarAmi.Trouver(usine, variable.Key.VarName, variable.Key.VendorGuid) is not { } defaut || defaut.Length != taille)
                return null;
            compares += taille;
            var couverts = new bool[taille];
            foreach (var (reglage, loc) in variable)
            {
                // Un emplacement qui sort de sa variable : la table ne décrit pas ce firmware.
                if (loc.Offset < 0 || loc.Taille <= 0 || loc.Offset + loc.Taille > taille) return null;
                for (var i = loc.Offset; i < loc.Offset + loc.Taille && i < taille; i++) couverts[i] = true;
                if (vivante.AsSpan(loc.Offset, loc.Taille).SequenceEqual(defaut.AsSpan(loc.Offset, loc.Taille))) continue;
                // Une valeur hors du menu, d'un côté ou de l'autre : la table ne décrit pas ce firmware.
                if (NvramBiosSettingsReader.Decoder(loc, vivante) is not { } maintenant
                    || NvramBiosSettingsReader.Decoder(loc, defaut) is not { } avant)
                    return null;
                nommes.Add(new EcartAUsine(reglage, maintenant.Libelle, avant.Libelle));
            }
            for (var i = 0; i < taille; i++)
                if (!couverts[i] && vivante[i] != defaut[i]) autres++;
        }
        return new EcartsAUsine(nommes, autres, compares);
    }
}
