using DrDxt.Detection.Cpu;

// ============================================================================================
// LA BOUCLE QUI REGARDE LE PROCESSEUR — par CŒUR, et pendant qu'on fait autre chose.
//
// CE QU'ELLE CORRIGE. `CpuFrequencyReader.Lire()` interroge l'instance `_Total`, moyenne des
// 28 processeurs logiques, cœurs INACTIFS COMPRIS. Mesure du 09/09/2026 en partie : `_Total`
// rendait 3 622 MHz quand les cœurs reellement charges tournaient a 3 861. La moyenne repond a
// cote de la seule question qui compte : a quelle frequence tourne le cœur QUI TRAVAILLE ?
//
// Ce que la boucle suit, releve par releve : la frequence des cœurs charges, combien il y en a,
// et les indicateurs de limite. Ces indicateurs ne voient PAS les limites de puissance du
// paquet (RAPL) : un 0 n'est donc pas un blanc-seing, et l'outil le dit au lieu de le taire.
//
// Le journal CSV permet de relire APRES COUP ce qui s'est passe pendant une partie, sans
// qu'un humain regarde defiler des chiffres.
//
// LECTURE PURE : compteurs de performance, aucune ecriture, aucun reglage.
//
//   dotnet run --project tools/cpuwatch -- [secondes|0=sans fin] [intervalleMs] [journal.csv]
// ============================================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;

var secondes = args.Length > 0 && double.TryParse(args[0], out var s) ? s : 0;
var intervalle = args.Length > 1 && int.TryParse(args[1], out var i) ? i : 2000;
var journal = args.Length > 2 ? args[2] : null;

var carte = new CpuTopology().Lire();
if (carte.Count == 0)
{
    Console.WriteLine("   topologie CPU NON LUE — on ne devine pas quels cœurs sont les rapides.");
    return 2;
}
var classes = CpuTopology.ClassesParPerformance(carte);
var classeHaute = classes[0];
var rapides = carte.Where(p => p.ClasseEfficacite == classeHaute)
                   .Select(p => (int)p.IndexLogique).ToHashSet();

var lecteur = new CpuFrequencyReader();
var etat = lecteur.Lire();
var baseMhz = etat.FrequenceNominaleMhz ?? 0;
if (baseMhz <= 0)
{
    Console.WriteLine("   frequence nominale non lue : sans elle un pourcentage n'est pas des MHz.");
    return 2;
}

Console.WriteLine("== BOUCLE DE SURVEILLANCE PROCESSEUR ==");
Console.WriteLine($"   {carte.Count} processeurs logiques, dont {rapides.Count} de la classe "
                  + $"{CpuTopology.NommerClasse(classeHaute, classes)}   base {baseMhz:0} MHz");
Console.WriteLine($"   releve toutes les {intervalle} ms" + (secondes > 0 ? $", pendant {secondes:0.#} s" : ", sans fin (Ctrl+C pour sortir)"));
if (journal is not null)
{
    if (!File.Exists(journal))
        File.WriteAllText(journal, "horodatage;coeurs_charges;mhz_moyen;mhz_max;mhz_total;bridage\n");
    Console.WriteLine($"   journal : {journal}");
}
Console.WriteLine();
Console.WriteLine("   heure      charges   moyenne     max    _Total   bridage");
Console.WriteLine("   (* « aucun » = les indicateurs Windows valent 0. Ils ne voient PAS les limites");
Console.WriteLine("      de puissance du paquet : ce n'est donc pas la preuve que rien ne bride.)");

var fin = secondes > 0 ? DateTime.UtcNow.AddSeconds(secondes) : DateTime.MaxValue;
var historique = new List<double>();

while (DateTime.UtcNow < fin)
{
    var coeurs = lecteur.LireParProcesseur()
        .Where(c => c.IndexLogique >= 0 && rapides.Contains(c.IndexLogique))
        .ToList();

    // « Charge » se decide sur l'UTILISATION, jamais sur la frequence : un cœur inactif peut
    // afficher une frequence haute une fraction de seconde, et le compter fausserait tout.
    var charges = coeurs.Where(c => c.UtilisationPourcent >= 60 && c.PourcentageDeNominal is not null).ToList();
    var global = lecteur.Lire();

    if (charges.Count == 0)
    {
        Console.WriteLine($"   {DateTime.Now:HH:mm:ss}         0         -       -   {global.FrequenceEffectiveMhz ?? 0,6:0}   "
                          + Bridage(global));
    }
    else
    {
        var mhz = charges.Select(c => c.PourcentageDeNominal!.Value * baseMhz / 100.0).ToList();
        var moy = mhz.Average();
        historique.Add(moy);
        Console.WriteLine($"   {DateTime.Now:HH:mm:ss}   {charges.Count,7}   {moy,7:0}   {mhz.Max(),5:0}   "
                          + $"{global.FrequenceEffectiveMhz ?? 0,6:0}   " + Bridage(global));
        if (journal is not null)
            File.AppendAllText(journal,
                $"{DateTime.Now:O};{charges.Count};{moy:0};{mhz.Max():0};{global.FrequenceEffectiveMhz ?? 0:0};{global.IndicateursDeLimite ?? -1:0}\n");
    }

    Thread.Sleep(intervalle);
}

if (historique.Count > 0)
{
    historique.Sort();
    Console.WriteLine();
    Console.WriteLine($"   cœurs charges — mediane {historique[historique.Count / 2]:0} MHz"
                      + $"   min {historique[0]:0}   max {historique[^1]:0}   ({historique.Count} releves)");
}
return 0;

static string Bridage(CpuFrequencyState e) => e.EstBride switch
{
    null => "non lu",
    true => $"OUI (masque {e.IndicateursDeLimite:0})",
    false => "aucun*",
};
