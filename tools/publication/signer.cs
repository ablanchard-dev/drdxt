#:project ../../src/DrDxt.Infrastructure/DrDxt.Infrastructure.csproj
#:property TargetFramework=net10.0-windows7.0
#:package System.Security.Cryptography.ProtectedData@10.0.12
// SEC5 (09/10/2026) : l'outil de release des mises à jour. Il tourne sur la machine du mainteneur, jamais dans l'App.
//
//   dotnet run tools/publication/signer.cs -- cle
//       Affiche la clé publique ; avec --creer, crée la paire Ed25519 (une fois, jamais écrasée). La graine privée est
//       chiffrée pour le compte Windows courant ET par une phrase demandée à chaque usage, dans
//       %LOCALAPPDATA%\DrDXT-release\mise-a-jour.seed ; elle ne quitte pas ce PC et n'entre jamais dans le dépôt. La clé
//       PUBLIQUE affichée va dans UpdateTrust.ProductionPublicKey : l'App ne reconnaît que celle-là.
//
//   dotnet run tools/publication/signer.cs -- manifeste <canal App|Profiles> <version> <url du paquet> <fichier du paquet> <sortie>
//       Calcule le SHA-256 du paquet, signe la forme canonique (celle que l'App vérifie : UpdateVerifier.CanonicalBytes),
//       écrit le manifeste JSON, puis le relit avec la clé publique : « signature vérifiée » ou rien n'est écrit.
//
// Ce que l'App attend (UpdateComposition.ManifestPath) : <racine>/updates/app/manifest et <racine>/updates/profiles/manifest.
// Avec GitHub Pages du dépôt public comme racine, ces deux fichiers sont des fichiers statiques du site ; le paquet lui-même
// est un asset de GitHub Releases (son URL est dans le manifeste, son empreinte aussi).
using System.Security.Cryptography;
using System.Text.Json;
using DrDxt.Infrastructure.Updates;
using NSec.Cryptography;

var dossier = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DrDXT-release");
var cheminGraine = Path.Combine(dossier, "mise-a-jour.seed");

if (args.Length == 0) { Usage(); return 2; }
try
{
    switch (args[0])
    {
        case "cle": return Cle(args.Contains("--creer"));
        case "manifeste" when args.Length == 6: return Manifeste(args[1], args[2], args[3], args[4], args[5]);
        default: Usage(); return 2;
    }
}
catch (Exception ex) { Console.Error.WriteLine("refus : " + ex.Message); return 1; }

// Revue du 09/10/2026 : DPAPI seul laissait tout programme du compte lire la graine. La phrase, demandée à chaque usage et
// jamais écrite, entre comme entropie : sans elle, le fichier ne se déchiffre pas. Elle n'a pas de rattrapage. Limites
// acceptées : la phrase passe par une chaîne .NET non effaçable le temps de la dérivation, et le sel est fixe (la
// dérivation sert d'entropie DPAPI, pas de clé : une même phrase sur deux PC ne donne pas la même graine).
byte[] Entropie()
{
    Console.Error.Write("phrase de la clé de release (rien ne s'affiche) : ");
    var phrase = new System.Text.StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (phrase.Length > 0) phrase.Length--; continue; }
        if (k.KeyChar != '\0') phrase.Append(k.KeyChar);
    }
    Console.Error.WriteLine();
    if (phrase.Length < 12) throw new InvalidOperationException("phrase trop courte (12 caractères au moins)");
    return Rfc2898DeriveBytes.Pbkdf2(phrase.ToString(), "DrDXT clé de release"u8.ToArray(), 300_000, HashAlgorithmName.SHA256, 32);
}

int Cle(bool creer)
{
    Directory.CreateDirectory(dossier);
    if (!File.Exists(cheminGraine))
    {
        if (!creer) { Console.Error.WriteLine($"aucune graine dans {cheminGraine} ; `cle --creer` en crée une (geste du mainteneur, une fois)."); return 1; }
        // Saisie double à la création : une faute de frappe laisserait une graine que personne ne peut plus déchiffrer.
        var entropie = Entropie();
        var confirmation = Entropie();
        if (!entropie.AsSpan().SequenceEqual(confirmation)) { Console.Error.WriteLine("les deux saisies diffèrent : rien n'est créé."); return 1; }
        CryptographicOperations.ZeroMemory(confirmation);
        using var cle = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var graine = cle.Export(KeyBlobFormat.RawPrivateKey);
        File.WriteAllBytes(cheminGraine, ProtectedData.Protect(graine, entropie, DataProtectionScope.CurrentUser));
        CryptographicOperations.ZeroMemory(graine); CryptographicOperations.ZeroMemory(entropie);
        Console.WriteLine($"graine créée, chiffrée pour ce compte ET par la phrase : {cheminGraine}");
        Console.WriteLine("à coller dans UpdateTrust.ProductionPublicKey, puis livrer : l'App ne reconnaît que cette clé.");
    }
    else if (creer) { Console.Error.WriteLine($"une graine existe déjà : {cheminGraine} ; elle n'est jamais écrasée (la retirer à la main pour recommencer)."); return 1; }
    using var k = Ouvrir();
    var publique = k.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    Console.WriteLine("clé publique (32 octets) :");
    Console.WriteLine("  hex    : " + Convert.ToHexStringLower(publique));
    Console.WriteLine("  C#     : " + string.Join(", ", publique.Select(b => $"0x{b:X2}")));
    return 0;
}

