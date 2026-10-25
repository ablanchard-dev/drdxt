using FluentAssertions;

namespace DrDxt.App.Tests;

// Le relevé de fond demande si une App tourne par le mutex de l'instance unique, sans le prendre.
public sealed class InstanceUniqueOuverteTests
{
    [Fact]
    public void Une_App_qui_tient_le_mutex_se_voit_ouverte_et_sinon_non()
    {
        var nom = $@"Local\DrDXT-App-test-{Guid.NewGuid():N}";
        InstanceUnique.Ouverte(nom).Should().BeFalse();

        using var app = InstanceUnique.Prendre(nom, () => { });
        app.Should().NotBeNull("témoin : la place est prise");
        InstanceUnique.Ouverte(nom).Should().BeTrue();
    }

    // L'App élevée tient un mutex que le relevé non élevé ne peut pas ouvrir : refusé, il existe, l'App est ouverte (et la
    // tâche du relevé ne tombe pas en faute).
    [Fact]
    public void Un_mutex_qu_on_ne_peut_pas_ouvrir_dit_l_App_ouverte()
    {
        var nom = $@"Local\DrDXT-App-test-{Guid.NewGuid():N}";
        using var mutex = new Mutex(false, nom);
        var refus = new System.Security.AccessControl.MutexSecurity();
        refus.AddAccessRule(new System.Security.AccessControl.MutexAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.MutexRights.FullControl, System.Security.AccessControl.AccessControlType.Deny));
        mutex.SetAccessControl(refus);

        InstanceUnique.Ouverte(nom).Should().BeTrue();
    }

    // Un second lancement non élevé, l'App élevée déjà ouverte : Prendre levait sur ce même refus, et le processus restait sans
    // fenêtre. La place est tenue : il se retire.
    [Fact]
    public void Prendre_devant_un_mutex_qu_on_ne_peut_pas_ouvrir_cede_la_place()
    {
        var nom = $@"Local\DrDXT-App-test-{Guid.NewGuid():N}";
        using var mutex = new Mutex(false, nom);
        var refus = new System.Security.AccessControl.MutexSecurity();
        refus.AddAccessRule(new System.Security.AccessControl.MutexAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.MutexRights.FullControl, System.Security.AccessControl.AccessControlType.Deny));
        mutex.SetAccessControl(refus);

        InstanceUnique.Prendre(nom, () => { }).Should().BeNull();
    }

    // La relance élevée : l'App qui s'éteint tient encore la place quand la nouvelle démarre. La nouvelle attend qu'elle la
    // rende au lieu de se croire un second lancement et de s'arrêter : le joueur a accepté l'UAC et n'aurait plus d'App.
    [Fact]
    public async System.Threading.Tasks.Task Apres_une_relance_la_nouvelle_App_attend_que_l_ancienne_rende_la_place()
    {
        var nom = $@"Local\DrDXT-App-test-{Guid.NewGuid():N}";
        var ancienne = InstanceUnique.Prendre(nom, () => { });
        ancienne.Should().NotBeNull("témoin : la première prend la place");

        // Sur son propre fil, et la place rendue sans passer par le pool : un pool saturé retarderait le rendu au-delà de
        // l'attente, et le test jugerait la vitesse de la machine au lieu de l'attente elle-même.
        var nouvelle = System.Threading.Tasks.Task.Factory.StartNew(() => InstanceUnique.Prendre(nom, () => { }, TimeSpan.FromSeconds(30)),
            CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);
        Thread.Sleep(300);
        ancienne!.Dispose();

        using var prise = await nouvelle;
        prise.Should().NotBeNull();
    }
}
