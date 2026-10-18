using System.Globalization;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Leases;

// -----------------------------------------------------------------------
// HelperDiagnosticsLog : un IDiagnosticsLog minimal et autonome, pour l'usage propre du helper
// élevé (le chien de garde des baux y journalise les retours automatiques).
//
// Exprès sans référence à DrDxt.Infrastructure.DiagnosticsLog : le helper élevé (la surface de
// risque n°1) ne référence que Core ; le journal reste donc un petit ajout au fichier, au mieux,
// au lieu de tirer Infrastructure dans le processus élevé. Un échec d'écriture (disque plein,
// ACL) est avalé : le journal est une trace d'audit secondaire et ne doit jamais interrompre ni
// faire planter le helper.
// -----------------------------------------------------------------------
public sealed class HelperDiagnosticsLog : IDiagnosticsLog
{
    private const string DateFormat = "yyyy-MM-ddTHH:mm:ssZ";
    private readonly string _journalPath;
    private readonly object _lock = new();

    private readonly bool _ecrivable = true;

    public HelperDiagnosticsLog(string? directory = null)
    {
        if (directory is null)
        {
            // Le journal quitte le profil de l'utilisateur. Le controle de jonction
            // ci-dessous laissait une course entre controle et ecriture ; dans %ProgramData%\DrDxt
            // durci (proprietaire Administrateurs, utilisateurs en lecture) personne d'autre qu'un
            // administrateur ne peut rien y poser. Racine non durcissable = pas de journal.
            _journalPath = CheminParDefaut();
            _ecrivable = DrDxt.Reconciler.Manifest.TrustedManifestRoot
                .EnsureCreatedHardened(Path.GetDirectoryName(_journalPath)!).IsOk;
            return;
        }
        try { Directory.CreateDirectory(directory); } catch { /* best-effort */ }
        _journalPath = Path.Combine(directory, "helper.journal.log");
    }

    /// <summary>Le journal par defaut : a la racine durcie, jamais dans le profil. Sans effet de
    /// bord, contrairement au constructeur qui cree ou repare la racine.</summary>
    internal static string CheminParDefaut() =>
        Path.Combine(DrDxt.Reconciler.Manifest.ProgramDataManifestStorage.DefaultRoot(), "helper.journal.log");

    public void Change(string tweakId, string from, string to, string actor)
    {
        var ts = DateTime.UtcNow.ToString(DateFormat, CultureInfo.InvariantCulture);
        var line = $"{ts} [{actor}] tweak={tweakId} from='{from}' to='{to}'";
        lock (_lock)
        {
            try
            {
                // Defense en profondeur (le chemin par defaut est deja la racine durcie) : un
                // dossier remplace par une jonction enverrait cet ajout eleve n'importe ou, System32
                // compris. On n'ecrit jamais a travers un point de reparse.
                if (!_ecrivable || TraverseUnPointDeReparse(_journalPath)) return;
                // La réparation de la racine ne descend pas : un fichier posé d'avance en lien dur n'est jamais écrit.
                DrDxt.Core.FichierJsonAtomique.AjouterSansLien(_journalPath, line + Environment.NewLine, "HELPER_JOURNAL");
            }
            catch { /* secondary audit trail — never abort the helper on a write failure */ }
        }
    }

    // Le fichier, son dossier et le dossier au-dessus (« DrDXT ») : les trois niveaux que DrDXT crée.
    internal static bool TraverseUnPointDeReparse(string fichier)
    {
        var p = fichier;
        for (var niveau = 0; niveau < 3 && !string.IsNullOrEmpty(p); niveau++, p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint))
                return true;
        return false;
    }

    public Task<Result<string>> ExportAsync()
    {
        try
        {
            string content;
            lock (_lock)
            {
                content = File.Exists(_journalPath) ? File.ReadAllText(_journalPath) : string.Empty;
            }
            return Task.FromResult(Result<string>.Ok(content));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<string>.Fail(ErrorCodes.IoFailure, $"Failed to read helper journal: {ex.Message}"));
        }
    }
}
