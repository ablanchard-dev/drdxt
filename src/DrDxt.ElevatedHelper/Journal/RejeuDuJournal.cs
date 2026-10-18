using System.Globalization;
using DrDxt.Core;
using DrDxt.Reconciler.Desinstallation;

namespace DrDxt.ElevatedHelper.Journal;

/// <summary>
/// Le rejeu du journal d'écriture anticipée. Une entrée restée au journal est une écriture dont l'App n'a pas noté l'origine ;
/// si son demandeur est mort, plus personne ne la rendra : le rejeu rend l'image d'avant, avec les règles de la
/// désinstallation (RenduALaDesinstallation) : rendue seulement si la valeur posée est encore là, respectée si quelqu'un l'a
/// changée depuis. Plusieurs poses d'une même feuille n'en font qu'une : la dernière valeur posée, à comparer à la machine, et
/// la plus ancienne image, la vraie origine (jugées une à une, la première se lirait « changée » par la seconde). Un demandeur
/// vivant n'est pas touché : il notera l'origine, ou rendra la pose lui-même.
/// Une écriture dans HKCU ne se rejoue que dans la ruche de son demandeur : le processus qui rejoue doit avoir son compte.
/// Vu de SYSTEM, ou d'un helper élevé avec le compte d'un autre administrateur, HKCU est une autre ruche.
/// Une entrée jugée sort du journal ; une entrée en suspens (écriture refusée, cible illisible, ruche d'un autre compte) y
/// reste pour le rejeu suivant. Un journal illisible ne rend rien.
/// Limite : quand un demandeur vivant et un mort ont posé la même feuille, l'entrée du mort se lit « respectée » (la valeur
/// en place est celle du vivant) et sort ; l'image d'avant du vivant est la pose du mort. Si le vivant meurt à son tour sans
/// acquitter, le rejeu rend cette pose, pas l'origine du joueur. Il faut deux App en même temps ; aucune valeur étrangère à
/// DrDXT n'est écrite, l'origine du joueur est perdue.
/// </summary>
public sealed class RejeuDuJournal(
    JournalDEcritureAnticipee journal,
    ILectureDesCibles lecture,
    IElevatedClient ecriture,
    Func<AppelantDuJournal, bool> vivant,
    Func<string?> sidCourant)
{
    /// <summary>Le nom des lignes du rapport : une écriture du journal n'a pas de levier, sa cible la nomme.</summary>
    public const string Source = "journal";

    /// <summary>Le demandeur vit-il encore ? Le même processus : même PID, même heure de création, même compte.</summary>
    public static bool ProcessusVivant(AppelantDuJournal a)
        => IdentiteDesAppelants.De(new Ipc.CallerInfo(a.Pid)) == a;

    /// <summary>Le compte du processus qui rejoue.</summary>
    public static string? CompteCourant()
    {
        using var moi = System.Security.Principal.WindowsIdentity.GetCurrent();
        return moi.User?.Value;
    }

    public async Task<Result<RapportDuRendu>> ExecuterAsync(CancellationToken ct)
    {
        var lu = journal.EnAttente();
        if (!lu.IsOk) return Result<RapportDuRendu>.Fail(lu.Error!.Value.Code, lu.Error.Value.Message);

        var lignes = new List<LigneDuRendu>();
        var aRendre = new List<(IReadOnlyList<EntreeDuJournal> Entrees, AppliedTweak Origine)>();
        foreach (var feuille in lu.Value!.Where(e => !vivant(e.Appelant)).OrderBy(e => e.Le).GroupBy(e => Cible(e.Op, e.Appelant)))
        {
            var entrees = feuille.ToList();
            var (premiere, derniere) = (entrees[0], entrees[^1]);
            if (derniere.Op is SetRegistryValueOp { Hive: RegistryHiveKind.CurrentUser } && sidCourant() != derniere.Appelant.Sid)
            {
                lignes.Add(new LigneDuRendu(Source, feuille.Key, IssueDuRendu.Illisible, Docteur.T("JEA.SessionUtilisateur"), EnSuspens: true));
                continue;
            }
            if (OrigineDe(derniere.Op, premiere.ImageAvant) is not { } origine
                || PrivilegedOpCodec.Serialize(origine) is not { IsOk: true, Value: { } origineJson }
                || PrivilegedOpCodec.Serialize(derniere.Op) is not { IsOk: true, Value: { } poseeJson })
            {
                lignes.Add(new LigneDuRendu(Source, feuille.Key, IssueDuRendu.ValeurPoseeInconnue,
                    Docteur.T("JEA.OrigineNonRendable", premiere.ImageAvant)));
                Retirer(entrees);
                continue;
            }
            aRendre.Add((entrees, new AppliedTweak(premiere.Id.ToString("N"), poseeJson, origineJson, premiere.Le)));
        }
        if (aRendre.Count == 0) return Result<RapportDuRendu>.Ok(new RapportDuRendu(null, lignes));

        var rendu = await new RenduALaDesinstallation(new Manifeste(aRendre.Select(a => a.Origine).ToList()), lecture, ecriture)
            .ExecuterAsync(ct).ConfigureAwait(false);
        foreach (var (entrees, origine) in aRendre)
        {
            var siennes = rendu.Lignes.Where(l => l.TweakId == origine.TweakId).ToList();
            if (!siennes.Any(l => l.OrigineEnSuspens)) Retirer(entrees);
            lignes.AddRange(siennes.Select(l => l with { TweakId = Source }));
        }
        return Result<RapportDuRendu>.Ok(new RapportDuRendu(rendu.ManifesteNonLu, lignes));
    }

    private void Retirer(IEnumerable<EntreeDuJournal> entrees)
    {
        // Une entrée qui ne sort pas (journal occupé) se rejoue : la valeur d'origine en place, elle sera respectée.
        foreach (var e in entrees) journal.Retirer(e.Id);
    }

    /// <summary>L'op qui remet l'image d'avant, sous la forme de l'op posée ; null quand l'image ne se rend pas (absente hors
    /// registre, ou illisible pour cette op).</summary>
    internal static PrivilegedOp? OrigineDe(PrivilegedOp posee, string? avant) => posee switch
    {
        SetRegistryValueOp s => avant is null ? new DeleteRegistryValueOp(s.Hive, s.Path, s.Name) : s with { Value = avant },
        SetActivePowerPlanOp when Guid.TryParse(avant, out var plan) => new SetActivePowerPlanOp(plan),
        SetProcessorBoostModeOp b when Naturel(avant) is { } v => b with { AcValue = v },
        SetWirelessPowerSavingOp w when Naturel(avant) is { } v => w with { AcValue = v },
        // L'inverse est la même op, les deux registres échangés.
        SetCpuPackagePowerLimitOp c when ulong.TryParse(avant, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            => new SetCpuPackagePowerLimitOp(v, c.Valeur),
        RunServiceControlOp r when avant == "running" => r with { Action = ServiceAction.Start },
        RunServiceControlOp r when avant == "stopped" => r with { Action = ServiceAction.Stop },
        ApplyNvApiDrsSettingOp d when Naturel(avant) is { } v => d with { Value = v },
        ApplyNvApiPowerLimitOp n when Entier(avant) is { } v => n with { PowerLimitPercent = v },
        ApplyAdlxTuningOp a when Entier(avant) is { } v => a with { IntValue = v },
        _ => null,
    };

    private static uint? Naturel(string? s) => uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static int? Entier(string? s) => int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>La feuille qu'une op écrit : deux poses de la même feuille se fondent. HKCU est la ruche de chaque compte.</summary>
    private static string Cible(PrivilegedOp op, AppelantDuJournal qui) => op switch
    {
        SetRegistryValueOp { Hive: RegistryHiveKind.CurrentUser } s
            => $"{qui.Sid}\\{s.Path.Trim().Trim('\\')}\\{s.Name}".ToUpperInvariant(),
        SetRegistryValueOp s => $"{s.Hive}\\{s.Path.Trim().Trim('\\')}\\{s.Name}".ToUpperInvariant(),
        SetProcessorBoostModeOp b => $"boost {b.PlanGuid:D}",
        SetWirelessPowerSavingOp w => $"wifi {w.PlanGuid:D}",
        RunServiceControlOp r => $"service {r.ServiceName}".ToUpperInvariant(),
        ApplyNvApiDrsSettingOp d => $"drs {d.ProfileName.ToUpperInvariant()} 0x{d.SettingId:X8}",
        ApplyNvApiPowerLimitOp n => $"nvlimit {n.GpuIndex}",
        ApplyAdlxTuningOp a => $"adlx {a.GpuIndex} {a.Kind}",
        _ => op.GetType().Name,
    };

    /// <summary>Les entrées à rendre, sous la forme que la désinstallation lit.</summary>
    private sealed class Manifeste(IReadOnlyList<AppliedTweak> origines) : IDesiredStateStore
    {
        public Task<Result<DesiredState>> LoadAsync()
            => Task.FromResult(Result<DesiredState>.Ok(new DesiredState(Array.Empty<AppliedTweak>(), origines)));

        public Task<Result> CommitAsync(DesiredState state)
            => Task.FromResult(Result.Fail(ErrorCodes.NotAllowlisted, "The write-ahead journal replay does not write a manifest."));
    }
}
