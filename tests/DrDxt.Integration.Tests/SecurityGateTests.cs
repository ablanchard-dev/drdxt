using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// T21 — CI security gate (M5).  A STATIC safety net over everything M5 built
// (licence T1-T9/T18, update T12-T13).  These are not behaviour tests — they scan
// the source TREE and FAIL the build if a security regression is introduced:
//
//   (1) ANTI-SECRET     no Keygen admin token / webhook HMAC secret / bearer token /
//                       long secret-shaped blob may live in src/DrDxt.*  (the server
//                       secret lives only in server/, via env, never committed).
//   (2) anti-fail-open  no `catch` in the licence/update trust layer may return a
//                       SUCCESS (Result.Ok / true / LicenseStatus.Licensed).  A broken
//                       signature must never become a pass (§1, §9 of 07-SECURITY).
//   (3) public-only keys  EmbeddedKeys / UpdateDevKeys expose only public material;
//                       the dev/test PRIVATE seed must stay banner-fenced and is never
//                       referenced by PRODUCTION code (only by tests).
//
// These tests carry [Trait("Category","SecurityGate")] so CI can select them exactly
// (mirrors the M4 ReconcileGate pattern in .github/workflows/ci.yml).  A 0-match
// filter would be a silent gate, so the trait is applied to real, present tests here.
//
// Repo-root resolution: every test resolves the repo root by walking UP from the test
// assembly's location (AppContext.BaseDirectory) until it finds the directory that
// contains DrDXT.sln.  This is robust to the bin/Debug|Release/net8.0-windows nesting
// and to being run from any CWD (CI, IDE, dotnet test).
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SecurityGateTests
{
    // ── repo-root resolution (walk up to the folder holding DrDXT.sln) ───────────
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate DrDXT.sln by walking up from " + AppContext.BaseDirectory +
            " — the security gate needs the repo root to scan src/DrDxt.*.");
    }

    private static string SrcRoot()
    {
        var src = Path.Combine(RepoRoot(), "src");
        Directory.Exists(src).Should().BeTrue("the security gate scans the src/ tree");
        return src;
    }

    // Every production .cs under src/DrDxt.* (skip generated obj/bin).
    private static IReadOnlyList<string> ProductionCsFiles()
        => FichiersDuDepot.Sous(SrcRoot(), "*.cs")
            .Where(p => !IsGenerated(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    private static bool IsGenerated(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  (1) ANTI-SECRET  — no server-only credential may appear in the client tree.
    //
    //  Method: scan every production .cs line.  Line comments (// …) and the bodies
    //  of // banners are STRIPPED first, because the codebase legitimately *documents*
    //  these forbidden tokens in prose ("the admin TOKEN and the webhook HMAC secret
    //  are SERVER-only").  We only flag tokens that survive comment-stripping, i.e.
    //  tokens in actual CODE / string literals.  Zero false positives on the current
    //  tree (verified: the only matches today are in comments).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    [Trait("Category", "SecurityGate")]
    public void NoServerSecretIsCommittedInTheClientTree()
    {
        // Forbidden code-token substrings (case-insensitive), the shape a leaked
        // server credential would take.  Not matched in comments.
        var forbidden = new[]
        {
            "Authorization: Bearer", // a bearer/admin header would mean the client holds a server token
            "admin-token",
            "admin_token",
            "keygen_admin",
            "keygenadmin",
        };

        var offenders = new List<string>();

        foreach (var file in ProductionCsFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = StripLineComment(lines[i]);
                if (string.IsNullOrWhiteSpace(code))
                    continue;

                foreach (var token in forbidden)
                {
                    if (code.Contains(token, StringComparison.OrdinalIgnoreCase))
                        offenders.Add($"{Rel(file)}:{i + 1}  [{token}]  {lines[i].Trim()}");
                }

                // A long secret-shaped blob assigned to a *secret*/*token*/*apiKey*/
                // *password*/*hmac* member is a leaked secret.  Ed25519 PUBLIC keys
                // (byte arrays 0x..) and the public SPKI pins are not caught: they are
                // neither named like a secret nor a contiguous base64/hex literal of a
                // secret field.
                if (SecretNamedLongLiteral.IsMatch(code))
                    offenders.Add($"{Rel(file)}:{i + 1}  [secret-named long literal]  {lines[i].Trim()}");
            }
        }

        offenders.Should().BeEmpty(
            "no Keygen admin token, webhook HMAC secret, bearer token, or secret-named blob " +
            "may live in src/DrDxt.* — the server secret lives ONLY in server/ via env (07-SECURITY §4.4):" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // An identifier that CONTAINS a secret word (secret / token / admin / bearer /
    // credential / apiKey / password / passwd / hmac - anywhere in the name, e.g.
    // WebhookHmacSecret, AdminKey, ServerToken) assigned (= or :) a >=20-char base64/hex
    // STRING LITERAL.  This is the "long blob in a *secret*/*token*/*apiKey* field" case
    // from the plan.
    //
    // Measured 26/08/2026 by planting the same 28-char base64 blob in EmbeddedKeys.cs:
    //   WebhookHmacSecret  = "..."   -> caught      AdminKey         = "..." -> MISSED
    //   "Authorization: Bearer ..."  -> caught      ServerToken      = "..." -> MISSED
    //   KeygenAdminKey     = "..."   -> caught      BearerCredential = "..." -> MISSED
    // The word list carried secret/api_key/password/passwd/hmac while the PLAN said
    // "*secret*/*token*/*apiKey*": token/admin/bearer/credential were never added, so a
    // pasted Keygen admin token in a field named AdminKey shipped SILENTLY.  Added now.
    //
    // CEILING, stated on purpose: the same blob SPLIT across two literals
    // ("aGVsbG8" + "d29ybGQ") still passes, and always will - that is deliberate evasion,
    // not the accidental paste this gate exists to stop.  No text scan closes it.
    //
    //   * the identifier is [A-Za-z0-9_]* containing one of the words (case-insensitive);
    //   * "key" alone is intentionally not a trigger (too broad - "publicKey", "KeyBlob"),
    //     only the compound "apikey"/"api_key" is.  The SPKI pins sit in an array
    //     initializer with no `identifier =` before them, so they stay unflagged.
    private static readonly Regex SecretNamedLongLiteral = new(
        @"\b[A-Za-z0-9_]*(secret|token|admin|bearer|credential|api_?key|password|passwd|hmac)[A-Za-z0-9_]*""?\s*[=:]\s*""[A-Za-z0-9+/]{20,}={0,2}""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // ─────────────────────────────────────────────────────────────────────────
    //  (2) anti-fail-open — no catch in the trust layer returns success.
    //
    //  Files guarded: the four that decide trust (signature/licence/update/gate).
    //  Method: for each `catch` keyword we extract its BLOCK BODY by brace-balancing
    //  ( `catch (...) { … }` ), then STRIP comments from the body, then assert the body
    //  contains NO success-return pattern.  Because we only look INSIDE catch bodies,
    //  the many *legitimate* `return LicenseStatus.Licensed` / `return Result.Ok()` that
    //  live OUTSIDE catch (the happy path) are never flagged — that is the false-positive
    //  the plan warns about, and the brace-balancing precisely avoids it.
    //
    //  Proven zero-false-positive on today's code (already fail-clean): every catch in
    //  these files returns Result.Fail / Result<…>.Fail / null.
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [Trait("Category", "SecurityGate")]
    [InlineData("src/DrDxt.Infrastructure/Updates/UpdateVerifier.cs")]
    public void NoCatchInTheTrustLayerReturnsSuccess(string relativePath)
    {
        var full = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(full).Should().BeTrue($"the guarded trust-layer file {relativePath} must exist");

        var source = File.ReadAllText(full);

        var offenders = new List<string>();
        foreach (var body in EnumerateCatchBodies(source))
        {
            var code = StripComments(body.Body);

            // Any of these inside a catch body is a fail-OPEN: a thrown/faulted path that
            // ends up GRANTING trust instead of refusing it.
            if (SuccessReturnInCatch.IsMatch(code))
            {
                offenders.Add(
                    $"{relativePath}: a catch block (near offset {body.Offset}) returns SUCCESS — " +
                    "a faulted signature/licence/update path must FAIL-CLEAN, never grant trust.");
            }
        }

        offenders.Should().BeEmpty(
            "the licence/update trust layer must be FAIL-CLEAN: no catch may return " +
            "Result.Ok / true / LicenseStatus.Licensed (07-SECURITY §1, §9):" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // Success MENTIONED anywhere in a catch body — not just `return <success>`.
    //
    // Measured 26/08/2026 by mutating the catch of Ed25519LicenseVerifier.Verify:
    //   `return Result.Ok();`                                   -> caught (red)
    //   `var passe = Result.Ok(); return passe;`                -> not caught (green)
    //   `return signature.Length > 0 ? Result.Ok() : Fail(...);` -> not caught (green)
    // The old pattern anchored on `return\s+`, so two ordinary ways of writing the same
    // fail-open walked straight through a gate whose name promises it cannot happen.
    // Dropping the anchor catches all three. Verified zero false positives: none of the
    // 7 catch bodies in the guarded files mentions Ok / Licensed / true today.
    //
    // CEILING, stated on purpose - this is a text scan, not a compiler. It still cannot
    // see a success routed through a helper (`return Grant();`), a field set in the catch
    // and returned after it, or a swallowing `catch { }` that falls through to the happy
    // path. Those need a BEHAVIOURAL test (fault-inject, assert Fail), not a wider regex.
    private static readonly Regex SuccessReturnInCatch = new(
        @"(Result(<[^>]+>)?\s*\.\s*Ok)\b" +
        @"|(LicenseStatus\s*\.\s*Licensed)\b" +
        @"|\bOk\s*\(" +
        @"|return\s+true\s*;",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // ─────────────────────────────────────────────────────────────────────────
    //  (3) public-only key files — UpdateDevKeys (banner-fenced in-file seed).
    //
    //  Invariant: the client root-of-trust files expose only public material in a way
    //  PROD code may consume.  For UpdateDevKeys, a dev/test PRIVATE seed is tolerated only if:
    //    (a) it is clearly banner-fenced as DEV/TEST, AND
    //    (b) it is never referenced by production code (only by tests).
    //  The gate fails if a private seed loses its dev/test banner, or if any production
    //  file references the seed member.  EmbeddedKeys (license) and ManifestDevKeys are held
    //  to the STRICTER "no seed in any config" bar instead (see (4a) / (4a-bis)).
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [Trait("Category", "SecurityGate")]
    [InlineData("src/DrDxt.Infrastructure/Updates/UpdateDevKeys.cs", "UpdateDevKeys", "DevPrivateSeed")]
    // NB: the MANIFEST key (ManifestDevKeys) is not in this list — it is held to a STRICTER
    // bar than an in-file banner-fenced seed: its private seed ships NOWHERE in src/ (Debug
    // or Release), it lives only in the test assemblies. See
    // ManifestDevKeys_ShipsNoUsablePrivateSeedInAnyConfig below. Only UpdateDevKeys still
    // keeps a banner-fenced in-file dev seed.
    public void DevPrivateSeedIsBannerFencedAndNeverUsedByProductionCode(
        string relativePath, string className, string seedMember)
    {
        var full = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(full).Should().BeTrue();
        var source = File.ReadAllText(full);

        // (a) The seed member is present and BANNER-FENCED as DEV/TEST.  We require the
        // explicit dev/test marker near the file (the loud "DEV / TEST PRIVATE SEED"
        // banner) — its absence means someone stripped the warning.
        source.Should().Contain(seedMember, $"{relativePath} declares the dev/test seed");
        source.Should().MatchRegex(
            @"DEV\s*/\s*TEST",
            $"{relativePath}: the private seed MUST keep its DEV/TEST banner — a release ships public material only");
        source.Should().Contain("NEVER READ AT RUNTIME",
            $"{relativePath}: the private-seed banner must state it is never read by production code");

        // (b1) INSIDE the declaring file, the seed appears only as its declaration
        // (`… byte[] DevPrivateSeed =>`) — it is never consumed by a sibling production
        // method (the thing that would turn the client into a forger, e.g. a
        // `CreateDevSigner() => …FromSeed(DevPrivateSeed)`).  Comments / XML-docs that
        // merely NAME the seed are stripped first, so only real code counts.
        var selfUses = CodeLinesReferencing(full, seedMember)
            .Where(l => !IsSeedDeclaration(l, seedMember))
            .ToList();
        selfUses.Should().BeEmpty(
            $"{relativePath}: {seedMember} must be DECLARED only — never consumed by production code " +
            "in its own file (a sibling signer would let the client forge, not just verify):" +
            Environment.NewLine + string.Join(Environment.NewLine, selfUses));

        // (b2) NO OTHER production file may reference the seed via its QUALIFIED name
        // (`ClassName.DevPrivateSeed`) in code.  Qualified, so an identically-named seed
        // in a different class is not mis-attributed.  Tests legitimately use it (to sign
        // fixtures) and live OUTSIDE src/, so they are not scanned here.
        var qualified = $"{className}.{seedMember}";
        var prodReferers = ProductionCsFiles()
            .Where(p => !PathEquals(p, full))
            .Where(p => CodeLinesReferencing(p, qualified).Any())
            .Select(Rel)
            .ToList();
        prodReferers.Should().BeEmpty(
            $"the dev/test {qualified} must NEVER be referenced by production code outside its own file " +
            "(only by tests) — a prod reference would mean the client can mint/forge, not just verify:" +
            Environment.NewLine + string.Join(Environment.NewLine, prodReferers));
    }

    [Fact]
    [Trait("Category", "SecurityGate")]
    public void EmbeddedKeyFilesExposePublicMaterialBanner()
    {
        // Every client/service root-of-trust file must carry the loud "EMBEDDED PUBLIC
        // MATERIAL only" banner — its presence is the human-readable contract the other
        // checks enforce. This includes the MANIFEST key (the moat's integrity root).
        foreach (var rel in new[]
        {
            "src/DrDxt.Infrastructure/Updates/UpdateDevKeys.cs",
            "src/DrDxt.Reconciler/Manifest/ManifestDevKeys.cs",
        })
        {
            var full = Path.Combine(RepoRoot(), rel.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(full).Should().BeTrue();
            File.ReadAllText(full).Should().Contain(
                "EMBEDDED PUBLIC MATERIAL ONLY",
                $"{rel} must keep the public-only banner that states no secret may live here");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  (4a) MANIFEST KEY — no usable private seed in any config (M5 review #3/#4).
    //
    //  The manifest-signing key is the moat's integrity root. Unlike the licence/update
    //  dev seeds (which legitimately keep a banner-fenced in-file seed), the manifest dev
    //  seed must not ship in DrDxt.Reconciler.dll in EITHER Debug or Release — it lives
    //  only in the test assemblies (DrDxt.TestSupport.ManifestDevTestKeys, tests/Shared).
    //  So: ManifestDevKeys.cs declares NO private seed member, and the known dev seed byte
    //  pattern appears NOWHERE under src/.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    [Trait("Category", "SecurityGate")]
    public void ManifestDevKeys_ShipsNoUsablePrivateSeedInAnyConfig()
    {
        var manifestKeys = Path.Combine(RepoRoot(),
            "src", "DrDxt.Reconciler", "Manifest", "ManifestDevKeys.cs");
        File.Exists(manifestKeys).Should().BeTrue();
        var source = File.ReadAllText(manifestKeys);

        // No private-seed MEMBER is declared (a `byte[] …Seed =>/=` would be a shipped key).
        Regex.IsMatch(source, @"\bbyte\s*\[\s*\]\s*\w*Seed\b")
            .Should().BeFalse(
                "ManifestDevKeys.cs (the moat's root of trust) must declare NO private seed " +
                "member — the dev/test seed lives only in the test assemblies");

        // The known dev seed byte pattern must not appear ANYWHERE in the shipping tree
        // (a #if DEBUG fence would still embed the bytes in a Debug binary; this is stricter).
        var offenders = ProductionCsFiles()
            .Where(p => NormalizeForByteScan(File.ReadAllText(p)).Contains(KnownManifestDevSeedPattern))
            .Select(Rel)
            .ToList();
        offenders.Should().BeEmpty(
            "the manifest dev/test PRIVATE seed must not be embedded in any src/ binary " +
            "(Debug or Release) — it belongs only in the test assemblies:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  (4b) embedded-dev-key fallback is #if DEBUG-fenced in both wirings (review #4).
    //
    //  The host (HostServiceCollectionExtensions) and the App (AppReconcilerSeams) both
    //  fall back to the embedded dev PUBLIC key (ManifestDevKeys.CreateVerifier()) when no
    //  production key is provisioned. That fallback must compile only under #if DEBUG, so a
    //  RELEASE build fails closed and never trusts the dev key. This gate fails if the
    //  fallback is moved OUTSIDE a #if DEBUG region in either wiring (the empirical
    //  fail-open the reviewer reproduced).
    //
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [Trait("Category", "SecurityGate")]
    [InlineData("src/DrDxt.ReconcilerHost/HostServiceCollectionExtensions.cs", "ManifestDevKeys.CreateVerifier(")]
    [InlineData("src/DrDxt.App/AppReconcilerSeams.cs", "ManifestDevKeys.CreateVerifier(")]
    public void EmbeddedDevKeyVerifierFallback_IsDebugFenced(string relativePath, string fallbackToken)
    {
        var full = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(full).Should().BeTrue($"{relativePath} must exist");
        var lines = File.ReadAllLines(full);

        var uses = new List<int>();
        var unfenced = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            // Only CODE uses count (a comment naming the method is not a fallback).
            if (!StripLineComment(lines[i]).Contains(fallbackToken))
                continue;
            uses.Add(i + 1);
            if (!IsInDebugOnlyRegion(lines, i))
                unfenced.Add($"{relativePath}:{i + 1}  {lines[i].Trim()}");
        }

        uses.Should().NotBeEmpty(
            $"{relativePath} is expected to WIRE the embedded-dev-key verifier fallback — a " +
            "0-match here would be a silent gate (the fallback was renamed/removed without notice)");
        unfenced.Should().BeEmpty(
            "the embedded dev-key verifier fallback must compile ONLY under #if DEBUG so a " +
            "RELEASE build fails closed (M5 key-custody invariant):" +
            Environment.NewLine + string.Join(Environment.NewLine, unfenced));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  (4c) 11/09/2026 — la graine de signature du manifeste en variable d'environnement.
    //
    //  L'App signait avec une graine lue dans DRDXT_MANIFEST_SIGNING_SEED. Le manifeste est
    //  desormais signe par le helper eleve avec la cle propre a la machine ; la graine
    //  d'environnement n'a plus aucun usage, ni en production ni en developpement, et elle a ete
    //  retiree du produit. Plus strict qu'un #if DEBUG : aucune ligne de CODE de src/ ne peut la
    //  nommer, dans aucune configuration (les commentaires, qui racontent son histoire, sont
    //  retires avant la recherche). La reintroduire, meme sous DEBUG, fait rougir ce test.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    [Trait("Category", "SecurityGate")]
    public void The_manifest_signing_seed_env_var_is_read_by_no_production_code_in_any_config()
    {
        const string graine = "DRDXT_MANIFEST_SIGNING_SEED";
        var offenders = new List<string>();
        foreach (var file in ProductionCsFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (StripLineComment(lines[i]).Contains(graine, StringComparison.Ordinal))
                    offenders.Add($"{Rel(file)}:{i + 1}  {lines[i].Trim()}");
        }

        offenders.Should().BeEmpty(
            "the manifest is signed by the elevated helper with the machine's own key; no src/ code may " +
            "read a signing seed from the environment, in Debug or Release:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // The first 8 bytes of the manifest dev/test PRIVATE seed (distinctive), normalized for
    // a whitespace-insensitive scan. Kept here (in a TEST that is never shipped) purely to
    // detect a re-introduction of the seed into src/.
    private const string KnownManifestDevSeedPattern =
        "0x31,0x5d,0xed,0x95,0x60,0xb6,0xa4,0x94";

    private static string NormalizeForByteScan(string s)
        => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    // True iff the given (0-based) line sits inside an ACTIVE `#if DEBUG` true-branch. A
    // small preprocessor walk: tracks #if/#elif/#else/#endif nesting and whether the
    // current frame is the DEBUG guard's compiled branch. Handles the simple, un-nested
    // `#if DEBUG … #else … #endif` shape the two wirings use.
    private static bool IsInDebugOnlyRegion(IReadOnlyList<string> lines, int index)
    {
        var stack = new Stack<(bool IsDebug, bool Active)>();
        for (var i = 0; i <= index && i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("#if ", StringComparison.Ordinal))
                stack.Push((t.Substring(4).Trim() == "DEBUG", Active: true));
            else if (t.StartsWith("#elif", StringComparison.Ordinal))
            {
                if (stack.Count > 0) { stack.Pop(); stack.Push((false, true)); }
            }
            else if (t == "#else")
            {
                if (stack.Count > 0) { var top = stack.Pop(); stack.Push((top.IsDebug, !top.Active)); }
            }
            else if (t.StartsWith("#endif", StringComparison.Ordinal))
            {
                if (stack.Count > 0) stack.Pop();
            }
        }

        return stack.Any(f => f.IsDebug && f.Active);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Rel(string fullPath)
    {
        var root = RepoRoot();
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? fullPath.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/')
            : fullPath;
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(
            Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // The CODE lines (comments / XML-docs stripped) of a file that reference the given
    // token as a whole word.  Comment-stripping is what makes the `<see cref="…"/>` doc
    // mentions and the prose banners — which legitimately NAME the seed — not count.
    private static IReadOnlyList<string> CodeLinesReferencing(string file, string token)
    {
        var word = new Regex($@"(?<![A-Za-z0-9_]){Regex.Escape(token)}(?![A-Za-z0-9_])",
            RegexOptions.CultureInvariant);
        return File.ReadLines(file)
            .Select(StripLineComment)
            .Where(code => word.IsMatch(code))
            .Select(code => code.Trim())
            .ToList();
    }

    // True iff a (comment-stripped) line is the seed's DECLARATION, e.g.
    // `public static byte[] DevPrivateSeed =>` — the definition, not a forging use.
    private static bool IsSeedDeclaration(string codeLine, string seedMember)
        => Regex.IsMatch(
            codeLine,
            $@"\bbyte\s*\[\s*\]\s*{Regex.Escape(seedMember)}\b",
            RegexOptions.CultureInvariant);

    // Remove a trailing // line comment, but not a // that sits inside a string literal.
    private static string StripLineComment(string line)
    {
        var inString = false;
        var inChar = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (inChar)
            {
                if (c == '\\') { i++; continue; }
                if (c == '\'') inChar = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '\'': inChar = true; break;
                case '/' when i + 1 < line.Length && line[i + 1] == '/':
                    return line.Substring(0, i);
            }
        }

        return line;
    }

    // Strip both // line comments and /* … */ block comments from a code fragment, while
    // preserving string/char literals (so a "//" inside a string survives).  Used to make
    // the catch-body success scan blind to prose like "// never a pass".
    private static string StripComments(string code)
    {
        var sb = new StringBuilder(code.Length);
        var inString = false;
        var inChar = false;
        var inLine = false;
        var inBlock = false;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            var n = i + 1 < code.Length ? code[i + 1] : '\0';

            if (inLine)
            {
                if (c == '\n') { inLine = false; sb.Append(c); }
                continue;
            }
            if (inBlock)
            {
                if (c == '*' && n == '/') { inBlock = false; i++; }
                continue;
            }
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < code.Length) { sb.Append(n); i++; }
                else if (c == '"') inString = false;
                continue;
            }
            if (inChar)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < code.Length) { sb.Append(n); i++; }
                else if (c == '\'') inChar = false;
                continue;
            }

            if (c == '/' && n == '/') { inLine = true; continue; }
            if (c == '/' && n == '*') { inBlock = true; i++; continue; }
            if (c == '"') { inString = true; sb.Append(c); continue; }
            if (c == '\'') { inChar = true; sb.Append(c); continue; }
            sb.Append(c);
        }

        return sb.ToString();
    }

    private readonly record struct CatchBody(int Offset, string Body);

    // Find each `catch` keyword and extract the brace-balanced body of its block.
    // Skips the word "catch" if it appears inside a string/comment (defensive — none do
    // today).  The exception filter `catch (T) when (…)` is handled: we start brace
    // counting at the first '{' after the catch keyword.
    private static IEnumerable<CatchBody> EnumerateCatchBodies(string source)
    {
        foreach (Match m in Regex.Matches(
            source, @"\bcatch\b", RegexOptions.CultureInvariant))
        {
            // Find the opening brace of this catch's block.
            var open = source.IndexOf('{', m.Index);
            if (open < 0) continue;

            // Brace-balance to the matching close, skipping braces inside strings/chars/comments.
            var depth = 0;
            var inString = false;
            var inChar = false;
            var inLine = false;
            var inBlock = false;
            var close = -1;
            for (var i = open; i < source.Length; i++)
            {
                var c = source[i];
                var nx = i + 1 < source.Length ? source[i + 1] : '\0';

                if (inLine) { if (c == '\n') inLine = false; continue; }
                if (inBlock) { if (c == '*' && nx == '/') { inBlock = false; i++; } continue; }
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (inChar)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '\'') inChar = false;
                    continue;
                }

                switch (c)
                {
                    case '/' when nx == '/': inLine = true; break;
                    case '/' when nx == '*': inBlock = true; i++; break;
                    case '"': inString = true; break;
                    case '\'': inChar = true; break;
                    case '{': depth++; break;
                    case '}':
                        depth--;
                        if (depth == 0) { close = i; }
                        break;
                }

                if (close >= 0) break;
            }

            if (close > open)
                yield return new CatchBody(open, source.Substring(open + 1, close - open - 1));
        }
    }
}
