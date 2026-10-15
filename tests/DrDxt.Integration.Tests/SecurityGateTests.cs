using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Porte de sécurité CI. Un filet statique sur le code de licence et de mise à jour
// (signature, garde, confiance). Ce ne sont pas des tests de comportement : ils balaient
// l'arbre source et font échouer la génération si une régression de sécurité entre :
//
//   (1) anti-secret       aucun jeton admin Keygen, secret HMAC de webhook, jeton bearer ou
//                         long blob en forme de secret ne peut vivre dans src/DrDxt.* (le secret
//                         serveur ne vit que dans server/, par l'environnement, jamais commité).
//   (2) anti-ouverture    aucun `catch` de la couche de confiance licence/mise à jour ne peut
//                         rendre un succès (Result.Ok / true / LicenseStatus.Licensed). Une
//                         signature cassée ne doit jamais devenir un laissez-passer.
//   (3) clés publiques seules  EmbeddedKeys / UpdateDevKeys n'exposent que du matériel public ;
//                         la graine privée de dev/test doit rester encadrée par sa bannière et
//                         n'est jamais référencée par du code de production (seulement par des tests).
//
// Ces tests portent [Trait("Category","SecurityGate")] pour que la CI les sélectionne exactement
// (même motif que ReconcileGate dans .github/workflows/ci.yml). Un filtre à 0 correspondance
// serait une porte muette, donc le trait est posé sur de vrais tests présents ici.
//
// Résolution de la racine du dépôt : chaque test remonte depuis l'emplacement de l'assembly
// de test (AppContext.BaseDirectory) jusqu'au dossier qui contient DrDXT.sln. Robuste à
// l'imbrication bin/Debug|Release/net8.0-windows et à tout répertoire courant
// (CI, IDE, dotnet test).
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SecurityGateTests
{
    // ── résolution de la racine du dépôt (remonter jusqu'au dossier qui tient DrDXT.sln) ────
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

    // Chaque .cs de production sous src/DrDxt.* (obj/bin générés exclus).
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
    //  (1) anti-secret : aucun identifiant réservé au serveur ne peut apparaître dans l'arbre client.
    //
    //  Méthode : balayer chaque ligne .cs de production. Les commentaires de ligne (// …) et le
    //  corps des bannières // sont retirés d'abord, parce que le dépôt documente légitimement ces
    //  jetons interdits en prose (« le jeton admin et le secret HMAC du webhook sont serveur
    //  seulement »). Seuls les jetons qui survivent au retrait des commentaires sont signalés,
    //  c'est-à-dire ceux dans du vrai code ou des littéraux. Zéro faux positif sur l'arbre
    //  courant (vérifié : les seules correspondances sont dans des commentaires).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    [Trait("Category", "SecurityGate")]
    public void NoServerSecretIsCommittedInTheClientTree()
    {
        // Sous-chaînes de jetons de code interdites (insensibles à la casse), la forme que
        // prendrait un identifiant serveur fuité. Non cherchées dans les commentaires.
        var forbidden = new[]
        {
            "Authorization: Bearer", // un en-tête bearer/admin voudrait dire que le client tient un jeton serveur
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

                // Un long blob en forme de secret affecté à un membre *secret*/*token*/*apiKey*/
                // *password*/*hmac* est un secret fuité. Les clés publiques Ed25519 (tableaux
                // d'octets 0x..) et les épingles SPKI publiques ne sont pas attrapées : ni nommées
                // comme un secret, ni littéral base64/hex contigu d'un champ secret.
                if (SecretNamedLongLiteral.IsMatch(code))
                    offenders.Add($"{Rel(file)}:{i + 1}  [secret-named long literal]  {lines[i].Trim()}");
            }
        }

        offenders.Should().BeEmpty(
            "no Keygen admin token, webhook HMAC secret, bearer token, or secret-named blob " +
            "may live in src/DrDxt.* — the server secret lives ONLY in server/ via env (07-SECURITY §4.4):" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // Un identifiant qui contient un mot secret (secret / token / admin / bearer /
    // credential / apiKey / password / passwd / hmac, n'importe où dans le nom, par exemple
    // WebhookHmacSecret, AdminKey, ServerToken) affecté (= ou :) d'un littéral chaîne
    // base64/hex de 20 caractères ou plus. C'est le cas « long blob dans un champ
    // *secret*/*token*/*apiKey* ».
    //
    // Mesuré en plantant le même blob base64 de 28 caractères dans EmbeddedKeys.cs :
    //   WebhookHmacSecret  = "..."   → attrapé      AdminKey         = "..." → manqué
    //   "Authorization: Bearer ..."  → attrapé      ServerToken      = "..." → manqué
    //   KeygenAdminKey     = "..."   → attrapé      BearerCredential = "..." → manqué
    // La liste de mots portait secret/api_key/password/passwd/hmac alors que l'intention
    // visait « *secret*/*token*/*apiKey* » : token/admin/bearer/credential manquaient, donc un
    // jeton admin Keygen collé dans un champ nommé AdminKey partait en silence. Ils y sont.
    //
    // Plafond, dit exprès : le même blob coupé en deux littéraux
    // ("aGVsbG8" + "d29ybGQ") passe toujours, et passera toujours ; c'est une évasion délibérée,
    // pas le collage accidentel que cette porte existe pour arrêter. Aucun balayage de texte ne le ferme.
    //
    //   * l'identifiant est [A-Za-z0-9_]* contenant l'un des mots (insensible à la casse) ;
    //   * « key » seul n'est pas un déclencheur à dessein (trop large : « publicKey », « KeyBlob »),
    //     seul le composé « apikey »/« api_key » l'est. Les épingles SPKI sont dans un initialiseur
    //     de tableau sans `identifiant =` devant, donc elles restent non signalées.
    private static readonly Regex SecretNamedLongLiteral = new(
        @"\b[A-Za-z0-9_]*(secret|token|admin|bearer|credential|api_?key|password|passwd|hmac)[A-Za-z0-9_]*""?\s*[=:]\s*""[A-Za-z0-9+/]{20,}={0,2}""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // ─────────────────────────────────────────────────────────────────────────
    //  (2) anti-ouverture : aucun catch de la couche de confiance ne rend un succès.
    //
    //  Fichiers gardés : les quatre qui décident la confiance (signature/licence/mise à jour/porte).
    //  Méthode : pour chaque mot-clé `catch`, extraire le corps de son bloc par équilibrage
    //  d'accolades ( `catch (...) { … }` ), puis retirer les commentaires du corps, puis vérifier
    //  que le corps ne contient aucun motif de retour de succès. Comme on ne regarde qu'à
    //  l'intérieur des corps de catch, les nombreux `return LicenseStatus.Licensed` /
    //  `return Result.Ok()` légitimes hors catch (le chemin heureux) ne sont jamais signalés :
    //  c'est le faux positif à éviter, et l'équilibrage d'accolades l'évite précisément.
    //
    //  Zéro faux positif prouvé sur le code courant (déjà à échec propre) : chaque catch de
    //  ces fichiers rend Result.Fail / Result<…>.Fail / null.
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

            // N'importe lequel de ces motifs dans un corps de catch est une ouverture : un chemin
            // levé ou en faute qui finit par accorder la confiance au lieu de la refuser.
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

    // Succès mentionné n'importe où dans un corps de catch, pas seulement `return <succès>`.
    //
    // Mesuré en mutant le catch de Ed25519LicenseVerifier.Verify :
    //   `return Result.Ok();`                                   → attrapé (rouge)
    //   `var passe = Result.Ok(); return passe;`                → non attrapé (vert)
    //   `return signature.Length > 0 ? Result.Ok() : Fail(...);` → non attrapé (vert)
    // L'ancien motif s'ancrait sur `return\s+`, donc deux façons ordinaires d'écrire la même
    // ouverture traversaient une porte dont le nom promet que c'est impossible.
    // Retirer l'ancre attrape les trois. Zéro faux positif vérifié : aucun des
    // 7 corps de catch des fichiers gardés ne mentionne Ok / Licensed / true.
    //
    // Plafond, dit exprès : c'est un balayage de texte, pas un compilateur. Il ne voit toujours
    // pas un succès routé par une aide (`return Grant();`), un champ posé dans le catch et rendu
    // après, ni un `catch { }` qui avale et retombe sur le chemin heureux. Ceux-là demandent
    // un test de comportement (injecter la faute, vérifier Fail), pas une regex plus large.
    private static readonly Regex SuccessReturnInCatch = new(
        @"(Result(<[^>]+>)?\s*\.\s*Ok)\b" +
        @"|(LicenseStatus\s*\.\s*Licensed)\b" +
        @"|\bOk\s*\(" +
        @"|return\s+true\s*;",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // ─────────────────────────────────────────────────────────────────────────
    //  (3) fichiers de clés publiques seules : UpdateDevKeys (graine en fichier encadrée par bannière).
    //
    //  Invariant : les fichiers de racine de confiance du client n'exposent que du matériel public
    //  consommable par le code de production. Pour UpdateDevKeys, une graine privée de dev/test n'est tolérée que si :
    //    (a) elle est clairement encadrée par une bannière dev/test, et
    //    (b) elle n'est jamais référencée par du code de production (seulement par des tests).
    //  La porte échoue si une graine privée perd sa bannière dev/test, ou si un fichier de
    //  production référence le membre graine. EmbeddedKeys (licence) et ManifestDevKeys sont
    //  tenus à la barre plus stricte « aucune graine dans aucune configuration » (voir (4a) / (4a-bis)).
    // ─────────────────────────────────────────────────────────────────────────
    [Theory]
    [Trait("Category", "SecurityGate")]
    [InlineData("src/DrDxt.Infrastructure/Updates/UpdateDevKeys.cs", "UpdateDevKeys", "DevPrivateSeed")]
    // La clé du manifeste (ManifestDevKeys) n'est pas dans cette liste : elle est tenue à une
    // barre plus stricte qu'une graine en fichier encadrée : sa graine privée n'est livrée nulle
    // part dans src/ (Debug ou Release), elle ne vit que dans les assemblys de test. Voir
    // ManifestDevKeys_ShipsNoUsablePrivateSeedInAnyConfig ci-dessous. Seul UpdateDevKeys
    // garde encore une graine de dev en fichier encadrée par bannière.
    public void DevPrivateSeedIsBannerFencedAndNeverUsedByProductionCode(
        string relativePath, string className, string seedMember)
    {
        var full = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(full).Should().BeTrue();
        var source = File.ReadAllText(full);

        // (a) Le membre graine est présent et encadré par une bannière dev/test. On exige le
        // marqueur dev/test explicite près du fichier (la bannière bien visible « DEV / TEST PRIVATE
        // SEED ») : son absence veut dire que quelqu'un a retiré l'avertissement.
        source.Should().Contain(seedMember, $"{relativePath} declares the dev/test seed");
        source.Should().MatchRegex(
            @"DEV\s*/\s*TEST",
            $"{relativePath}: the private seed MUST keep its DEV/TEST banner — a release ships public material only");
        source.Should().Contain("NEVER READ AT RUNTIME",
            $"{relativePath}: the private-seed banner must state it is never read by production code");

        // (b1) Dans le fichier déclarant, la graine n'apparaît que comme sa déclaration
        // (`… byte[] DevPrivateSeed =>`) ; elle n'est jamais consommée par une méthode de
        // production voisine (ce qui ferait du client un faussaire, par exemple un
        // `CreateDevSigner() => …FromSeed(DevPrivateSeed)`). Les commentaires et docs XML qui
        // ne font que nommer la graine sont retirés d'abord, donc seul le vrai code compte.
        var selfUses = CodeLinesReferencing(full, seedMember)
            .Where(l => !IsSeedDeclaration(l, seedMember))
            .ToList();
        selfUses.Should().BeEmpty(
            $"{relativePath}: {seedMember} must be DECLARED only — never consumed by production code " +
            "in its own file (a sibling signer would let the client forge, not just verify):" +
            Environment.NewLine + string.Join(Environment.NewLine, selfUses));

        // (b2) Aucun autre fichier de production ne peut référencer la graine par son nom
        // qualifié (`ClassName.DevPrivateSeed`) dans du code. Qualifié, pour qu'une graine de même
        // nom dans une autre classe ne soit pas mal attribuée. Les tests s'en servent légitimement
        // (pour signer des fixtures) et vivent hors de src/, donc ils ne sont pas balayés ici.
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
        // Chaque fichier de racine de confiance du client ou du service doit porter la bannière
        // bien visible « EMBEDDED PUBLIC MATERIAL only » : sa présence est le contrat lisible que
        // les autres contrôles imposent. Clé du manifeste comprise (la racine d'intégrité de la douve).
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
    //  (4a) Clé du manifeste : aucune graine privée utilisable dans aucune configuration.
    //
    //  La clé de signature du manifeste est la racine d'intégrité de la douve. Contrairement aux
    //  graines de dev de licence et de mise à jour (qui gardent légitimement une graine en fichier
    //  encadrée), la graine de dev du manifeste ne doit être livrée dans DrDxt.Reconciler.dll ni
    //  en Debug ni en Release ; elle ne vit que dans les assemblys de test
    //  (DrDxt.TestSupport.ManifestDevTestKeys, tests/Shared). Donc : ManifestDevKeys.cs ne déclare
    //  aucun membre graine privée, et le motif d'octets de la graine de dev connue n'apparaît nulle part sous src/.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    [Trait("Category", "SecurityGate")]
    public void ManifestDevKeys_ShipsNoUsablePrivateSeedInAnyConfig()
    {
        var manifestKeys = Path.Combine(RepoRoot(),
            "src", "DrDxt.Reconciler", "Manifest", "ManifestDevKeys.cs");
        File.Exists(manifestKeys).Should().BeTrue();
        var source = File.ReadAllText(manifestKeys);

        // Aucun membre graine privée n'est déclaré (un `byte[] …Seed =>/=` serait une clé livrée).
        Regex.IsMatch(source, @"\bbyte\s*\[\s*\]\s*\w*Seed\b")
            .Should().BeFalse(
                "ManifestDevKeys.cs (the moat's root of trust) must declare NO private seed " +
                "member — the dev/test seed lives only in the test assemblies");

        // Le motif d'octets de la graine de dev connue ne doit apparaître nulle part dans l'arbre livré
        // (une barrière #if DEBUG embarquerait encore les octets dans un binaire Debug ; ceci est plus strict).
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
    //  (4b) le repli sur la clé de dev embarquée est sous #if DEBUG dans les deux câblages.
    //
    //  L'hôte (HostServiceCollectionExtensions) et l'App (AppReconcilerSeams) se replient tous
    //  deux sur la clé publique de dev embarquée (ManifestDevKeys.CreateVerifier()) quand aucune
    //  clé de production n'est provisionnée. Ce repli ne doit compiler que sous #if DEBUG, pour
    //  qu'une version Release se ferme et ne fasse jamais confiance à la clé de dev. Cette porte
    //  échoue si le repli sort d'une région #if DEBUG dans l'un ou l'autre câblage (l'ouverture
    //  empirique reproduite sur banc).
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
            // Seules les utilisations dans le code comptent (un commentaire qui nomme la méthode n'est pas un repli).
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

    // Les 8 premiers octets de la graine privée de dev/test du manifeste (distinctifs), normalisés
    // pour un balayage insensible aux espaces. Gardés ici (dans un test jamais livré) uniquement
    // pour détecter une réintroduction de la graine dans src/.
    private const string KnownManifestDevSeedPattern =
        "0x31,0x5d,0xed,0x95,0x60,0xb6,0xa4,0x94";

    private static string NormalizeForByteScan(string s)
        => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    // Vrai si la ligne donnée (base 0) est dans la branche vraie active d'un `#if DEBUG`. Une
    // petite marche de préprocesseur : suit l'imbrication #if/#elif/#else/#endif et si le
    // cadre courant est la branche compilée de la garde DEBUG. Gère la forme simple, non
    // imbriquée, `#if DEBUG … #else … #endif` que les deux câblages utilisent.
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

    // Les lignes de code (commentaires et docs XML retirés) d'un fichier qui référencent le
    // jeton donné comme mot entier. Le retrait des commentaires fait que les mentions
    // `<see cref="…"/>` et les bannières en prose, qui nomment légitimement la graine, ne comptent pas.
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

    // Vrai si une ligne (sans commentaires) est la déclaration de la graine, par exemple
    // `public static byte[] DevPrivateSeed =>` : la définition, pas une utilisation pour forger.
    private static bool IsSeedDeclaration(string codeLine, string seedMember)
        => Regex.IsMatch(
            codeLine,
            $@"\bbyte\s*\[\s*\]\s*{Regex.Escape(seedMember)}\b",
            RegexOptions.CultureInvariant);

    // Retire un commentaire de ligne // final, mais pas un // qui se trouve dans un littéral chaîne.
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

    // Retire les commentaires de ligne // et de bloc /* … */ d'un fragment de code, en
    // préservant les littéraux chaîne et caractère (un « // » dans une chaîne survit). Sert à
    // rendre le balayage des corps de catch aveugle à la prose comme « // jamais un laissez-passer ».
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

    // Trouve chaque mot-clé `catch` et extrait le corps équilibré en accolades de son bloc.
    // Saute le mot « catch » s'il apparaît dans une chaîne ou un commentaire (défensif ; aucun
    // ne le fait). Le filtre d'exception `catch (T) when (…)` est géré : le comptage d'accolades
    // commence à la première '{' après le mot-clé catch.
    private static IEnumerable<CatchBody> EnumerateCatchBodies(string source)
    {
        foreach (Match m in Regex.Matches(
            source, @"\bcatch\b", RegexOptions.CultureInvariant))
        {
            // Trouver l'accolade ouvrante du bloc de ce catch.
            var open = source.IndexOf('{', m.Index);
            if (open < 0) continue;

            // Équilibrer jusqu'à l'accolade fermante correspondante, en sautant celles des chaînes, caractères et commentaires.
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
