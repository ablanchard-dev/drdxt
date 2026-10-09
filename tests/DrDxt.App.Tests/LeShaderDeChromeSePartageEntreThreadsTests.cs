using System.Reflection;
using System.Windows.Media.Effects;
using DrDxt.App.Effets;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// crash.log du 18/09/2026 01:18 : « Le thread appelant ne peut pas accéder à cet objet parce qu'un
/// autre thread en est propriétaire », levé par PixelShader.get_ShaderRenderMode. Le shader du chrome
/// liquide est partagé (static) par toutes les instances ; non gelé, il appartenait au premier thread
/// qui l'avait créé. Gelé, il se lit depuis n'importe quel thread.
/// </summary>
public sealed class LeShaderDeChromeSePartageEntreThreadsTests
{
    private static PixelShader ShaderPartage() =>
        (PixelShader)typeof(ChromeLiquideEffect)
            .GetField("Shader", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    [Fact]
    public void Le_shader_partage_est_gele_et_se_lit_depuis_un_autre_thread()
    {
        // Premier thread : l'application et une instance, comme au démarrage.
        var shader = StaApplication.Run(() => { _ = new ChromeLiquideEffect(); return ShaderPartage(); });
        shader.IsFrozen.Should().BeTrue();

        // Second thread : la lecture qui plantait.
        Exception? leve = null;
        var autre = new Thread(() =>
        {
            try { _ = shader.ShaderRenderMode; }
            catch (Exception ex) { leve = ex; }
        });
        autre.SetApartmentState(ApartmentState.STA);
        autre.Start();
        autre.Join();

        leve.Should().BeNull("un Freezable gelé se lit depuis tout thread");
    }
}
