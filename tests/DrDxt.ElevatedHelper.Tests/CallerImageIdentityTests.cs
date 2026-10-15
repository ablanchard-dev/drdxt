using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// Durcissement de l'authentification de l'appelant : la vérification du chemin de
// développement contrôle l'identité de l'image qui se connecte (chemin + empreinte), pas
// seulement son chemin. Un pair au même chemin mais d'un binaire différent est refusé :
// l'égalité de chemin seule ferait confiance à un exécutable substitué.
//
// La vraie vérification Authenticode (WinVerifyTrust + éditeur épinglé) des versions
// signées reste à faire ; cette couche est la porte d'identité des versions de
// développement non signées (chemin + empreinte de l'image). Jamais ouverte : doute = refus.
//
// La résolution d'identité est derrière une couture (ICallerImageIdentity), testée ici
// avec des faux ; la vraie résolution chemin + empreinte est la frontière vivante.
// -----------------------------------------------------------------------
public sealed class CallerImageIdentityTests
{
    private const string ExpectedExe = @"C:\Program Files\DrDXT\DrDxt.App.exe";
    private static readonly byte[] ExpectedHash = { 1, 2, 3, 4, 5 };

    // Faux résolveur d'identité : associe un PID à (chemin, empreinte) ou rend null.
    private sealed class FakeIdentity : ICallerImageIdentity
    {
        private readonly (string path, byte[]? hash)? _result;
        private readonly bool _throws;
        public FakeIdentity((string, byte[]?)? result = null, bool throws = false)
        {
            _result = result;
            _throws = throws;
        }
        public CallerImage? Resolve(int pid)
        {
            if (_throws) throw new InvalidOperationException("boom");
            return _result is { } r ? new CallerImage(r.path, r.hash) : null;
        }
    }

    private static DrDxtAppCallerAuthenticator Auth(ICallerImageIdentity identity) =>
        DrDxtAppCallerAuthenticator.WithImageIdentity(ExpectedExe, ExpectedHash, identity);

    [Fact]
    public void Authorizes_when_path_and_hash_both_match()
    {
        var auth = Auth(new FakeIdentity((ExpectedExe, ExpectedHash)));
        auth.Authorize(new CallerInfo(1234)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Denies_when_path_matches_but_hash_differs()
    {
        // Même chemin, binaire différent (exécutable substitué) → refus : c'est la brèche fermée.
        var auth = Auth(new FakeIdentity((ExpectedExe, new byte[] { 9, 9, 9 })));
        var r = auth.Authorize(new CallerInfo(1234));
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    [Fact]
    public void Denies_when_path_differs_even_if_hash_matches()
    {
        var auth = Auth(new FakeIdentity((@"C:\Windows\System32\cmd.exe", ExpectedHash)));
        auth.Authorize(new CallerInfo(1234)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Denies_when_hash_could_not_be_computed()
    {
        // Aucune empreinte résolue (fichier illisible) → identité improuvable → refus.
        var auth = Auth(new FakeIdentity((ExpectedExe, (byte[]?)null)));
        auth.Authorize(new CallerInfo(1234)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Denies_when_identity_unresolved()
    {
        var auth = Auth(new FakeIdentity(result: null));
        auth.Authorize(new CallerInfo(1234)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Denies_when_identity_resolver_throws()
    {
        var auth = Auth(new FakeIdentity(throws: true));
        var r = auth.Authorize(new CallerInfo(1234));
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
    }

    [Fact]
    public void Denies_when_no_pid()
    {
        var auth = Auth(new FakeIdentity((ExpectedExe, ExpectedHash)));
        auth.Authorize(new CallerInfo(null)).IsOk.Should().BeFalse();
        auth.Authorize(default).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Constructor_rejects_empty_expected_hash()
    {
        Action act = () => DrDxtAppCallerAuthenticator.WithImageIdentity(
            ExpectedExe, Array.Empty<byte>(), new FakeIdentity((ExpectedExe, ExpectedHash)));
        act.Should().Throw<ArgumentException>();
    }

    // MainModule lit la liste des modules dans la mémoire du processus appelant : une valeur qu'il contrôle. Le chemin
    // vient du noyau (QueryFullProcessImageNameW, comme PipeServerIdentity), et le résolveur ne passe plus par MainModule.
    [Fact]
    public void Le_chemin_de_l_appelant_vient_du_noyau_pas_de_sa_memoire()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var source = File.ReadAllText(Path.Combine(d!.FullName, "src", "DrDxt.ElevatedHelper", "Ipc", "CallerImageIdentity.cs"));
        source.Should().NotContain(".MainModule");

        var moi = new Win32CallerImageIdentity().Resolve(Environment.ProcessId);
        moi!.Value.Path.Should().BeEquivalentTo(Environment.ProcessPath);
    }
}
