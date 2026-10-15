using System.Windows;
using System.Windows.Input;
using DrDxt.App.Effets;

namespace DrDxt.App;

/// <summary>La coquille : la barre de titre du produit, et la règle qui autorise les animations.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // La taille du XAML est ce qu'on veut, pas ce qu'on a. WPF ouvre a la
        // taille demandee meme si elle depasse l'ecran : sur un portable 1366x768, ou sur un
        // 1080p a 150 %, le bas de la fenetre (l'action) s'ouvrirait hors de l'ecran.
        // `WorkArea` retire deja la barre des taches. La regle, elle, est testee sans fenetre.
        var zone = SystemParameters.WorkArea;
        (Width, Height) = TailleDeDepart.Adaptee(Width, Height, zone.Width, zone.Height);

        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) => SystemCommands.MaximizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(this)));

        MoteurDAnimation.Brancher(this);
    }
}
