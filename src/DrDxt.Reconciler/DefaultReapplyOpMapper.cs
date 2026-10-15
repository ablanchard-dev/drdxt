using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// DefaultReapplyOpMapper : l'IReapplyOpMapper de production, et la frontière de sécurité du
// réconciliateur (I3, la leçon WinRing0).
//
// Il traduit un tweakId réconciliable + sa valeur voulue sérialisée en exactement une des
// feuilles PrivilegedOp scellées existantes, aux champs bornés, ou il échoue avant de produire
// la moindre op. Il ne construit jamais de primitive privilégiée libre ou générique.
//
// Source unique. La cible de l'op (ruche/chemin/nom/type, serviceName, surface) est lue
// entièrement dans le même IReconcilableTweakCatalog que la sonde de lecture : lecture et
// écriture partagent une seule table d'emplacements. Le desiredValueJson fourni par l'appelant
// n'influe que sur le champ de valeur borné, jamais sur la cible : une valeur hostile ne peut pas
// diriger une écriture admin vers une autre clé. La valeur est aussi resérialisée par
// ReconcilableValueCanonicalizer pour que l'op porte la forme identique à l'octet près que la
// sonde relit pour confirmer.
//
// Fermé par construction : un tweakId inconnu (sans descripteur), une valeur illisible ou hors
// type, ou une surface sans feuille d'op en V1 (ScheduledTask / Firmware) → Fail, aucune op.
// Seuls les trois ids branchés en V1 se traduisent : latency.reflex.lowLatencyMode (registre
// DWORD), fixit.sanePowerPlan (PowerPlan), fixit.x3dVCacheService (Service).
// -----------------------------------------------------------------------
public sealed class DefaultReapplyOpMapper : IReapplyOpMapper
{
    private readonly IReconcilableTweakCatalog _catalog;

    public DefaultReapplyOpMapper(IReconcilableTweakCatalog catalog)
        => _catalog = catalog;

    public Result<PrivilegedOp> Map(string tweakId, string desiredValueJson)
    {
        // (1) Résoudre d'abord le descripteur. Un id absent de la table fermée n'est pas
        // réconciliable → refus avant toute analyse de valeur ou construction d'op (I3).
        var descriptor = _catalog.DescriptorFor(tweakId);
        if (descriptor is null)
            return Result<PrivilegedOp>.Fail(
                ReconcilerErrorCodes.TweakNotAllowlisted,
                $"tweak '{tweakId}' has no reconcilable descriptor");

        // (2) Construire l'op depuis la surface du descripteur. La cible ne vient jamais de
        // desiredValueJson : seul le champ borné de valeur / d'identité ci-dessous en vient.
        return descriptor.Surface switch
        {
            ReconcileSurface.Registry => MapRegistry(tweakId, descriptor, desiredValueJson),
            ReconcileSurface.PowerPlan => MapPowerPlan(tweakId, desiredValueJson),
            ReconcileSurface.Service => MapService(tweakId, descriptor, desiredValueJson),

            // Aucune feuille d'op typée ne les réaffirme en V1 (une écriture de tâche planifiée / de
            // firmware est hors de l'ensemble fermé du réconciliateur). Refuser plutôt qu'improviser.
            _ => Result<PrivilegedOp>.Fail(
                ReconcilerErrorCodes.TweakNotAllowlisted,
                $"tweak '{tweakId}' surface {descriptor.Surface} has no op leaf in V1"),
        };
    }

    // Registre → SetRegistryValueOp avec le (ruche, chemin, nom, type) exact du descripteur et une
    // valeur validée pour le type déclaré. L'op porte la forme canonique en chaîne (resérialisée)
    // pour correspondre à l'octet près à ce que lit la sonde.
    private static Result<PrivilegedOp> MapRegistry(
        string tweakId, ReconcilableDescriptor descriptor, string desiredValueJson)
    {
        // Un descripteur de surface registre doit porter son emplacement (invariant de la table
        // fermée) ; garde défensive plutôt que déréférencer un null.
        if (descriptor.Registry is not { } reg)
            return Result<PrivilegedOp>.Fail(
                ReconcilerErrorCodes.TweakNotAllowlisted,
                $"tweak '{tweakId}' is Registry-surface but carries no registry location");

        var canonical = CanonicalizeRegistryValue(reg.Kind, desiredValueJson);
        if (!canonical.IsOk)
            return Result<PrivilegedOp>.Fail(canonical.Error!.Value.Code, canonical.Error.Value.Message);

        return Result<PrivilegedOp>.Ok(
            new SetRegistryValueOp(reg.Hive, reg.Path, reg.Name, reg.Kind, canonical.Value!));
    }

