using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// LIT les reglages BIOS dans la NVRAM, pour les cartes et versions de firmware que
/// <see cref="BiosQualificationMatrix"/> decrit avec un emplacement releve dans l'IFR.
///
/// Tout ou rien, et c'est voulu : si la variable n'a pas la taille que l'IFR declare, ou si UN
/// octet tombe hors des valeurs du menu, la table ne decrit pas ce firmware et aucune lecture
/// n'est rendue. Une seule valeur fausse suffirait a refaire le faux « XMP actif ».
///
/// Lecture seule : la couture ne sait que lire.
/// </summary>
public sealed class NvramBiosSettingsReader
{
    private readonly Func<string, string, CancellationToken, Task<Result<byte[]>>> _lireVariable;
    private readonly IReadOnlyList<TableBiosAmd>? _tablesAmd;

    /// <param name="tablesAmd">Les tables des cartes AMD ; null = <see cref="TablesBiosAmd.Connues"/>.</param>
    public NvramBiosSettingsReader(Func<string, string, CancellationToken, Task<Result<byte[]>>> lireVariable,
        IReadOnlyList<TableBiosAmd>? tablesAmd = null)
    {
        _lireVariable = lireVariable;
        _tablesAmd = tablesAmd;
    }

    /// <summary>Liste vide = rien de lu (carte hors matrice, variable illisible, ou table qui ne
    /// colle pas au firmware). Jamais une lecture partielle.</summary>
    public async Task<IReadOnlyList<BiosSettingReading>> ReadAsync(
        string? carte, string? firmware, CancellationToken ct = default)
    {
        var entrees = BiosQualificationMatrix.Pour(carte, firmware).Where(e => e.Nvram is not null).ToList();
        if (entrees.Count == 0)
            return TablesBiosAmd.Pour(carte, firmware, _tablesAmd) is { } amd ? await LireAmdAsync(amd, ct) : [];

        var variables = new Dictionary<(string, string), byte[]>();
        var lus = new List<BiosSettingReading>();
        foreach (var e in entrees)
        {
            var loc = e.Nvram!;
            var cle = (loc.VarName, loc.VendorGuid);
            if (!variables.TryGetValue(cle, out var octets))
            {
                Result<byte[]> r;
                try { r = await _lireVariable(loc.VarName, loc.VendorGuid, ct); }
                catch { return []; }
                if (!r.IsOk || r.Value is null || r.Value.Length != loc.VarSize) return [];
                variables[cle] = octets = r.Value;
            }

            if (Decoder(loc, octets) is not { } v) return [];
            lus.Add(new BiosSettingReading(e.Setting, v.Brut, v.Libelle, loc.Default is { } d && v.Brut == d));
        }
        return lus;
    }

    // Une carte AMD : toutes les variables de sa table et le magasin d'usine, puis la lecture tout ou rien.
    private async Task<IReadOnlyList<BiosSettingReading>> LireAmdAsync(TableBiosAmd table, CancellationToken ct)
    {
        try
        {
            var vivantes = new Dictionary<(string, string), byte[]>();
            foreach (var v in table.Variables)
            {
                var r = await _lireVariable(v.Nom, v.Guid, ct);
                if (!r.IsOk || r.Value is null) return [];
                vivantes[(v.Nom, v.Guid)] = r.Value;
            }
            var magasin = await _lireVariable(NvarAmi.NomMagasin, GuidsUefi.AmiStandardDefaults, ct);
            return LecteurBiosAmd.Lire(table, vivantes, magasin.IsOk && magasin.Value is { } m ? NvarAmi.Decoder(m) : null);
        }
        catch { return []; }
    }

    // Ce que la comparaison à l'usine couvre : la matrice de la carte, sinon chaque copie de la table AMD (nommée par son
    // chemin de menu quand le réglage en a plusieurs).
    private List<(string, NvramLocation)> Emplacements(string? carte, string? firmware)
    {
        var matrice = BiosQualificationMatrix.Pour(carte, firmware).Where(e => e.Nvram is not null).Select(e => (e.Setting, e.Nvram!)).ToList();
        if (matrice.Count > 0 || TablesBiosAmd.Pour(carte, firmware, _tablesAmd) is not { } amd) return matrice;
        return amd.Reglages.SelectMany(r => r.Copies.Select(c => (r.Copies.Count == 1 ? r.Nom : $"{r.Nom} ({c.CheminMenu})", c.Emplacement))).ToList();
    }

