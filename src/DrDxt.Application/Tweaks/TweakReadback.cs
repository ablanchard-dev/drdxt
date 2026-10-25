using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks;

/// <summary>Ce que la relecture peut dire d'un levier applique.</summary>
public enum EtatRelu
{
    /// <summary>La valeur courante est revenue a celle d'avant l'application. Le gain de ce levier
    /// n'existe plus : un reboot, une mise a jour ou un autre outil l'a defait.
    /// C'est le seul etat qui appelle un geste, et le seul que personne ne remarquait.</summary>
    RevenuAvantApplication,

    /// <summary>La valeur courante differe de celle d'avant l'application.
    ///
    /// **Ce n'est pas « le levier est correctement applique »**, et la nuance decide de ce que le
    /// produit a le droit de dire. Le snapshot ne porte que l'etat original ; il ne porte pas la
    /// valeur visee. Un tiers qui aurait pose une troisieme valeur donnerait exactement ce
    /// resultat.</summary>
    DifferentDeAvant,

    /// <summary>L'op de ce snapshot ne sait pas se relire : elle ne touche pas le registre, ou
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

/// <summary>Ce que porte un réglage posé : encore la valeur de DrDXT (ou rien ne le relit), revenu à l'origine, ou changé depuis
/// (ni l'une ni l'autre).</summary>
public enum PoseRelue { DeDrDxt, ALOrigine, ChangeDepuis }

/// <summary>
/// Relire ce qu'un levier a ecrit : une fois, pour tous les leviers de registre.
///
/// ══ pourquoi cette classe tient en une page ══
/// Pas de methode de relecture dans `ITweak`, a implementer levier par levier : le snapshot porte
/// deja la reponse.
///
/// `LatencyTweakBase.RevertAsync` deserialise `OriginalValueJson` en `PrivilegedOp` et la rejoue.
/// Ce JSON est donc une op typee qui nomme exactement la feuille touchee : ruche, chemin, nom,
/// type, valeur d'avant. Relire ne demande alors aucune connaissance du levier : la feuille est
/// dans le snapshot, et une seule fonction couvre tous les leviers qui ecrivent dans le registre.
///
/// ══ ce qu'elle ne dit pas, et c'est la moitie de son interet ══
/// Elle ne peut pas affirmer « ce levier est correctement applique ». Le snapshot porte l'etat
/// original, jamais la valeur visee. Tout ce qui se deduit honnetement, c'est : la valeur courante
/// est-elle revenue a celle d'avant, ou non. Les deux etats s'appellent donc
/// <see cref="EtatRelu.RevenuAvantApplication"/> et <see cref="EtatRelu.DifferentDeAvant"/>, et pas
/// « perdu » et « applique », deux noms qui affirmeraient plus que la mesure ne porte.
///
/// ══ ce qu'elle debloque ══
/// Une partie des leviers n'est pas reposee au demarrage : leurs gains disparaitraient au redemarrage sans que
/// rien ne le remarque. `RevenuAvantApplication` est exactement le constat qui le remarque.
/// </summary>
public static class TweakReadback
{
    /// <summary>Tout rendu passe par là : rendre un réglage changé depuis réécrirait le choix du joueur. Une feuille que le
    /// registre ne relit pas (pilote, carte, plan d'alimentation) se juge par <paramref name="verdict"/>, le vérificateur de la
    /// pose : ni la valeur posée ni l'origine, c'est un changement d'ailleurs. Sans relecture possible : la valeur de DrDXT.</summary>
    /// <param name="verdict">Le vérificateur de la pose (<see cref="AppliedStateVerifier"/>) ; consulté seulement quand le
    /// registre ne relit pas la feuille.</param>
    public static PoseRelue EtatDeLaPose(TweakSnapshot s, IRegistryReader? lecteur, Func<TweakSnapshot, EtatApplique>? verdict = null)
    {
        // Origine égale à la valeur posée = DrDXT n'a rien changé.
        if (string.Equals(s.AppliedValueJson, s.OriginalValueJson, StringComparison.Ordinal)) return PoseRelue.ALOrigine;
        var surOrigine = lecteur is null ? EtatRelu.NonRelisable : Relire(s, lecteur).Etat;
        if (surOrigine == EtatRelu.NonRelisable && verdict is not null)
            return verdict(s) switch
            {
                EtatApplique.NotApplied => PoseRelue.ALOrigine,
                EtatApplique.Drifted => PoseRelue.ChangeDepuis,
                _ => PoseRelue.DeDrDxt,
            };
        if (lecteur is null) return PoseRelue.DeDrDxt;
        if (s.AppliedValueJson is { Length: > 0 } posee)
        {
            var surPosee = Relire(new TweakSnapshot(s.TweakId, posee), lecteur).Etat;
            if (surPosee == EtatRelu.RevenuAvantApplication) return PoseRelue.DeDrDxt;
            if (surPosee == EtatRelu.DifferentDeAvant)
                return surOrigine == EtatRelu.RevenuAvantApplication ? PoseRelue.ALOrigine : PoseRelue.ChangeDepuis;
        }
        return surOrigine == EtatRelu.RevenuAvantApplication ? PoseRelue.ALOrigine : PoseRelue.DeDrDxt;
    }

