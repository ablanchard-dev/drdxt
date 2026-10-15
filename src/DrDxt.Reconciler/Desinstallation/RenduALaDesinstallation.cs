using System.Globalization;
using System.Text;
using DrDxt.Core;

namespace DrDxt.Reconciler.Desinstallation;

// -----------------------------------------------------------------------
// RenduALaDesinstallation : ce que la desinstallation rend, et ce qu'elle laisse.
//
// Lancee par l'installeur Inno (`DrDxt.ElevatedHelper.exe --uninstall-revert`, deja eleve), elle relit le
// manifeste signe (verify-only, cle publique de la machine) et, pour chaque origine qu'il porte (les
// corrections au redemarrage (`Tweaks`) comme les leviers gardes (`RevertOnly`)) :
//
//   * une feuille n'est rendue que si sa valeur courante est encore celle que DrDXT y a posee. Un reglage
//     change a la main depuis est respecte, et le rapport le dit. Une valeur illisible, ou une valeur posee
//     que le manifeste ne connait pas, ne se touche pas : on ne rend pas ce qu'on ne sait pas comparer ;
//   * un instantane composite (plusieurs feuilles) est tout-ou-rien : il n'est
//     rendu que si toutes ses feuilles sont encore a la valeur posee par DrDXT. Une seule changee a la
//     main, illisible, ou dont la cible a disparu ⇒ aucune feuille n'est touchee, et le rapport nomme la
//     coupable. Rendre les autres laisserait un melange que personne n'a choisi ;
//   * avant toute ecriture, la cible doit exister encore (la cle, le service, le plan) : disparue ⇒ rien
//     n'est ecrit, dit dans le rapport, plutot qu'une valeur orpheline laissee derriere ;
//   * aucun echec n'arrete le reste : une entree qui echoue (ou qui jette) est ecrite dans le rapport, et
//     l'entree suivante est tentee. Un manifeste absent ou illisible rend un rapport « rien rendu ».
//
// La valeur posee : pour une correction, la valeur voulue traduite par le meme mapper que le
// reconciliateur ; pour un levier garde, `DesiredValueJson` = l'op avant (ou le composite des ops avant)
// que le levier a ecrite. Les entrees sont rendues de la plus ancienne a la plus recente : une feuille deja
// rendue par une entree plus ancienne n'est pas rendue une seconde fois (la plus ancienne origine est la
// vraie).
//
// Ecrire passe par un IElevatedClient : en production l'executeur en processus du helper, derriere la meme
// allowlist feuille-exacte que le tube.
// -----------------------------------------------------------------------

/// <summary>Lit la valeur courante a la cible d'une ecriture, sous la forme que cette ecriture pose :
/// registre DWORD/QWORD en decimal invariant, chaine telle quelle ; plan actif en GUID « D » ; service
/// « running » / « stopped » / « notinstalled » ; reglage DRS en decimal. <c>Ok(null)</c> = la valeur est
/// absente. <c>Fail</c> = illisible : rien ne sera touche.</summary>
public interface ILectureDesCibles
{
    Result<string?> Lire(PrivilegedOp ecriture);

    /// <summary>La cible existe-t-elle encore ? Une valeur absente n'est pas
    /// la meme chose qu'une cible disparue : la cle de registre a pu etre supprimee, le service
    /// desinstalle, le plan d'alimentation efface. Y ecrire recreerait une valeur orpheline : un
    /// reglage que DrDXT n'a jamais pose la, laisse derriere lui par la desinstallation. <c>Ok(false)</c>
    /// = disparue, on n'ecrit rien et on le dit. <c>Fail</c> = on ne sait pas : on ne touche pas non plus.</summary>
    Result<bool> CibleExiste(PrivilegedOp ecriture);
}

public enum IssueDuRendu
{
    Rendu,
    Respecte,
    DejaRendu,
    ValeurPoseeInconnue,
    Illisible,
    EchecDEcriture,

    /// <summary>Une seule feuille d'un levier composite a ete changee a la main : aucune
    /// feuille du composite n'est rendue. Rendre les autres laisserait la machine dans un etat que ni
    /// DrDXT ni l'utilisateur n'a jamais choisi.</summary>
    CompositePartiellementModifie,

