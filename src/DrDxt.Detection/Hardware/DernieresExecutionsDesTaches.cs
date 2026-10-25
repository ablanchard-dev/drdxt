using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>La dernière exécution de chaque tâche planifiée, par l'objet COM « Schedule.Service »
/// (IRegisteredTask.LastRunTime), sans droits, dossiers compris. Une tâche jamais lancée (année avant 2000) est écartée.
/// <c>null</c> = non lu.</summary>
public static class DernieresExecutionsDesTaches
{
    /// <summary>Le Planificateur n'a pas de délai : un service bloqué figerait la famille, d'où la borne de temps. Figé ou
    /// illisible : <c>null</c>, non lu.</summary>
    public static IReadOnlyList<TacheExecutee>? Lire()
        => DrDxt.Core.LectureWmiBornee.Lire<IReadOnlyList<TacheExecutee>?>(LireSansBorne, () => null);

    private static IReadOnlyList<TacheExecutee>? LireSansBorne()
    {
        try
        {
            if (Type.GetTypeFromProgID("Schedule.Service") is not { } type) return null;
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            var liste = new List<TacheExecutee>();
            Parcourir(service.GetFolder(@"\"), liste);
            return liste;
        }
        catch (Exception) { return null; }
    }

    private static void Parcourir(dynamic dossier, List<TacheExecutee> liste)
    {
        // 1 = TASK_ENUM_HIDDEN : les tâches cachées comptent aussi.
        foreach (dynamic t in dossier.GetTasks(1))
        {
            try
            {
                DateTime derniere = t.LastRunTime;
                if (derniere.Year >= 2000) liste.Add(new TacheExecutee((string)t.Path, new DateTimeOffset(derniere)));
            }
            catch (Exception) { /* une tâche illisible n'empêche pas les autres */ }
        }
        foreach (dynamic sous in dossier.GetFolders(0)) Parcourir(sous, liste);
    }
}
