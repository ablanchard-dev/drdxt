using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks;

/// <summary>Ce qu'une relecture de cible autorise a affirmer.</summary>
public enum EtatApplique
{
    /// <summary>La valeur lue est celle que le levier a demandee : le seul etat qui autorise a mesurer son gain.</summary>
    Applied,

    /// <summary>La valeur lue est celle d'avant : l'ecriture n'a pas pris (clamp du pilote, feuille protegee, echec
    /// muet). Mesurer ici attribuerait du bruit a un changement inexistant.</summary>
    NotApplied,

    /// <summary>La valeur lue n'est ni la cible ni l'originale. Quelqu'un d'autre ecrit sur cette
    /// feuille : un autre outil, une strategie de groupe, le pilote lui-meme.</summary>
    Drifted,

    /// <summary>Cette op ne sait pas se relire. « Je ne sais pas lire » n'est jamais « c'est applique ».</summary>
    Unavailable,

    /// <summary>La lecture a echoue, ou le snapshot ne porte pas la valeur demandee. Un trou, pas un etat.</summary>
    Unknown,
}

/// <summary>La preuve (ou son absence) qu'un levier est en place, avec de quoi la contester.</summary>
/// <param name="TweakId">Le levier.</param>
/// <param name="Verdict">Ce qui peut etre affirme.</param>
/// <param name="Cible">La feuille relue, « ruche\chemin\nom », ou <c>null</c>.</param>
/// <param name="OriginalState">Ce qu'il y avait avant, <c>null</c> = la valeur etait absente.</param>
/// <param name="RequestedState">Ce que le levier a demande, <c>null</c> = non porte par le snapshot.</param>
/// <param name="ObservedState">Ce qui est lu maintenant, <c>null</c> = la valeur est absente.</param>
public sealed record AppliedStateEvidence(
    string TweakId,
    EtatApplique Verdict,
    string? Cible,
    string? OriginalState,
    string? RequestedState,
    string? ObservedState)
{
    /// <summary>Le seul cas qui autorise un banc ; tout le reste, « je ne sais pas » compris, l'interdit.</summary>
    public bool AutoriseLaMesure => Verdict == EtatApplique.Applied;
}

/// <summary>
/// Prouver qu'un levier est en place. « Different d'avant » (<c>DifferentDeAvant</c>, `TweakReadback`) n'est pas « egal
/// a la cible » : un autre outil, une strategie de groupe ou un clamp du pilote donnent le meme verdict. Le snapshot
/// porte l'original et la valeur posee (`AppliedValueJson`) sur la meme feuille : la comparaison a trois termes, sans
/// rien demander aux leviers. Seul <see cref="EtatApplique.Applied"/> autorise la mesure ; `Unavailable` et `Unknown`
/// interdisent de conclure dans les deux sens, sans condamner le levier. La courbe de ventilation et les decalages de
/// frequence NVIDIA n'ont pas de valeur cible : `Unknown` ici, ce qui ne veut pas dire « invérifiable ».
/// </summary>
public static class AppliedStateVerifier
{
    /// <summary>Ce qui relit la carte et le processeur : la limite de puissance (NVAPI, ADLX), les réglages du pilote AMD, la
    /// fréquence maximale AMD, le champ PL1 du registre 0x610. Un lecteur absent rend <c>Unavailable</c>.</summary>
    public sealed record LecteursCarte(INvApiGpuTuning? Nvidia = null, IAdlxGpuTuning? Amd = null, IAdlx? PiloteAmd = null,
        ICpuPowerLimitReader? Cpu = null);

    /// <summary>Le champ PL1 du registre 0x610 (bits 14:0), le seul qu'écrit le levier.</summary>
    private const ulong ChampPl1 = 0x7FFF;

    /// <summary>La valeur qu'une op de la carte ou du processeur pose, dans l'unité que son lecteur rend (pourcentage
    /// entier, MHz entiers, état du pilote, champ PL1 brut) ; null pour toute autre op.</summary>
    public static long? ValeurDeCarte(PrivilegedOp op) => op switch
    {
        ApplyNvApiPowerLimitOp n => n.PowerLimitPercent,
        ApplyAdlxTuningOp { Kind: AdlxTuningKind.PowerLimit or AdlxTuningKind.AntiLag or AdlxTuningKind.FrameRateTarget
            or AdlxTuningKind.VerticalRefresh or AdlxTuningKind.Undervolt } a => a.IntValue,
        SetCpuPackagePowerLimitOp c => (long)(c.Valeur & ChampPl1),
        _ => null,
    };

