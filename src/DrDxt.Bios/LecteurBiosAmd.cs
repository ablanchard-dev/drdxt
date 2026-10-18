using System.Security.Cryptography;
using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Lit les réglages d'une carte AMD avec la table de sa carte et de sa version (<see cref="TablesBiosAmd"/>), tout ou
/// rien : le firmware doit être celui de la table (empreinte de son magasin d'usine), chaque variable doit avoir la taille
/// de l'IFR, chaque copie doit tenir dans les valeurs de son menu, et chaque réglage doit avoir au plus une copie
/// visible. La copie lue est celle que le menu montre, jamais une copie cachée. Pur ; lecture seule.
/// </summary>
public static class LecteurBiosAmd
{
    /// <summary>Liste vide = rien de nommé. Un réglage dont aucune copie n'est visible n'est pas rendu : le menu ne le
    /// montre pas au joueur.</summary>
    public static IReadOnlyList<BiosSettingReading> Lire(TableBiosAmd table,
        IReadOnlyDictionary<(string Nom, string Guid), byte[]> vivantes, IReadOnlyList<NvarAmi.Entree>? usine)
    {
        if (usine is null) return [];
        var declarees = table.Variables.ToDictionary(v => (v.Nom, v.Guid));
        var defauts = new Dictionary<(string, string), byte[]>();
        foreach (var v in table.Variables)
        {
            if (!vivantes.TryGetValue((v.Nom, v.Guid), out var vivante) || vivante.Length != v.Taille) return [];
            // L'identité du firmware : sans elle, un décalage d'un à quatre octets ne fait sortir qu'une partie des
            // valeurs de leurs listes, et une table voisine passerait pour la bonne.
            if (NvarAmi.Trouver(usine, v.Nom, v.Guid) is not { } defaut || defaut.Length != v.Taille
                || Convert.ToHexStringLower(SHA256.HashData(defaut)) != v.EmpreinteUsine)
                return [];
            defauts[(v.Nom, v.Guid)] = defaut;
        }

        var lus = new List<BiosSettingReading>();
        foreach (var reglage in table.Reglages)
        {
            var visibles = new List<CopieDeReglage>();
            foreach (var copie in reglage.Copies)
            {
                var loc = copie.Emplacement;
                if (!declarees.TryGetValue((loc.VarName, loc.VendorGuid), out var v) || v.Taille != loc.VarSize) return [];
                // Chaque copie, même cachée, doit tenir dans son menu : c'est la vérification d'identité octet par octet.
                if (NvramBiosSettingsReader.Decoder(loc, vivantes[(loc.VarName, loc.VendorGuid)]) is null) return [];
                bool? montree = true;
                foreach (var c in copie.Visible)
                {
                    if (!vivantes.TryGetValue((c.VarName, c.VendorGuid), out var octets) || !declarees.ContainsKey((c.VarName, c.VendorGuid))
                        || c.Offset < 0 || c.Offset >= octets.Length)
                    { montree = null; break; }
                    if (!c.Valeurs.Contains(octets[c.Offset])) { montree = false; break; }
                }
                if (montree is null) return [];
                if (montree == true) visibles.Add(copie);
            }
            if (visibles.Count > 1) return [];
            if (visibles.Count == 0) continue;
            var l = visibles[0].Emplacement;
            var vivanteDe = vivantes[(l.VarName, l.VendorGuid)];
            var valeur = NvramBiosSettingsReader.Decoder(l, vivanteDe)!.Value;
            var usineDe = defauts[(l.VarName, l.VendorGuid)];
            lus.Add(new BiosSettingReading(reglage.Nom, valeur.Brut, valeur.Libelle,
                vivanteDe.AsSpan(l.Offset, l.Taille).SequenceEqual(usineDe.AsSpan(l.Offset, l.Taille))));
        }
        return lus;
    }
}