    /// <summary>Relu, le réglage porte encore la valeur que DrDXT a posée : une preuve positive (la feuille relue à la valeur
    /// posée, ou le vérificateur qui dit « appliqué »). « Rien ne le relit » n'en est pas une.</summary>
    public static bool EncorePoseRelue(TweakSnapshot s, IRegistryReader? lecteur, Func<TweakSnapshot, EtatApplique>? verdict = null)
    {
        if (string.Equals(s.AppliedValueJson, s.OriginalValueJson, StringComparison.Ordinal)) return false;
        var surOrigine = lecteur is null ? EtatRelu.NonRelisable : Relire(s, lecteur).Etat;
        if (surOrigine == EtatRelu.NonRelisable) return verdict?.Invoke(s) == EtatApplique.Applied;
        return lecteur is not null && s.AppliedValueJson is { Length: > 0 } posee
            && Relire(new TweakSnapshot(s.TweakId, posee), lecteur).Etat == EtatRelu.RevenuAvantApplication;
    }

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

        // Un composite (mode Jeu : deux feuilles) n'est revenu que si toutes ses feuilles le
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
        // DirectXUserGlobalSettings porte aussi Auto HDR, que le joueur règle lui-même : seules les clés de DrDXT s'y relisent.
        SetRegistryValueOp { Path: DirectXUserGlobalSettings.Chemin, Name: DirectXUserGlobalSettings.Nom } set
            => ComparerClesDeDrDxt(lecteur, set.Hive, set.Value),
        DeleteRegistryValueOp { Path: DirectXUserGlobalSettings.Chemin, Name: DirectXUserGlobalSettings.Nom } del
            => ComparerClesDeDrDxt(lecteur, del.Hive, null),

        // La valeur d'avant existait : revert la reposerait.
        SetRegistryValueOp set when set.Kind == RegistryValueType.DWord
            => Comparer(lecteur, set.Hive, set.Path, set.Name, set.Value),

        // Les chaînes (VRR) et les chaînes multiples (fichier d'échange) se relisent aussi,
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

    private static Relecture ComparerClesDeDrDxt(IRegistryReader lecteur, RegistryHiveKind ruche, string? avant)
    {
        var cible = $"{ruche}\\{DirectXUserGlobalSettings.Chemin}\\{DirectXUserGlobalSettings.Nom}";
        var lu = lecteur.ReadString(ruche, DirectXUserGlobalSettings.Chemin, DirectXUserGlobalSettings.Nom);
        if (!lu.IsOk) return new Relecture(EtatRelu.NonLu, cible, avant, null);
        var attendues = DirectXUserGlobalSettings.ClesDeDrDxtDe(avant);
        var lues = DirectXUserGlobalSettings.ClesDeDrDxtDe(lu.Value);
        return new Relecture(attendues == lues ? EtatRelu.RevenuAvantApplication : EtatRelu.DifferentDeAvant, cible, avant, lu.Value);
    }

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