    /// <summary>Ce qui relit la valeur d'une op de <see cref="ValeurDeCarte"/>, dans la même unité ; null sans lecteur.</summary>
    public static Func<Result<long>>? LecteurDeCarte(PrivilegedOp op, LecteursCarte? carte)
    {
        static Result<long> Long(Result<int> r) => r.IsOk ? Result<long>.Ok(r.Value) : Result<long>.Fail(r.Error!.Value.Code, r.Error.Value.Message);
        return op switch
        {
            ApplyNvApiPowerLimitOp n when carte?.Nvidia is { } nv => () => Long(nv.GetPowerLimitPct(n.GpuIndex)),
            ApplyAdlxTuningOp { Kind: AdlxTuningKind.PowerLimit } a when carte?.Amd is { } amd => () => Long(amd.GetPowerLimitPct(a.GpuIndex)),
            ApplyAdlxTuningOp { Kind: AdlxTuningKind.Undervolt } a when carte?.Amd is { } amd => () => Long(amd.GetGfxMaxFreqMhz(a.GpuIndex)),
            ApplyAdlxTuningOp { Kind: AdlxTuningKind.AntiLag } when carte?.PiloteAmd is { } p => () => Long(p.GetAntiLagState()),
            ApplyAdlxTuningOp { Kind: AdlxTuningKind.FrameRateTarget } when carte?.PiloteAmd is { } p => () => Long(p.GetFrameRateTarget()),
            ApplyAdlxTuningOp { Kind: AdlxTuningKind.VerticalRefresh } when carte?.PiloteAmd is { } p => () => Long(p.GetVsyncMode()),
            SetCpuPackagePowerLimitOp when carte?.Cpu is { } cpu => () => cpu.LireRegistres() is { IsOk: true } l
                ? Result<long>.Ok((long)(l.Value!.Registres.PkgPowerLimit & ChampPl1))
                : Result<long>.Fail(ErrorCodes.TweakSdkUnavailable, "PL1"),
            _ => null,
        };
    }

    /// <summary>Le nom d'une op de la carte ou du processeur, pour dire ce qui a été relu.</summary>
    public static string NomDeCarte(PrivilegedOp op) => op switch
    {
        ApplyNvApiPowerLimitOp or ApplyAdlxTuningOp { Kind: AdlxTuningKind.PowerLimit } => Docteur.T("VM.LimitePuissance"),
        ApplyAdlxTuningOp { Kind: AdlxTuningKind.Undervolt } => Docteur.T("VM.FrequenceMax"),
        ApplyAdlxTuningOp { Kind: AdlxTuningKind.FrameRateTarget } => Docteur.T("VM.Frtc"),
        ApplyAdlxTuningOp { Kind: AdlxTuningKind.AntiLag } => "Anti-Lag (0/1)",
        ApplyAdlxTuningOp a => a.Kind.ToString(),
        _ => "PL1",
    };