    /// <summary>La cible de l'ecriture n'existe plus (cle supprimee, service desinstalle,
    /// plan efface). Rien n'est ecrit : on ne pose pas une valeur orpheline.</summary>
    CibleDisparue,
}

/// <param name="EnSuspens">L'origine reste à rendre (écriture refusée, cible illisible, composite bloqué par l'une d'elles) :
/// le manifeste ne se met pas de côté.</param>
public sealed record LigneDuRendu(string TweakId, string Cible, IssueDuRendu Issue, string Detail, bool EnSuspens = false)
{
    public bool OrigineEnSuspens => EnSuspens || Issue is IssueDuRendu.EchecDEcriture or IssueDuRendu.Illisible;
}

/// <param name="ManifesteNonLu">Pourquoi le manifeste n'a pas ete lu ; <c>null</c> quand il l'a ete.</param>
public sealed record RapportDuRendu(string? ManifesteNonLu, IReadOnlyList<LigneDuRendu> Lignes)
{
    public string EnTexte(DateTimeOffset quand)
    {
        var s = new StringBuilder();
        s.AppendLine(Docteur.T("RD.Titre"));
        s.AppendLine(Docteur.T("RD.Date") + quand.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        if (ManifesteNonLu is not null)
        {
            s.AppendLine(Docteur.T("RD.ManifesteNonLu", ManifesteNonLu));
            s.AppendLine(Docteur.T("RD.RienRendu"));
            return s.ToString();
        }

        s.AppendLine(Docteur.T("RD.ManifesteLu"));
        if (Lignes.Count == 0)
        {
            s.AppendLine(Docteur.T("RD.AucuneOrigine"));
            return s.ToString();
        }

        var rendus = Lignes.Count(l => l.Issue == IssueDuRendu.Rendu);
        var respectes = Lignes.Count(l => l.Issue == IssueDuRendu.Respecte);
        s.AppendLine(Docteur.T("RD.Compte", rendus, respectes, Lignes.Count - rendus - respectes));
        foreach (var l in Lignes)
            s.AppendLine($"- [{l.Issue}] {l.TweakId} - {l.Cible} : {l.Detail}");
        return s.ToString();
    }
}

public sealed class RenduALaDesinstallation
{
    private readonly IDesiredStateStore _manifeste;
    private readonly ILectureDesCibles _lecture;
    private readonly IElevatedClient _ecriture;
    private static readonly IReapplyOpMapper Reaffirmation = new DefaultReapplyOpMapper(new DefaultReconcilableTweakCatalog());

    /// <summary>Ce que DrDXT A pose pour ce levier, feuille par feuille : la meme regle pour la
    /// desinstallation (qui rend) et la verification au lancement (qui relit). Une seule definition, pour
    /// que les deux ne jugent jamais deux choses differentes. <c>null</c> = ce qui a ete pose n'est pas
    /// connu : le verdict sera « inconnu », jamais « applique ».</summary>
    /// <param name="estCorrection">Entree de <c>Tweaks</c> (correction reaffirmee) plutot que de <c>RevertOnly</c>.</param>
    public static IReadOnlyList<PrivilegedOp>? PoseesDe(AppliedTweak t, bool estCorrection)
        => Ops(t.PoseesJson) ?? (estCorrection ? Reaffirmee(t) : Ops(t.DesiredValueJson));

    public RenduALaDesinstallation(IDesiredStateStore manifeste, ILectureDesCibles lecture, IElevatedClient ecriture)
    {
        _manifeste = manifeste;
        _lecture = lecture;
        _ecriture = ecriture;
    }

