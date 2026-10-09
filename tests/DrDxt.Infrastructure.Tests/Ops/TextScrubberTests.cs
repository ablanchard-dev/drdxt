using DrDxt.Infrastructure.Ops;
using FluentAssertions;

namespace DrDxt.Infrastructure.Tests.Ops;

// -----------------------------------------------------------------------
// T14/T15 — the SHARED scrubbing pipeline. Both the crash reporter (T14) and the
// diagnostics export (T15) run every outbound string through this so PII / license
// keys / raw fingerprints / user paths never leave the machine in the clear.
//
// These tests pin the scrubbing CONTRACT by assertion (RGPD minimisation, cahier §8):
// each rule is proven on a concrete input, and the "nothing-to-scrub" path is proven
// to leave clean text untouched.
// -----------------------------------------------------------------------
public sealed class TextScrubberTests
{
    private static readonly ITextScrubber Scrub = new TextScrubber();

    [Fact]
    public void Null_Returns_Empty()
        => Scrub.Scrub(null).Should().BeEmpty();

    // --- user path masking: C:\Users\<name> → C:\Users\<redacted> -------------------

    [Fact]
    public void Masks_Windows_User_Path_Username()
    {
        var input = @"at Foo() in C:\Users\alicejoueuse\Downloads\DrDXT\thing.cs:line 12";
        var output = Scrub.Scrub(input);

        output.Should().NotContain("alicejoueuse", "the real username must never leave the machine");
        output.Should().Contain(@"C:\Users\<redacted>");
        output.Should().Contain("thing.cs", "the path TAIL is kept — only the username is masked");
    }

    [Fact]
    public void Masks_User_Path_CaseInsensitively_AnyDrive()
    {
        var input = @"D:\USERS\Alexandre\AppData\Local\DrDXT\log.txt";
        var output = Scrub.Scrub(input);

        output.Should().NotContain("Alexandre");
        output.Should().Contain(@"<redacted>");
        output.Should().Contain("AppData", "the tail after the username is preserved");
    }

    [Fact]
    public void Masks_Forward_Slash_User_Path()
    {
        var input = "file C:/Users/john.doe/secret/app.log opened";
        var output = Scrub.Scrub(input);

        output.Should().NotContain("john.doe");
        output.Should().Contain("<redacted>");
    }

    // --- license key removal --------------------------------------------------------

    [Fact]
    public void Masks_License_Key_Looking_Token()
    {
        // Keygen-style key: groups of hex/alnum separated by dashes.
        var input = "activation failed for key A1B2C3D4-E5F6-7890-ABCD-EF1234567890 retrying";
        var output = Scrub.Scrub(input);

        output.Should().NotContain("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");
        output.Should().Contain("<redacted-key>");
    }

    [Fact]
    public void Une_source_a_tirets_sans_chiffre_n_est_pas_une_cle()
    {
        // 25/09/2026 : le rapport réel masquait la source Microsoft de MMCSS.
        const string source = "learn.microsoft.com/windows/win32/procthread/multimedia-class-scheduler-service";
        Scrub.Scrub($"[source : {source}]").Should().Contain(source);
    }

    [Fact]
    public void Un_paquet_du_Store_n_est_pas_une_cle()
    {
        // 25/09/2026 : titre réel du journal Windows Update, masqué en « <redacted-key>.MSICenter ».
        const string paquet = "9NVMNJCR03XV-9426MICRO-STARINTERNATION.MSICenter";
        Scrub.Scrub($"Mises à jour Windows · {paquet}: 1 échec(s)").Should().Contain(paquet);
    }

    [Fact]
    public void Une_cle_produit_Windows_reste_masquee()
        => Scrub.Scrub("clef VK7JG-NPHTM-C97JM-9MPGT-3V66T fin")
            .Should().NotContain("VK7JG-NPHTM-C97JM-9MPGT-3V66T").And.Contain("<redacted-key>");

    [Fact]
    public void Un_GUID_en_minuscules_reste_masque()
        => Scrub.Scrub("id a1b2c3d4-e5f6-7890-abcd-ef1234567890 fin")
            .Should().NotContain("a1b2c3d4-e5f6-7890-abcd-ef1234567890").And.Contain("<redacted-key>");

    [Fact]
    public void Masks_Labelled_License_Key()
    {
        var input = "licenseKey=f8092b4efaaca228deadbeefcafef00d processed";
        var output = Scrub.Scrub(input);

        output.Should().NotContain("f8092b4efaaca228deadbeefcafef00d");
        output.Should().Contain("<redacted-key>");
    }

    // --- raw fingerprint / long hash removal ----------------------------------------

    [Fact]
    public void Masks_Labelled_Fingerprint()
    {
        var input = "fingerprint: 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08 ok";
        var output = Scrub.Scrub(input);

        output.Should().NotContain("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08");
        output.Should().Contain("<redacted-fp>");
    }

    // Mutation-proofing (2026-08-15): removing the LabelledFingerprint rule broke NO test,
    // because Masks_Labelled_Fingerprint feeds a 64-char hex that BareHex (32+) also
    // catches. The two rules shadowed each other: each was only defended by the other.
    // These two cases isolate them — a labelled fingerprint too SHORT for BareHex, and a
    // bare blob with no label — so each rule now has a test that fails when it is removed.

    [Fact]
    public void Masks_Short_Labelled_Fingerprint_That_BareHex_Cannot_Catch()
    {
        // 10 hex chars: matches LabelledFingerprint (8+) but not BareHex (32+).
        var input = "machine fp: a1b2c3d4e5 reported";
        var output = Scrub.Scrub(input);

        output.Should().NotContain("a1b2c3d4e5");
        output.Should().Contain("<redacted-fp>");
    }

    [Fact]
    public void Masks_Unlabelled_Hex_Blob()
    {
        // 40 hex chars, no label and no dashes: only BareHex can mask it.
        var input = "digest 5f4dcc3b5aa765d61d8327deb882cf99a1b2c3d4 written";
        var output = Scrub.Scrub(input);

        output.Should().NotContain("5f4dcc3b5aa765d61d8327deb882cf99a1b2c3d4");
        output.Should().Contain("<redacted-fp>");
    }

    // --- email / PII removal --------------------------------------------------------

    [Fact]
    public void Masks_Email_Address()
    {
        var input = "user joueur.exemple@example.com hit an error";
        var output = Scrub.Scrub(input);

        output.Should().NotContain("joueur.exemple@example.com");
        output.Should().Contain("<redacted-email>");
    }

    // --- clean text is untouched ----------------------------------------------------

    [Fact]
    public void Leaves_Clean_Text_Untouched()
    {
        const string input = "ReflexLowLatency applied from Off to On; avgFps 139";
        Scrub.Scrub(input).Should().Be(input);
    }

    // --- idempotent: scrubbing already-scrubbed text changes nothing more -----------

    [Fact]
    public void Is_Idempotent()
    {
        var input = @"C:\Users\alicejoueuse\app key A1B2C3D4-E5F6-7890-ABCD-EF1234567890";
        var once = Scrub.Scrub(input);
        var twice = Scrub.Scrub(once);
        twice.Should().Be(once);
    }
}