    // Comparée dans l'unité que le lecteur rend, sans conversion : un arrondi ne fait jamais « autre valeur ».
    private static AppliedStateEvidence VerifierCarte(string tweakId, PrivilegedOp demande, PrivilegedOp? origine, LecteursCarte? carte)
    {
        var nom = NomDeCarte(demande);
        var vise = ValeurDeCarte(demande)!.Value;
        long? avant = origine is not null && MemeFeuille(origine, demande) ? ValeurDeCarte(origine) : null;
        var texteAvant = avant?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var texteVise = vise.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (LecteurDeCarte(demande, carte) is not { } lire)
            return new AppliedStateEvidence(tweakId, EtatApplique.Unavailable, nom, texteAvant, texteVise, null);
        var lu = lire();
        if (!lu.IsOk)
            return new AppliedStateEvidence(tweakId, EtatApplique.Unknown, nom, texteAvant, texteVise, null);
        var verdict = lu.Value == vise ? EtatApplique.Applied
            : lu.Value == avant ? EtatApplique.NotApplied
            : EtatApplique.Drifted;
        return new AppliedStateEvidence(tweakId, verdict, nom, texteAvant, texteVise,
            lu.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Les quatre reglages du pilote NVIDIA que ce depot sait relire, par identifiant DRS : le NVAPI du depot expose quatre
    /// lectures nommees, verifiees contre `NvApiDriverSettings.h`, pas de lecture par identifiant. Un identifiant non
    /// couvert rend `Unavailable`, pas une valeur devinee.
    /// </summary>
    private static readonly IReadOnlyDictionary<uint, Func<INvApi, Result<uint>>> LecturesDrs =
        new Dictionary<uint, Func<INvApi, Result<uint>>>
        {
            [0x1057EB71] = api => api.GetPreferredPstate(),      // PREFERRED_PSTATE_ID
            [0x007ba09e] = api => api.GetMaxPreRenderedFrames(), // PRERENDERLIMIT_ID
            [0x00A879CF] = api => api.GetVsyncMode(),            // VSYNCMODE_ID
            [0x10835002] = api => api.GetFrameRateLimit(),       // FRL_FPS_ID
        };

    /// <summary>
    /// Un levier composite (Vsync + plafond ; X3D = service + deux feuilles) se prouve feuille par feuille, face a
    /// l'inverse de la meme feuille (jamais par position : `VrrVsyncCap` range ses inverses a l'envers). Applied seulement
    /// si toutes le sont ; une feuille illisible le rend Unknown ou Unavailable.
    /// </summary>
    private static AppliedStateEvidence VerifierComposite(TweakSnapshot snapshot, IReadOnlyList<PrivilegedOp> posees,
        IRegistryReader lecteur, INvApi? nvApi, IPowerPlanReader? alimentation, IServiceStateReader? services, LecteursCarte? carte)
    {
        var inverses = CompositeSnapshotCodec.Decode(snapshot.OriginalValueJson) is { IsOk: true, Value: { } lus }
            ? lus
            : Array.Empty<PrivilegedOp>();

        var feuilles = posees.Select(posee =>
        {
            var inverse = inverses.FirstOrDefault(inv => MemeFeuille(inv, posee));
            var origine = inverse is null ? "" : PrivilegedOpCodec.Serialize(inverse).Value ?? "";
            var cible = PrivilegedOpCodec.Serialize(posee).Value ?? "";
            return Verifier(new TweakSnapshot(snapshot.TweakId, origine, cible), lecteur, nvApi, alimentation, services, carte);
        }).ToList();

        return new AppliedStateEvidence(snapshot.TweakId, Combiner(feuilles.Select(f => f.Verdict).ToList()),
            string.Join(" + ", feuilles.Select(f => f.Cible ?? "?")),
            Joindre(feuilles.Select(f => f.OriginalState)),
            Joindre(feuilles.Select(f => f.RequestedState)),
            Joindre(feuilles.Select(f => f.ObservedState)));
    }

    /// <summary>Un trou (Unknown, puis Unavailable) l'emporte toujours : il interdit d'affirmer.</summary>
    private static EtatApplique Combiner(IReadOnlyList<EtatApplique> verdicts)
    {
        if (verdicts.Contains(EtatApplique.Unknown)) return EtatApplique.Unknown;
        if (verdicts.Contains(EtatApplique.Unavailable)) return EtatApplique.Unavailable;
        if (verdicts.All(v => v == EtatApplique.Applied)) return EtatApplique.Applied;
        if (verdicts.All(v => v == EtatApplique.NotApplied)) return EtatApplique.NotApplied;
        return EtatApplique.Drifted;
    }

    private static string Joindre(IEnumerable<string?> parts) => string.Join(" + ", parts.Select(p => p ?? "absent"));

    /// <summary>Deux ops visent-elles la meme feuille ? L'inverse d'une ecriture peut etre une suppression.</summary>
    private static bool MemeFeuille(PrivilegedOp inverse, PrivilegedOp posee) => (inverse, posee) switch
    {
        (SetRegistryValueOp i, SetRegistryValueOp p) => MemeCle(i.Hive, i.Path, i.Name, p),
        (DeleteRegistryValueOp i, SetRegistryValueOp p) => MemeCle(i.Hive, i.Path, i.Name, p),
        // Une valeur à rendre absente (origine d'un exercice profond) face à la valeur écrite de la même feuille.
        (SetRegistryValueOp i, DeleteRegistryValueOp p) => MemeCle(p.Hive, p.Path, p.Name, i),
        (ApplyNvApiDrsSettingOp i, ApplyNvApiDrsSettingOp p) =>
            i.SettingId == p.SettingId && string.Equals(i.ProfileName, p.ProfileName, StringComparison.OrdinalIgnoreCase),
        (DeleteNvApiDrsSettingOp i, ApplyNvApiDrsSettingOp p) =>
            i.SettingId == p.SettingId && string.Equals(i.ProfileName, p.ProfileName, StringComparison.OrdinalIgnoreCase),
        (SetActivePowerPlanOp, SetActivePowerPlanOp) => true,
        (RunServiceControlOp i, RunServiceControlOp p) =>
            string.Equals(i.ServiceName, p.ServiceName, StringComparison.OrdinalIgnoreCase),
        (ApplyNvApiPowerLimitOp i, ApplyNvApiPowerLimitOp p) => i.GpuIndex == p.GpuIndex,
        (ApplyAdlxTuningOp i, ApplyAdlxTuningOp p) => i.GpuIndex == p.GpuIndex && i.Kind == p.Kind,
        (SetCpuPackagePowerLimitOp, SetCpuPackagePowerLimitOp) => true,
        _ => false,
    };

    private static bool MemeCle(RegistryHiveKind ruche, string chemin, string nom, SetRegistryValueOp p) =>
        ruche == p.Hive
        && string.Equals(chemin, p.Path, StringComparison.OrdinalIgnoreCase)
        && string.Equals(nom, p.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Compare l'etat lu a la cible et a l'original portes par le snapshot.</summary>
    /// <param name="lecteur">Le seam de lecture registre. Non elevee : lire ne demande pas l'UAC.</param>
    /// <param name="nvApi">Le pilote NVIDIA ; sans lui, un reglage DRS rend <c>Unavailable</c>.</param>
    /// <param name="alimentation">Le lecteur de plan d'alimentation, meme role.</param>
    /// <param name="services">Le lecteur d'etat des services, meme role.</param>
    /// <param name="carte">Les lecteurs de la carte et du processeur, meme role.</param>
    public static AppliedStateEvidence Verifier(TweakSnapshot? snapshot, IRegistryReader lecteur,
        INvApi? nvApi = null, IPowerPlanReader? alimentation = null, IServiceStateReader? services = null,
        LecteursCarte? carte = null)
    {
        if (snapshot is null)
            return new AppliedStateEvidence("?", EtatApplique.Unknown, null, null, null, null);

        // Sans valeur demandee, rien a comparer : pas d'« applique » par indulgence.
        if (string.IsNullOrWhiteSpace(snapshot.AppliedValueJson))
            return new AppliedStateEvidence(snapshot.TweakId, EtatApplique.Unknown, null, null, null, null);

        if (CompositeSnapshotCodec.Decode(snapshot.AppliedValueJson) is { IsOk: true, Value: { Count: > 0 } posees })
            return VerifierComposite(snapshot, posees, lecteur, nvApi, alimentation, services, carte);

        var cible = Decoder(snapshot.AppliedValueJson);
        var origine = Decoder(snapshot.OriginalValueJson);
        if (cible is null)
            return new AppliedStateEvidence(snapshot.TweakId, EtatApplique.Unknown, null, null, null, null);

        // Un reglage du pilote NVIDIA se relit par le pilote, pas par le registre.
        if (cible is ApplyNvApiDrsSettingOp drs)
            return VerifierDrs(snapshot.TweakId, drs, origine, nvApi);

        // Le plan actif se relit par `powrprof`, la meme API que le levier pour capturer l'original.
        if (cible is SetActivePowerPlanOp plan)
            return VerifierPlan(snapshot.TweakId, plan, origine as SetActivePowerPlanOp, alimentation);

        // Une valeur a l'interieur d'un plan, relue par la meme API que le levier lit avant d'ecrire.
        if (cible is SetProcessorBoostModeOp boost)
            return VerifierBoost(snapshot.TweakId, boost, origine as SetProcessorBoostModeOp, alimentation);
        if (cible is SetWirelessPowerSavingOp sansFil)
            return VerifierSansFil(snapshot.TweakId, sansFil, origine as SetWirelessPowerSavingOp, alimentation);

        // La carte et le processeur, relus par ce qui les lit (NVAPI, ADLX, le registre 0x610).
        if (ValeurDeCarte(cible) is not null)
            return VerifierCarte(snapshot.TweakId, cible, origine, carte);

        // Un service demarre ou arrete, relu par son etat.
        if (cible is RunServiceControlOp { Action: ServiceAction.Start or ServiceAction.Stop } service)
            return VerifierService(snapshot.TweakId, service, origine as RunServiceControlOp, services);

        // Une valeur à rendre absente (l'origine « absente » d'un exercice profond) : absente = la cible est là.
        if (cible is DeleteRegistryValueOp supprimee)
        {
            var feuilleSupprimee = $"{supprimee.Hive}\\{supprimee.Path}\\{supprimee.Name}";
            var setAvant = origine as SetRegistryValueOp;
            // Lue selon le type que l'autre op du snapshot donne à la feuille (une chaîne ne se lit pas en DWORD).
            var luSupprimee = setAvant?.Kind switch
            {
                RegistryValueType.String => lecteur.ReadString(supprimee.Hive, supprimee.Path, supprimee.Name),
                _ => lecteur.ReadDword(supprimee.Hive, supprimee.Path, supprimee.Name) is var d && d.IsOk
                    ? Result<string?>.Ok(d.Value?.ToString()) : Result<string?>.Fail(ErrorCodes.TweakSdkUnavailable, "non lu"),
            };
            if (!luSupprimee.IsOk)
                return new AppliedStateEvidence(snapshot.TweakId, EtatApplique.Unknown, feuilleSupprimee, setAvant?.Value, null, null);
            var verdictSupprimee = luSupprimee.Value is null ? EtatApplique.Applied
                : string.Equals(luSupprimee.Value, setAvant?.Value, StringComparison.Ordinal) ? EtatApplique.NotApplied
                : EtatApplique.Drifted;
            return new AppliedStateEvidence(snapshot.TweakId, verdictSupprimee, feuilleSupprimee, setAvant?.Value, null, luSupprimee.Value);
        }

        // Une chaîne ou une chaîne multiple, relue comme le scan la relit (TweakReadback), comparée telle quelle.
        if (cible is SetRegistryValueOp { Kind: RegistryValueType.String or RegistryValueType.MultiString } texte)
            return VerifierTexte(snapshot.TweakId, texte, origine, lecteur);

        // Sinon, seules les ops de registre DWORD savent se relire.
        if (cible is not SetRegistryValueOp demande || demande.Kind != RegistryValueType.DWord)
            return new AppliedStateEvidence(snapshot.TweakId, EtatApplique.Unavailable, null, null, null, null);

        var feuille = $"{demande.Hive}\\{demande.Path}\\{demande.Name}";
        var avant = origine switch
        {
            SetRegistryValueOp set => set.Value,
            DeleteRegistryValueOp => null,          // la valeur n'existait pas avant
            _ => null,
        };

        var lu = lecteur.ReadDword(demande.Hive, demande.Path, demande.Name);
        if (!lu.IsOk)
            return new AppliedStateEvidence(snapshot.TweakId, EtatApplique.Unknown,
                feuille, avant, demande.Value, null);

        var observe = lu.Value?.ToString();

        // Sur la valeur, pas la chaine : « 2 » et « 02 » sont la meme chose pour le registre.
        if (MemeValeur(demande.Value, lu.Value))
            return new AppliedStateEvidence(snapshot.TweakId, EtatApplique.Applied,
                feuille, avant, demande.Value, observe);

        if (MemeValeur(avant, lu.Value))
            return new AppliedStateEvidence(snapshot.TweakId, EtatApplique.NotApplied,
                feuille, avant, demande.Value, observe);

        return new AppliedStateEvidence(snapshot.TweakId, EtatApplique.Drifted,
            feuille, avant, demande.Value, observe);
    }

    private static AppliedStateEvidence VerifierTexte(string tweakId, SetRegistryValueOp demande, PrivilegedOp? origine,
        IRegistryReader lecteur)
    {
        var feuille = $"{demande.Hive}\\{demande.Path}\\{demande.Name}";
        var avant = origine is SetRegistryValueOp set ? set.Value : null;   // une suppression : absente avant
        var lu = demande.Kind == RegistryValueType.String
            ? lecteur.ReadString(demande.Hive, demande.Path, demande.Name)
            : lecteur.ReadMultiString(demande.Hive, demande.Path, demande.Name);
        if (!lu.IsOk)
            return new AppliedStateEvidence(tweakId, EtatApplique.Unknown, feuille, avant, demande.Value, null);
        var verdict = string.Equals(lu.Value, demande.Value, StringComparison.Ordinal) ? EtatApplique.Applied
            : string.Equals(lu.Value, avant, StringComparison.Ordinal) ? EtatApplique.NotApplied
            : EtatApplique.Drifted;
        return new AppliedStateEvidence(tweakId, verdict, feuille, avant, demande.Value, lu.Value);
    }

    /// <summary>La meme comparaison a trois termes, cote pilote NVIDIA.</summary>
    private static AppliedStateEvidence VerifierDrs(string tweakId, ApplyNvApiDrsSettingOp demande,
        PrivilegedOp? inverse, INvApi? nvApi)
    {
        var feuille = $"NVAPI DRS 0x{demande.SettingId:X8} ({demande.ProfileName})";
        // L'inverse d'une ecriture au profil d'un jeu peut etre une suppression qui porte la valeur d'avant : sans ce cas,
        // un reglage reste a l'origine serait lu `Drifted` au lieu de `NotApplied`.
        uint? origine = inverse switch
        {
            ApplyNvApiDrsSettingOp a => a.Value,
            DeleteNvApiDrsSettingOp d => d.ValeurAttendue,
            _ => null,
        };
        var avant = origine?.ToString();
        var vise = demande.Value.ToString();

        if (nvApi is null || !nvApi.IsAvailable || !LecturesDrs.TryGetValue(demande.SettingId, out var lire))
            return new AppliedStateEvidence(tweakId, EtatApplique.Unavailable, feuille, avant, vise, null);

        // Relire au profil que l'ecriture a vise (ProfileName = chemin complet pour un jeu) : lu au profil de base, un
        // reglage ecrit dans le vide passerait `Applied`, un reglage bien pris au jeu serait accuse d'un ecart.
        var lu = Path.IsPathFullyQualified(demande.ProfileName)
            ? nvApi.GetDrsForApplication(demande.SettingId, demande.ProfileName) is { IsOk: true } auJeu
                ? Result<uint>.Ok(auJeu.Value.Value)
                : Result<uint>.Fail(ErrorCodes.TweakSdkUnavailable, Docteur.T("App.NonLuProfilJeu"))
            : lire(nvApi);
        if (!lu.IsOk)
            return new AppliedStateEvidence(tweakId, EtatApplique.Unknown, feuille, avant, vise, null);

        var observe = lu.Value.ToString();
        if (lu.Value == demande.Value)
            return new AppliedStateEvidence(tweakId, EtatApplique.Applied, feuille, avant, vise, observe);
        if (origine is not null && lu.Value == origine.Value)
            return new AppliedStateEvidence(tweakId, EtatApplique.NotApplied, feuille, avant, vise, observe);

        return new AppliedStateEvidence(tweakId, EtatApplique.Drifted, feuille, avant, vise, observe);
    }

    /// <summary>La comparaison a trois termes pour le plan d'alimentation actif : quel plan est actif, rien d'autre (ni
    /// ses sous-parametres). C'est la cible que ce levier demande.</summary>
    private static AppliedStateEvidence VerifierPlan(string tweakId, SetActivePowerPlanOp demande,
        SetActivePowerPlanOp? origine, IPowerPlanReader? alimentation)
    {
        const string feuille = "plan d'alimentation actif";
        var avant = origine?.PlanGuid.ToString();
        var vise = demande.PlanGuid.ToString();

        if (alimentation is null)
            return new AppliedStateEvidence(tweakId, EtatApplique.Unavailable, feuille, avant, vise, null);

        var lu = alimentation.GetActivePowerPlan();
        if (!lu.IsOk)
            return new AppliedStateEvidence(tweakId, EtatApplique.Unknown, feuille, avant, vise, null);

        var observe = lu.Value.ToString();
        if (lu.Value == demande.PlanGuid)
            return new AppliedStateEvidence(tweakId, EtatApplique.Applied, feuille, avant, vise, observe);
        if (origine is not null && lu.Value == origine.PlanGuid)
            return new AppliedStateEvidence(tweakId, EtatApplique.NotApplied, feuille, avant, vise, observe);

        return new AppliedStateEvidence(tweakId, EtatApplique.Drifted, feuille, avant, vise, observe);
    }

    /// <summary>La comparaison a trois termes pour le mode de boost secteur d'un plan. Elle prouve la valeur
    /// enregistree dans le plan, pas la frequence atteinte : celle-ci, c'est la mesure en jeu qui la juge.</summary>
    private static AppliedStateEvidence VerifierBoost(string tweakId, SetProcessorBoostModeOp demande,
        SetProcessorBoostModeOp? origine, IPowerPlanReader? alimentation)
    {
        var feuille = $"mode de boost secteur (plan {demande.PlanGuid:D})";
        var avant = origine?.AcValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var vise = demande.AcValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (alimentation is null)
            return new AppliedStateEvidence(tweakId, EtatApplique.Unavailable, feuille, avant, vise, null);
        var lu = alimentation.LireModeDeBoost(demande.PlanGuid);
        if (!lu.IsOk || lu.Value is null)
            return new AppliedStateEvidence(tweakId, EtatApplique.Unknown, feuille, avant, vise, null);

        var observe = lu.Value.Valeur.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var verdict = lu.Value.Valeur == demande.AcValue ? EtatApplique.Applied
            : origine is not null && lu.Value.Valeur == origine.AcValue ? EtatApplique.NotApplied
            : EtatApplique.Drifted;
        return new AppliedStateEvidence(tweakId, verdict, feuille, avant, vise, observe);
    }

    private static AppliedStateEvidence VerifierSansFil(string tweakId, SetWirelessPowerSavingOp demande,
        SetWirelessPowerSavingOp? origine, IPowerPlanReader? alimentation)
    {
        var feuille = $"économie d'énergie sans fil secteur (plan {demande.PlanGuid:D})";
        var avant = origine?.AcValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var vise = demande.AcValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (alimentation is null)
            return new AppliedStateEvidence(tweakId, EtatApplique.Unavailable, feuille, avant, vise, null);
        var lu = alimentation.LireEconomieSansFil(demande.PlanGuid);
        if (!lu.IsOk || lu.Value is null)
            return new AppliedStateEvidence(tweakId, EtatApplique.Unknown, feuille, avant, vise, null);

        var observe = lu.Value.Valeur.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var verdict = lu.Value.Valeur == demande.AcValue ? EtatApplique.Applied
            : origine is not null && lu.Value.Valeur == origine.AcValue ? EtatApplique.NotApplied
            : EtatApplique.Drifted;
        return new AppliedStateEvidence(tweakId, verdict, feuille, avant, vise, observe);
    }

    private static AppliedStateEvidence VerifierService(string tweakId, RunServiceControlOp demande, RunServiceControlOp? origine,
        IServiceStateReader? services)
    {
        static ServiceState Attendu(ServiceAction a) => a == ServiceAction.Start ? ServiceState.Running : ServiceState.Stopped;
        var feuille = $"service {demande.ServiceName}";
        var avant = origine is null ? null : Attendu(origine.Action).ToString();
        var vise = Attendu(demande.Action).ToString();

        if (services is null)
            return new AppliedStateEvidence(tweakId, EtatApplique.Unavailable, feuille, avant, vise, null);
        var lu = services.GetServiceState(demande.ServiceName);
        if (!lu.IsOk)
            return new AppliedStateEvidence(tweakId, EtatApplique.Unknown, feuille, avant, vise, null);

        var verdict = lu.Value == Attendu(demande.Action) ? EtatApplique.Applied
            : origine is not null && lu.Value == Attendu(origine.Action) ? EtatApplique.NotApplied
            : EtatApplique.Drifted;
        return new AppliedStateEvidence(tweakId, verdict, feuille, avant, vise, lu.Value.ToString());
    }

    private static PrivilegedOp? Decoder(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var decode = PrivilegedOpCodec.Deserialize(json);
            return decode.IsOk ? decode.Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Une valeur absente ne vaut aucune valeur presente, et deux absences se valent.</summary>
    private static bool MemeValeur(string? attendue, uint? lue)
    {
        if (attendue is null) return lue is null;
        if (lue is null) return false;
        return uint.TryParse(attendue, out var a)
            ? a == lue.Value
            : string.Equals(attendue, lue.Value.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
