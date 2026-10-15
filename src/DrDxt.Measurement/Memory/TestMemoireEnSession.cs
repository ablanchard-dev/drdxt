using static DrDxt.Core.TexteFrancais;
using DrDxt.Core;

namespace DrDxt.Measurement.Memory;

/// <param name="OctetsTestes">La memoire reellement ecrite puis relue, par passe.</param>
/// <param name="Passes">Combien de cycles ecrire-verifier ont ete completes sur tout le volume.</param>
/// <param name="Erreurs">Mots de 64 bits relus differents de ce qui avait ete ecrit.</param>
public sealed record ResultatTestMemoire(long OctetsTestes, int Passes, long Erreurs, TimeSpan Duree, int Fils)
{
    /// <summary>Ce que le resultat autorise a dire, limites comprises.</summary>
    public string Phrase => OctetsTestes == 0
        ? Docteur.T("TM.RienTeste")
        : Erreurs > 0
        ? Docteur.T("TM.Erreurs", Erreurs, Passes, Go)
        : Docteur.T("TM.Aucune", Passes, Go, Duree.TotalMinutes, Fils);

    private double Go => OctetsTestes / (double)(1L << 30);
}

/// <summary>
/// Tester la RAM sans redemarrer, parce que le test de Windows exige un redemarrage que
/// le joueur n'a pas toujours le temps de faire.
///
/// Methode des testeurs en session reconnus (TestMem5, HCI MemTest, Karhu) : ecrire un motif connu
/// dans de gros blocs, puis tout relire et comparer, sur tous les coeurs a la fois pour charger le
/// controleur memoire. Motif par mot de 64 bits derive de son adresse (un bit colle ou une adresse
/// qui en recouvre une autre rendent une valeur differente), puis son complement (chaque bit passe
/// par 0 et par 1). Ce que ces outils revendiquent (trouver des erreurs que MemTest86 manque parce
/// qu'elles n'apparaissent que sous charge et a chaud) vaut ici aussi ; leur limite aussi : la
/// memoire du systeme n'est pas testee.
/// </summary>
public static class TestMemoireEnSession
{
    /// <summary>Le coeur : ecrit puis verifie un bloc. <paramref name="entreEcritureEtLecture"/> est une
    /// couture de test (y retourner un bit prouve que la verification le voit).</summary>
    public static long EcrireVerifier(ulong[] bloc, ulong graine, Action<ulong[]>? entreEcritureEtLecture = null)
    {
        for (var i = 0; i < bloc.Length; i++) bloc[i] = Motif(graine, (ulong)i);
        entreEcritureEtLecture?.Invoke(bloc);
        long erreurs = 0;
        for (var i = 0; i < bloc.Length; i++) if (bloc[i] != Motif(graine, (ulong)i)) erreurs++;

        for (var i = 0; i < bloc.Length; i++) bloc[i] = ~Motif(graine, (ulong)i);
        entreEcritureEtLecture?.Invoke(bloc);
        for (var i = 0; i < bloc.Length; i++) if (bloc[i] != ~Motif(graine, (ulong)i)) erreurs++;
        return erreurs;
    }

    /// <summary>Teste <paramref name="octets"/> repartis sur <paramref name="fils"/> fils jusqu'a
    /// <paramref name="duree"/> (au moins une passe complete). Une allocation refusee reduit le volume
    /// au lieu d'echouer : le resultat dit ce qui a ete teste, pas ce qui etait demande.</summary>
    public static ResultatTestMemoire Lancer(long octets, TimeSpan duree, int fils, CancellationToken ct)
    {
        fils = Math.Max(1, fils);
        var motsParFil = Math.Max(1, octets / 8 / fils);
        var blocs = new List<ulong[]>();
        for (var f = 0; f < fils; f++)
        {
            try { blocs.Add(GC.AllocateUninitializedArray<ulong>((int)Math.Min(motsParFil, int.MaxValue / 2), pinned: false)); }
            catch (OutOfMemoryException) { break; }
        }

        // Rien d'alloue, rien a tester. Sans ce retour, la boucle tournait a vide toute la
        // duree ; et le resultat rapporte les fils qui ont vraiment tourne, pas ceux demandes.
        if (blocs.Count == 0) return new ResultatTestMemoire(0, 0, 0, TimeSpan.Zero, 0);
        fils = blocs.Count;

        var chrono = System.Diagnostics.Stopwatch.StartNew();
        long erreurs = 0;
        var passes = 0;
        do
        {
            var graine = 0x9E3779B97F4A7C15UL * (ulong)(passes + 1);
            var parBloc = new long[blocs.Count];
            // L'arret se prend entre deux passes : une passe coupee au milieu ne compterait ni comme
            // testee ni comme saine. Le resultat dit ce qui a ete fini.
            Parallel.For(0, blocs.Count, new ParallelOptions { MaxDegreeOfParallelism = fils },
                b => parBloc[b] = EcrireVerifier(blocs[b], graine + (ulong)b));
            erreurs += parBloc.Sum();
            passes++;
        }
        while (chrono.Elapsed < duree && !ct.IsCancellationRequested);

        var teste = blocs.Sum(b => (long)b.Length * 8);
        blocs.Clear();
        return new ResultatTestMemoire(teste, passes, erreurs, chrono.Elapsed, fils);
    }

    public const string Domaine = "MEMOIRE";

    /// <summary>Le resultat, tel que la sante l'affiche. Des erreurs : un defaut prouve. Zero erreur :
    /// « observe », jamais « sain » : ce test peut trouver un defaut, il ne prouve pas l'absence.</summary>
    public static RepairFinding Constat(ResultatTestMemoire r) => r.OctetsTestes == 0
        ? new RepairFinding(Domaine, "test mémoire en session", Docteur.T("TM.NonTeste"), null, RepairStatus.Unknown, r.Phrase)
        : r.Erreurs > 0
        ? new RepairFinding(Domaine, "test mémoire en session", Docteur.T("TM.NErreurs", r.Erreurs), Docteur.T("TM.AucuneErreur"),
            RepairStatus.KnownHarmful, r.Phrase + Docteur.T("TM.Baisser"),
            RepairAction.Recommandation)
        : new RepairFinding(Domaine, "test mémoire en session", Docteur.T("TM.AucuneErreur"), null, RepairStatus.Observed, r.Phrase);

    private static ulong Motif(ulong graine, ulong index)
    {
        // splitmix64 : chaque adresse a sa valeur, deux adresses voisines n'ont rien en commun.
        var z = graine + index * 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
