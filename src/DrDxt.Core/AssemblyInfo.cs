// Une DLL cherchée par son seul nom passe sinon par le dossier courant et le PATH, modifiables par l'utilisateur.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(
    System.Runtime.InteropServices.DllImportSearchPath.System32)]
