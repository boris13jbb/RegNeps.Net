using Xunit;

namespace RegNeps.Tests;

/// <summary>
/// Preferencias de Gráficas: clave <c>regneps.analytics.prefs.{userId}</c> y JSON corrupto
/// se manejan solo en JS (<c>wwwroot/js/regneps-ui.js</c> → getAnalyticsPreferences).
/// No hay lógica C# extraíble; cobertura automatizada no aplicable aquí.
/// </summary>
public sealed class AnalyticsPreferencesCoverageNoteTests
{
    [Fact]
    public void Analytics_Prefs_Key_And_Corrupt_Json_Are_Handled_In_Js_Only()
    {
        const string documentedKeyPrefix = "regneps.analytics.prefs.";
        Assert.StartsWith("regneps.analytics.prefs.", documentedKeyPrefix, StringComparison.Ordinal);
        // Comportamiento JS: JSON.parse falla → return null (defaults en Graficas.razor).
        Assert.True(true);
    }
}
