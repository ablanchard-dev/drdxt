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

    public NvramBiosSettingsReader(Func<string, string, CancellationToken, Task<Result<byte[]>>> lireVariable)
        => _lireVariable = lireVariable;

    /// <summary>Liste vide = rien de lu (carte hors matrice, variable illisible, ou table qui ne
    /// colle pas au firmware). Jamais une lecture partielle.</summary>
    public async Task<IReadOnlyList<BiosSettingReading>> ReadAsync(
        string? carte, string? firmware, CancellationToken ct = default)
    {
        var entrees = BiosQualificationMatrix.Pour(carte, firmware).Where(e => e.Nvram is not null).ToList();
        if (entrees.Count == 0) return [];

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

            if (loc.Offset + loc.Taille > octets.Length) return [];
            long brut = loc.Taille switch
            {
                1 => octets[loc.Offset],
                2 => BitConverter.ToUInt16(octets, loc.Offset),
                4 => BitConverter.ToUInt32(octets, loc.Offset),
                _ => -1,
            };
            string libelle;
            if (loc.Values.Count > 0)
            {
                if (brut is < 0 or > 255 || !loc.Values.TryGetValue((byte)brut, out var l)) return [];
                libelle = l;
            }
            else
            {
                // Numerique : hors des bornes de l'IFR, la table ne decrit pas ce firmware.
                if (brut < loc.Min || brut > loc.Max) return [];
                libelle = brut == 0 && loc.ZeroVautAuto ? "Auto" : $"{brut}{(loc.Unite is null ? "" : " " + loc.Unite)}";
            }
            lus.Add(new BiosSettingReading(e.Setting, brut, libelle, loc.Default is { } d && brut == d));
        }
        return lus;
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
