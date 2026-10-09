using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks;

/// <summary>Ce que la relecture peut dire d'un levier applique.</summary>
public enum EtatRelu
{
    /// <summary>La valeur courante est REVENUE a celle d'avant l'application. Le gain de ce levier
    /// n'existe plus — un reboot, une mise a jour ou un autre outil l'a defait.
    /// C'est le seul etat qui appelle un geste, et le seul que personne ne remarquait.</summary>
    RevenuAvantApplication,

    /// <summary>La valeur courante DIFFERE de celle d'avant l'application.
    ///
    /// **Ce n'est pas « le levier est correctement applique »**, et la nuance decide de ce que le
    /// produit a le droit de dire. Le snapshot ne porte que l'etat original ; il ne porte pas la
    /// valeur visee. Un tiers qui aurait pose une troisieme valeur donnerait exactement ce
    /// resultat.</summary>
    DifferentDeAvant,

    /// <summary>L'op de ce snapshot ne sait pas se relire — elle ne touche pas le registre, ou
    /// porte un type qu'on ne lit pas encore. « Je ne sais pas lire » n'est jamais « c'est
    /// applique ».</summary>
    NonRelisable,

    /// <summary>La lecture a echoue. Un trou de lecture, pas un etat.</summary>
    NonLu,
}

/// <summary>Le resultat de la relecture, avec de quoi le contester.</summary>
/// <param name="Etat">Ce qui peut etre dit.</param>
/// <param name="Cible">La feuille relue, « ruche\chemin\nom », ou <c>null</c> si non relisable.</param>
/// <param name="ValeurAvant">Ce que le snapshot portait, ou <c>null</c> quand la valeur etait absente.</param>
/// <param name="ValeurCourante">Ce qui est lu maintenant, ou <c>null</c> quand la valeur est absente.</param>
public sealed record Relecture(
    EtatRelu Etat, string? Cible, string? ValeurAvant, string? ValeurCourante);

