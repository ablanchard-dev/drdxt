using System.Windows;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            // où se trouvent les dictionnaires de ressources propres à un thème
                                                // (utilisé si une ressource est introuvable dans la page
                                                // ou dans les dictionnaires de ressources de l'application)
    ResourceDictionaryLocation.SourceAssembly   // où se trouve le dictionnaire de ressources générique
                                                // (utilisé si une ressource est introuvable dans la page, l'application
                                                // ou les dictionnaires de ressources propres à un thème)
)]

// Une DLL cherchée par son seul nom passe sinon par le dossier courant et le PATH, modifiables par l'utilisateur.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(
    System.Runtime.InteropServices.DllImportSearchPath.System32)]