    /// <summary>Ne jette jamais : tout ce qui arrive finit dans le rapport.</summary>
    public async Task<RapportDuRendu> ExecuterAsync(CancellationToken ct)
    {
        Result<DesiredState> charge;
        try
        {
            charge = await _manifeste.LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new RapportDuRendu($"exception {ex.GetType().Name} : {ex.Message}", Array.Empty<LigneDuRendu>());
        }

        if (!charge.IsOk || charge.Value is null)
            return new RapportDuRendu($"{charge.Error?.Code} : {charge.Error?.Message}", Array.Empty<LigneDuRendu>());

        var lignes = new List<LigneDuRendu>();
        var rendues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entrees = (charge.Value.Tweaks ?? Array.Empty<AppliedTweak>()).Select(t => (Entree: t, EstCorrection: true))
            .Concat((charge.Value.RevertOnly ?? Array.Empty<AppliedTweak>()).Select(t => (Entree: t, EstCorrection: false)))
            .Where(e => e.Entree is not null)
            .OrderBy(e => e.Entree.AppliedAt) // stable : a date egale, les corrections d'abord
            .ToList();

        foreach (var (entree, estCorrection) in entrees)
        {
            try
            {
                await RendreAsync(entree, estCorrection, rendues, lignes, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lignes.Add(new LigneDuRendu(entree.TweakId, "-", IssueDuRendu.EchecDEcriture,
                    $"exception {ex.GetType().Name} : {ex.Message}"));
            }
        }

        return new RapportDuRendu(null, lignes);
    }

    /// <summary>Une feuille jugee, avant toute ecriture. <paramref name="Retour"/> non nul ⇒ elle est
    /// prete a etre rendue.</summary>
    private sealed record FeuilleJugee(string Cible, IssueDuRendu Issue, string Detail,
        PrivilegedOp? Retour = null, string? Attendue = null);

    private async Task RendreAsync(AppliedTweak t, bool estCorrection, HashSet<string> rendues,
        List<LigneDuRendu> lignes, CancellationToken ct)
    {
        var retours = Ops(t.OriginalValueJson);
        if (retours is null)
        {
            lignes.Add(new LigneDuRendu(t.TweakId, "-", IssueDuRendu.Illisible,
                Docteur.T("RD.OrigineIndecodable")));
            return;
        }

        // Les valeurs posees. Quand le manifeste porte l'origine complete du levier
        // (`PoseesJson`, ecrit depuis l'Apply), elle fait foi : elle nomme chaque feuille. Sinon (levier a
        // une seule feuille, ou manifeste ecrit avant ce champ), on retombe sur la valeur voulue,
        // reaffirmee pour une correction, decodee telle quelle pour un levier garde.
        var posees = PoseesDe(t, estCorrection);

        // ── 1. Juger toutes les feuilles, sans rien ecrire ─────────────────────────────────────
        var jugees = retours.Select(retour => Juger(retour, posees)).ToList();

        // ── 2. Le tout-ou-rien des composites ────────────────────────
        // Un levier composite a plusieurs feuilles qui n'ont de sens qu'ensemble. Si une seule a ete
        // changee a la main (ou est devenue illisible, ou sa cible a disparu), rendre les autres
        // laisserait la machine dans un melange que personne n'a choisi : ni l'etat d'origine, ni
        // celui de DrDXT, ni celui de l'utilisateur. On ne touche donc a aucune feuille, et on le dit
        // nommement. « Deja rendue par une entree plus ancienne » n'est pas un empechement : la feuille
        // est deja revenue a l'origine, c'est le but.
        var estComposite = retours.Count > 1;
        if (estComposite)
        {
            var bloquantes = jugees
                .Where(f => f.Retour is null && f.Issue != IssueDuRendu.DejaRendu)
                .ToList();
            if (bloquantes.Count > 0)
            {
                var pourquoi = string.Join(" ; ", bloquantes.Select(b => $"{b.Cible} ({b.Issue})"));
                // Bloqué par une feuille illisible ou non écrite : rien n'est jugé, l'origine reste à rendre.
                var enSuspens = bloquantes.Any(b => b.Issue is IssueDuRendu.Illisible or IssueDuRendu.EchecDEcriture);
                foreach (var f in jugees)
                    lignes.Add(new LigneDuRendu(t.TweakId, f.Cible, IssueDuRendu.CompositePartiellementModifie,
                        Docteur.T("RD.CompositePartiel", pourquoi), enSuspens));
                return;
            }
        }

        // ── 3. Ecrire ──────────────────────────────────────────────────────────────────────────
        foreach (var f in jugees)
        {
            if (f.Retour is null)
            {
                lignes.Add(new LigneDuRendu(t.TweakId, f.Cible, f.Issue, f.Detail));
                continue;
            }

            var ecrit = await _ecriture.ExecuteAsync(f.Retour, ct).ConfigureAwait(false);
            if (ecrit.IsOk)
            {
                rendues.Add(f.Cible);
                lignes.Add(new LigneDuRendu(t.TweakId, f.Cible, IssueDuRendu.Rendu,
                    Docteur.T("RD.Rendue", f.Attendue, Decrire(f.Retour))));
            }
            else
            {
                lignes.Add(new LigneDuRendu(t.TweakId, f.Cible, IssueDuRendu.EchecDEcriture,
                    $"{ecrit.Error?.Code} : {ecrit.Error?.Message}"));
            }
        }

        FeuilleJugee Juger(PrivilegedOp retour, IReadOnlyList<PrivilegedOp>? posees)
        {
            // La règle de la carte, écrite ici et non plus seulement à l'inscription au manifeste.
            if (retour is ApplyNvApiClockOffsetOp or ApplyGpuFanCurveOp or ApplyAdlxTuningOp { Kind: AdlxTuningKind.Undervolt })
                return new FeuilleJugee(retour.GetType().Name, IssueDuRendu.ValeurPoseeInconnue,
                    Docteur.T("RD.RegleCarte"));
            var cible = Cible(retour);
            if (cible is null)
                return new FeuilleJugee(retour.GetType().Name, IssueDuRendu.ValeurPoseeInconnue,
                    Docteur.T("RD.SansCible"));

            if (rendues.Contains(cible))
                return new FeuilleJugee(cible, IssueDuRendu.DejaRendu, Docteur.T("RD.DejaRendue"));

            var posee = posees?.FirstOrDefault(p => string.Equals(Cible(p), cible, StringComparison.OrdinalIgnoreCase));
            var attendue = posee is null ? null : ValeurPosee(posee);
            if (posee is null || attendue is null)
                return new FeuilleJugee(cible, IssueDuRendu.ValeurPoseeInconnue,
                    Docteur.T("RD.ValeurInconnue"));

            // La cible existe-t-elle encore ? Avant de comparer quoi que ce soit : une cle supprimee,
            // un service desinstalle, un plan efface. Y ecrire poserait une valeur orpheline.
            var existe = _lecture.CibleExiste(retour);
            if (!existe.IsOk)
                return new FeuilleJugee(cible, IssueDuRendu.Illisible,
                    Docteur.T("RD.ExistenceInconnue", existe.Error?.Code, existe.Error?.Message));
            if (!existe.Value)
                return new FeuilleJugee(cible, IssueDuRendu.CibleDisparue,
                    Docteur.T("RD.CibleDisparue"));

            var lue = _lecture.Lire(posee);
            if (!lue.IsOk)
                return new FeuilleJugee(cible, IssueDuRendu.Illisible,
                    Docteur.T("RD.ValeurIllisible", lue.Error?.Code, lue.Error?.Message));

            // La regle : on ne rend que ce que DrDXT a pose et qui est encore la.
            if (!string.Equals(lue.Value, attendue, StringComparison.Ordinal))
                return new FeuilleJugee(cible, IssueDuRendu.Respecte,
                    Docteur.T("RD.Respecte", lue.Value ?? Docteur.T("TE.Absente"), attendue));

            return new FeuilleJugee(cible, IssueDuRendu.Rendu, string.Empty, retour, attendue);
        }
    }

    private static IReadOnlyList<PrivilegedOp>? Reaffirmee(AppliedTweak t)
    {
        var op = Reaffirmation.Map(t.TweakId, t.DesiredValueJson);
        return op.IsOk && op.Value is not null ? new[] { op.Value } : null;
    }

    /// <summary>Une op, ou le composite d'ops ; <c>null</c> sinon.</summary>
    private static IReadOnlyList<PrivilegedOp>? Ops(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var une = PrivilegedOpCodec.Deserialize(json);
            if (une.IsOk && une.Value is not null) return new[] { une.Value };
            var plusieurs = CompositeSnapshotCodec.Decode(json);
            return plusieurs.IsOk ? plusieurs.Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Ou une op ecrit : la cle qui apparie une op de retour a l'op posee.</summary>
    private static string? Cible(PrivilegedOp op) => op switch
    {
        SetRegistryValueOp s => Docteur.T("RD.Cible.Registre", $"{s.Hive}\\{s.Path.Trim().Trim('\\')}\\{s.Name}"),
        DeleteRegistryValueOp d => Docteur.T("RD.Cible.Registre", $"{d.Hive}\\{d.Path.Trim().Trim('\\')}\\{d.Name}"),
        SetActivePowerPlanOp => Docteur.T("RD.Cible.Plan"),
        SetProcessorBoostModeOp b => Docteur.T("RD.Cible.Boost", b.PlanGuid),
        SetCpuPackagePowerLimitOp => Docteur.T("RD.Cible.Pl1"),
        SetWirelessPowerSavingOp w => Docteur.T("RD.Cible.Wifi", w.PlanGuid),
        RunServiceControlOp r => $"service {r.ServiceName}",
        ApplyNvApiDrsSettingOp a => Docteur.T("RD.Cible.PiloteNvidia", a.ProfileName, $"0x{a.SettingId:X8}"),
        DeleteNvApiDrsSettingOp x => Docteur.T("RD.Cible.PiloteNvidia", x.ProfileName, $"0x{x.SettingId:X8}"),
        // Les réglages de la carte. Le genre ADLX nomme la feuille (la carte dédiée, une seule).
        ApplyNvApiPowerLimitOp n => Docteur.T("RD.Cible.LimiteNvidia", n.GpuIndex),
        ApplyAdlxTuningOp { Kind: AdlxTuningKind.PowerLimit } a => Docteur.T("RD.Cible.LimiteAmd", a.GpuIndex),
        ApplyAdlxTuningOp a => Docteur.T("RD.Cible.PiloteAmd", a.Kind),
        _ => null,
    };

    /// <summary>La valeur qu'une op pose, sous la forme que <see cref="ILectureDesCibles"/> rend ; <c>null</c>
    /// quand elle n'est pas comparable.</summary>
    private static string? ValeurPosee(PrivilegedOp op) => op switch
    {
        SetRegistryValueOp { Kind: RegistryValueType.DWord } s
            => uint.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var d)
                ? d.ToString(CultureInfo.InvariantCulture) : null,
        SetRegistryValueOp { Kind: RegistryValueType.QWord } s
            => ulong.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var q)
                ? q.ToString(CultureInfo.InvariantCulture) : null,
        SetRegistryValueOp { Kind: RegistryValueType.String } s => s.Value,
        SetActivePowerPlanOp p => p.PlanGuid.ToString("D", CultureInfo.InvariantCulture),
        SetProcessorBoostModeOp b => b.AcValue.ToString(CultureInfo.InvariantCulture),
        SetCpuPackagePowerLimitOp c => c.Valeur.ToString(CultureInfo.InvariantCulture),
        SetWirelessPowerSavingOp w => w.AcValue.ToString(CultureInfo.InvariantCulture),
        RunServiceControlOp { Action: ServiceAction.Start } => "running",
        RunServiceControlOp { Action: ServiceAction.Stop } => "stopped",
        ApplyNvApiDrsSettingOp a => a.Value.ToString(CultureInfo.InvariantCulture),
        ApplyNvApiPowerLimitOp n => n.PowerLimitPercent.ToString(CultureInfo.InvariantCulture),
        ApplyAdlxTuningOp a => a.IntValue.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string Decrire(PrivilegedOp op) => op switch
    {
        SetRegistryValueOp s => Docteur.T("RD.Decrire.Valeur", s.Value),
        DeleteRegistryValueOp => Docteur.T("RD.Decrire.Supprimee"),
        SetActivePowerPlanOp p => Docteur.T("RD.Decrire.Plan", p.PlanGuid),
        SetProcessorBoostModeOp b => Docteur.T("RD.Decrire.Boost", b.AcValue),
        SetCpuPackagePowerLimitOp c => Docteur.T("RD.Decrire.Registre610", $"0x{c.Valeur:X16}"),
        SetWirelessPowerSavingOp w => Docteur.T("RD.Decrire.Wifi", w.AcValue),
        RunServiceControlOp r => $"service {r.Action}",
        ApplyNvApiDrsSettingOp a => Docteur.T("RD.Decrire.Valeur", a.Value),
        ApplyNvApiPowerLimitOp n => Docteur.T("RD.Decrire.Limite", n.PowerLimitPercent),
        ApplyAdlxTuningOp a => $"{a.Kind} {a.IntValue}",
        _ => op.GetType().Name,
    };
}