/// <summary>
/// Relire ce qu'un levier a ecrit — une fois, pour tous les leviers de registre.
///
/// ══ pourquoi cette classe tient en une page ══
/// La matrice mesuree le 09/09/2026 disait : quinze leviers savent agir, **READBACK inconnu sur
/// douze**. La reponse evidente etait d'ajouter une methode a `ITweak` et de l'implementer douze
/// fois. Elle n'etait pas necessaire : **le snapshot porte deja la reponse.**
///
/// `LatencyTweakBase.RevertAsync` deserialise `OriginalValueJson` en `PrivilegedOp` et la rejoue.
/// Ce JSON est donc une op TYPEE qui nomme exactement la feuille touchee — ruche, chemin, nom,
/// type, valeur d'avant. Relire ne demande alors aucune connaissance du levier : la feuille est
/// dans le snapshot, et une seule fonction couvre tous les leviers qui ecrivent dans le registre.
///
/// ══ ce qu'elle ne dit pas, et c'est la moitie de son interet ══
/// Elle ne peut pas affirmer « ce levier est correctement applique ». Le snapshot porte l'etat
/// Original, jamais la valeur VISEE. Tout ce qui se deduit honnetement, c'est : la valeur courante
/// est-elle revenue a celle d'avant, ou non. Les deux etats s'appellent donc
/// <see cref="EtatRelu.RevenuAvantApplication"/> et <see cref="EtatRelu.DifferentDeAvant"/>, et pas
/// « perdu » et « applique » — deux noms qui affirmeraient plus que la mesure ne porte.
///
/// ══ ce qu'elle debloque ══
/// Onze leviers sur quinze ne sont pas persistes : leurs gains disparaissent au reboot **sans que
/// rien ne le remarque**. `RevenuAvantApplication` est exactement le constat qui le remarque.
/// </summary>
public static class TweakReadback
{
    /// <summary>Relit la feuille que decrit <paramref name="snapshot"/>.</summary>
    /// <param name="lecteur">Le seam de lecture registre. Non elevee : lire ne demande pas l'UAC.</param>
    public static Relecture Relire(TweakSnapshot? snapshot, IRegistryReader lecteur)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.OriginalValueJson))
            return new Relecture(EtatRelu.NonRelisable, null, null, null);

        PrivilegedOp? op;
        IReadOnlyList<PrivilegedOp>? composite = null;
        try
        {
            var decode = PrivilegedOpCodec.Deserialize(snapshot.OriginalValueJson);
            op = decode.IsOk ? decode.Value : null;
            if (op is null && CompositeSnapshotCodec.Decode(snapshot.OriginalValueJson) is { IsOk: true, Value: { Count: > 0 } ops })
                composite = ops;
        }
        catch (Exception)
        {
            // Un snapshot illisible est un trou, pas un etat : le meme reflexe que partout ailleurs
            // dans ce depot.
            return new Relecture(EtatRelu.NonLu, null, null, null);
        }

        // 06/10/2026 (revue) : un composite (mode Jeu : deux feuilles) n'est revenu que si toutes ses feuilles le
        // sont ; une feuille non lue ou non relisable rend tout le composite non lu ou non relisable.
        if (composite is not null)
        {
            var feuilles = composite.Select(o => RelireOp(o, lecteur)).ToList();
            var cible = string.Join(" ; ", feuilles.Select(r => r.Cible));
            var etat = feuilles.Any(r => r.Etat == EtatRelu.NonLu) ? EtatRelu.NonLu
                : feuilles.Any(r => r.Etat == EtatRelu.NonRelisable) ? EtatRelu.NonRelisable
                : feuilles.All(r => r.Etat == EtatRelu.RevenuAvantApplication) ? EtatRelu.RevenuAvantApplication
                : EtatRelu.DifferentDeAvant;
            return new Relecture(etat, cible, null, null);
        }

        return RelireOp(op, lecteur);
    }

    private static Relecture RelireOp(PrivilegedOp? op, IRegistryReader lecteur) => op switch
    {
        // La valeur d'avant EXISTAIT : revert la reposerait.
        SetRegistryValueOp set when set.Kind == RegistryValueType.DWord
            => Comparer(lecteur, set.Hive, set.Path, set.Name, set.Value),

        // 06/10/2026 (revue) : les chaînes (VRR) et les chaînes multiples (fichier d'échange) se relisent aussi,
        // comparées telles quelles : la chaîne d'avant est exactement ce que revert reposerait.
        SetRegistryValueOp { Kind: RegistryValueType.String } set
            => ComparerTexte(lecteur.ReadString(set.Hive, set.Path, set.Name), set),
        SetRegistryValueOp { Kind: RegistryValueType.MultiString } set
            => ComparerTexte(lecteur.ReadMultiString(set.Hive, set.Path, set.Name), set),

        // La valeur d'avant n'existait pas : revert la supprimerait, donc « avant » = absente.
        DeleteRegistryValueOp del
            => Comparer(lecteur, del.Hive, del.Path, del.Name, null),

        _ => new Relecture(EtatRelu.NonRelisable, null, null, null),
    };

    private static Relecture ComparerTexte(Result<string?> lu, SetRegistryValueOp avant)
    {
        var cible = $"{avant.Hive}\\{avant.Path}\\{avant.Name}";
        if (!lu.IsOk) return new Relecture(EtatRelu.NonLu, cible, avant.Value, null);
        return new Relecture(string.Equals(lu.Value, avant.Value, StringComparison.Ordinal)
            ? EtatRelu.RevenuAvantApplication : EtatRelu.DifferentDeAvant, cible, avant.Value, lu.Value);
    }

    private static Relecture Comparer(IRegistryReader lecteur, RegistryHiveKind ruche, string chemin,
        string nom, string? avant)
    {
        var cible = $"{ruche}\\{chemin}\\{nom}";
        var lu = lecteur.ReadDword(ruche, chemin, nom);
        if (!lu.IsOk) return new Relecture(EtatRelu.NonLu, cible, avant, null);

        var courante = lu.Value?.ToString();

        // Comparaison sur la valeur, pas sur la chaine : « 0 » et « 00 » sont la meme chose pour
        // le registre, et un ecart de formatage ferait crier une perte de gain qui n'existe pas.
        var identique = uint.TryParse(avant, out var a) && lu.Value is { } c
            ? a == c
            : string.Equals(avant, courante, StringComparison.OrdinalIgnoreCase);

        return new Relecture(
            identique ? EtatRelu.RevenuAvantApplication : EtatRelu.DifferentDeAvant,
            cible, avant, courante);
    }
}
