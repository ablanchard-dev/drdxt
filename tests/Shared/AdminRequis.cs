using System.Security.Principal;
using Xunit;

namespace DrDxt.Tests.Shared;

/// <summary>
/// Un test qui éprouve ce que fait le helper élevé : il donne son journal aux Administrateurs et n'accepte qu'un tube
/// qui leur appartient. Un processus non élevé ne peut ni l'un ni l'autre, et le test échouerait sans rien dire du code.
/// Hors admin il est donc ignoré, avec la raison. En CI il ne l'est jamais : une CI qui perdrait l'élévation verrait
/// ces tests échouer, pas disparaître.
/// </summary>
internal static class AdminRequis
{
    public static readonly string? Raison = Environment.GetEnvironmentVariable("CI") == "true" || EstEleve()
        ? null
        : "needs an administrator terminal: the elevated helper gives its journal and its pipe to the Administrators. "
          + "Run the tests from an admin terminal to include it (CONTRIBUTING.md).";

    private static bool EstEleve()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

internal sealed class FactAdminAttribute : FactAttribute
{
    public FactAdminAttribute() => Skip = AdminRequis.Raison;
}

internal sealed class TheoryAdminAttribute : TheoryAttribute
{
    public TheoryAdminAttribute() => Skip = AdminRequis.Raison;
}