int Manifeste(string canal, string version, string url, string fichier, string sortie)
{
    if (!Enum.TryParse<UpdateChannel>(canal, true, out var channel)) { Console.Error.WriteLine("canal : App ou Profiles"); return 2; }
    if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("l'URL du paquet doit être en https"); return 2; }
    // L'App compare les versions nombre à nombre et lit 0 dans tout segment non numérique : « 1.3.0-rc1 » vaudrait « 1.3.0 ».
    // Chiffres ASCII seulement, neuf au plus par segment (int.TryParse rendrait 0 au-delà), fin de chaîne stricte.
    if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^[0-9]{1,9}(\.[0-9]{1,9}){1,3}\z")) { Console.Error.WriteLine("version : des nombres séparés par des points, rien d'autre (1.2.3)"); return 2; }
    var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fichier)));
    var sansSignature = new UpdateManifest(channel, version, url, sha, Signature: string.Empty);
    using var k = Ouvrir();
    var signature = SignatureAlgorithm.Ed25519.Sign(k, UpdateVerifier.CanonicalBytes(sansSignature));
    var manifeste = sansSignature with { Signature = Convert.ToBase64String(signature) };

    // Relu avec la clé EMBARQUÉE dans l'App (UpdateTrust), pas avec celle dérivée de la graine : une graine d'un autre PC ou
    // d'un autre compte produirait sinon un manifeste « vérifié » que toutes les App refuseraient (revue du 09/10).
    var embarquee = UpdateTrust.ProductionPublicKey ?? [];
    if (!embarquee.AsSpan().SequenceEqual(k.PublicKey.Export(KeyBlobFormat.RawPublicKey)))
    { Console.Error.WriteLine("la graine de ce PC ne correspond pas à la clé publique embarquée dans l'App : rien n'est écrit."); return 1; }
    var verif = UpdateTrust.CreateProductionVerifier();
    if (!verif.IsOk || verif.Value is null) { Console.Error.WriteLine("vérificateur : " + verif.Error?.Message); return 1; }
    var ok = verif.Value.VerifyManifest(manifeste);
    if (!ok.IsOk) { Console.Error.WriteLine("la signature ne se relit pas : " + ok.Error?.Message); return 1; }

    // Écrit à la main : les mêmes noms de champs que le manifeste (le canal par son nom), sans sérialiseur générique.
    var tampon = new System.Buffers.ArrayBufferWriter<byte>();
    using (var w = new Utf8JsonWriter(tampon, new JsonWriterOptions { Indented = true }))
    {
        w.WriteStartObject();
        w.WriteString("Channel", manifeste.Channel.ToString());
        w.WriteString("Version", manifeste.Version);
        w.WriteString("Url", manifeste.Url);
        w.WriteString("Sha256", manifeste.Sha256);
        w.WriteString("Signature", manifeste.Signature);
        w.WriteEndObject();
    }
    File.WriteAllBytes(sortie, tampon.WrittenSpan.ToArray());
    Console.WriteLine($"manifeste écrit : {sortie}");
    Console.WriteLine($"  {channel} {version}  sha256 {sha}");
    Console.WriteLine("  signature vérifiée avec la clé publique");
    return 0;
}

Key Ouvrir()
{
    var entropie = Entropie();
    byte[] graine;
    try { graine = ProtectedData.Unprotect(File.ReadAllBytes(cheminGraine), entropie, DataProtectionScope.CurrentUser); }
    catch (CryptographicException) { throw new InvalidOperationException("phrase refusée : la graine ne se déchiffre pas"); }
    finally { CryptographicOperations.ZeroMemory(entropie); }
    try { return Key.Import(SignatureAlgorithm.Ed25519, graine, KeyBlobFormat.RawPrivateKey, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.None }); }
    finally { CryptographicOperations.ZeroMemory(graine); }
}

void Usage()
{
    Console.WriteLine("signer.cs -- cle");
    Console.WriteLine("signer.cs -- manifeste <App|Profiles> <version> <url https du paquet> <fichier du paquet> <sortie.json>");
}
