namespace DrDxt.Server.Common;

// Stable error codes for the server boundaries. UI never sees these — they are operational.
public static class ServerErrorCodes
{
    public const string ManifestSigningUnconfigured = "MANIFEST_SIGNING_UNCONFIGURED";
    public const string ManifestSigningFailed = "MANIFEST_SIGNING_FAILED";
}