    /// <summary>Pur : la valeur brute et le texte du menu, ou null quand l'octet sort des valeurs ou des bornes que
    /// l'IFR déclare (la table ne décrit alors pas ce firmware).</summary>
    internal static (long Brut, string Libelle)? Decoder(NvramLocation loc, byte[] octets)
    {
        if (loc.Offset < 0 || loc.Offset + loc.Taille > octets.Length) return null;
        long brut = loc.Taille switch
        {
            1 => octets[loc.Offset],
            2 => BitConverter.ToUInt16(octets, loc.Offset),
            4 => BitConverter.ToUInt32(octets, loc.Offset),
            _ => -1,
        };
        if (loc.Values.Count > 0)
            return brut is >= 0 and <= 255 && loc.Values.TryGetValue((byte)brut, out var l) ? (brut, l) : null;
        // Numerique : hors des bornes de l'IFR, la table ne decrit pas ce firmware.
        if (brut < loc.Min || brut > loc.Max) return null;
        return (brut, brut == 0 && loc.ZeroVautAuto ? "Auto" : $"{brut}{(loc.Unite is null ? "" : " " + loc.Unite)}");
    }

    /// <summary>
    /// Ce qui n'est plus à sa valeur d'usine, d'après le magasin « StdDefaults » du firmware lui-même, et pas d'après le
    /// défaut recopié dans la table. Toutes les variables que la table nomme sont comparées. Null = rien de comparé
    /// (carte hors table, variable ou magasin illisible, tailles différentes).
    /// </summary>
    public async Task<EcartsAUsine?> LireEcartsAsync(string? carte, string? firmware, CancellationToken ct = default)
    {
        var emplacements = Emplacements(carte, firmware);
        if (emplacements.Count == 0) return null;
        try
        {
            var magasin = await _lireVariable(NvarAmi.NomMagasin, GuidsUefi.AmiStandardDefaults, ct);
            if (!magasin.IsOk || magasin.Value is null || NvarAmi.Decoder(magasin.Value) is not { } usine) return null;
            var vivantes = new Dictionary<(string, string), byte[]>();
            foreach (var (nom, guid) in emplacements.Select(e => (e.Item2.VarName, e.Item2.VendorGuid)).Distinct())
            {
                var r = await _lireVariable(nom, guid, ct);
                if (!r.IsOk || r.Value is null) return null;
                vivantes[(nom, guid)] = r.Value;
            }
            return EcartsAUsine.Comparer(emplacements, vivantes, usine);
        }
        catch { return null; }
    }

    /// <summary>
    /// Verse les lectures dans l'etat des reglages, apres les avoir recoupees avec ce que Windows
    /// voit deja. Une contradiction jette toutes les lectures : c'est la table qu'elle accuse.
    ///
    /// Recoupements (ce que Windows prouve, et seulement ca) :
    /// · ReBAR lu par Windows (On/Off) doit etre la valeur du BIOS ;
    /// · un demarrage LEGACY prouve le CSM (st.Csm == On) : le BIOS ne peut pas dire « UEFI ».
    /// XMP n'a pas de recoupement dur : « profil desactive » avec une RAM au-dessus du standard
    /// est un reglage a la main (le cas releve le 17/09), pas une contradiction.
    /// </summary>
    public static SettingsState Appliquer(SettingsState st, IReadOnlyList<BiosSettingReading> lus)
    {
        if (lus.Count == 0) return st;
        var par = lus.ToDictionary(l => l.Setting, l => l);

        TriState? rebar = par.TryGetValue("Re-Size BAR", out var rb) ? EnabledOuNon(rb) : null;
        if (rebar is { } r && st.ReBar is TriState.On or TriState.Off && st.ReBar != r) return st;

        TriState? csm = par.TryGetValue("CSM", out var c) ? (c.Libelle == "CSM" ? TriState.On : TriState.Off) : null;
        if (csm == TriState.Off && st.Csm == TriState.On) return st;

        TriState? xmp = par.TryGetValue("XMP", out var x) ? (x.Raw == 0 ? TriState.Off : TriState.On) : null;

        return st with
        {
            XmpExpo = xmp ?? st.XmpExpo,
            ReBar = rebar ?? st.ReBar,
            Csm = csm ?? st.Csm,
            BiosLu = lus,
        };
    }

    private static TriState EnabledOuNon(BiosSettingReading l) => l.Raw == 0 ? TriState.Off : TriState.On;
}