    // Vérifier que la valeur voulue s'analyse pour le type déclaré, puis la resérialiser par le
    // canonicaliseur : zéros de tête et mise en forme sont ramenés à la chaîne exacte que lit la
    // sonde (égalité ordinale avec le manifeste).
    private static Result<string> CanonicalizeRegistryValue(RegistryValueType kind, string desiredValueJson)
        => kind switch
        {
            RegistryValueType.DWord =>
                uint.TryParse(desiredValueJson, NumberStyles.None, CultureInfo.InvariantCulture, out var dword)
                    ? Result<string>.Ok(ReconcilableValueCanonicalizer.Dword(dword))
                    : Result<string>.Fail(
                        ReconcilerErrorCodes.TweakNotAllowlisted,
                        $"value '{desiredValueJson}' is not a valid DWord"),

            RegistryValueType.QWord =>
                ulong.TryParse(desiredValueJson, NumberStyles.None, CultureInfo.InvariantCulture, out var qword)
                    ? Result<string>.Ok(ReconcilableValueCanonicalizer.Qword(qword))
                    : Result<string>.Fail(
                        ReconcilerErrorCodes.TweakNotAllowlisted,
                        $"value '{desiredValueJson}' is not a valid QWord"),

            // Une valeur String n'a pas de forme numérique à valider ; la porter telle quelle.
            RegistryValueType.String =>
                Result<string>.Ok(ReconcilableValueCanonicalizer.String(desiredValueJson)),

            _ => Result<string>.Fail(
                ReconcilerErrorCodes.TweakNotAllowlisted,
                $"unsupported registry value kind {kind}"),
        };

    // PowerPlan → SetActivePowerPlanOp. La valeur voulue est le GUID canonique « D » qu'émet le
    // canonicaliseur ; tout ce qui n'est pas un GUID → Fail (l'allowlist du helper filtre encore les
    // GUID de plan acceptés : un GUID valide reste borné en aval).
    private static Result<PrivilegedOp> MapPowerPlan(string tweakId, string desiredValueJson)
        => Guid.TryParse(desiredValueJson, out var guid)
            ? Result<PrivilegedOp>.Ok(new SetActivePowerPlanOp(guid))
            : Result<PrivilegedOp>.Fail(
                ReconcilerErrorCodes.TweakNotAllowlisted,
                $"value '{desiredValueJson}' is not a valid power-plan GUID for tweak '{tweakId}'");

    // Service → RunServiceControlOp sur le nom de service du descripteur. Seuls les jetons
    // run/stop donnent une action ; « notinstalled » / « unknown » / tout le reste → Fail. Le
    // réconciliateur n'installe ni ne désinstalle jamais un service pour réparer une dérive :
    // ServiceAction.Install n'est jamais émis ici (l'installation relève de l'apply).
    private static Result<PrivilegedOp> MapService(
        string tweakId, ReconcilableDescriptor descriptor, string desiredValueJson)
    {
        if (string.IsNullOrEmpty(descriptor.ServiceName))
            return Result<PrivilegedOp>.Fail(
                ReconcilerErrorCodes.TweakNotAllowlisted,
                $"tweak '{tweakId}' is Service-surface but carries no service name");

        return desiredValueJson switch
        {
            "running" => Result<PrivilegedOp>.Ok(
                new RunServiceControlOp(descriptor.ServiceName, ServiceAction.Start)),
            "stopped" => Result<PrivilegedOp>.Ok(
                new RunServiceControlOp(descriptor.ServiceName, ServiceAction.Stop)),

            // « notinstalled » / « unknown » / tout le reste : le réconciliateur n'installe ni ne désinstalle.
            _ => Result<PrivilegedOp>.Fail(
                ReconcilerErrorCodes.TweakNotAllowlisted,
                $"desired service state '{desiredValueJson}' is not re-appliable for tweak '{tweakId}'"),
        };
    }
}
