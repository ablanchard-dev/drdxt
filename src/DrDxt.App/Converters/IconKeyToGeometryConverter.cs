using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DrDxt.App.Converters;

/// <summary>
/// Maps a stable icon key to a 24×24 vector <see cref="Geometry"/>. Self-contained line-style
/// glyphs (no icon-font dependency), drawn with a stroked Path in the view.
/// </summary>
[ValueConversion(typeof(string), typeof(Geometry))]
public sealed class IconKeyToGeometryConverter : IValueConverter
{
    // 24x24 viewbox, simple line-art paths.
    private static readonly Dictionary<string, Geometry> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // Hardware / components
        ["Cpu"] = Parse("M8 8 H16 V16 H8 Z M5 10 H8 M5 14 H8 M16 10 H19 M16 14 H19 M10 5 V8 M14 5 V8 M10 16 V19 M14 16 V19"),
        ["Gpu"] = Parse(
            "M3 8 H21 V16 H3 Z " +                                          // carte (corps)
            "M8 12 m-2.2 0 a2.2 2.2 0 1 0 4.4 0 a2.2 2.2 0 1 0 -4.4 0 " +   // ventilateur
            "M13 11 H18 M13 13.5 H18 " +                                    // sorties
            "M6 16 V19 M18 16 V19"),                                        // pattes (équerre)
        ["Ram"] = Parse("M4 8 H20 V15 H4 Z M7 15 V18 M11 15 V18 M15 15 V18 M19 15 V18 M7 10 V13 M11 10 V13 M15 10 V13"),
        ["Ssd"] = Parse("M5 4 H19 V20 H5 Z M8 8 H16 M8 11 H16 M8 14 H13 M15 16 a1 1 0 1 0 0.01 0"),
        ["Network"] = Parse("M12 3 V9 M12 9 L5 15 M12 9 L19 15 M12 9 V15 M12 3 m-2 0 a2 2 0 1 0 4 0 a2 2 0 1 0 -4 0 M5 17 m-2 0 a2 2 0 1 0 4 0 a2 2 0 1 0 -4 0 M12 17 m-2 0 a2 2 0 1 0 4 0 a2 2 0 1 0 -4 0 M19 17 m-2 0 a2 2 0 1 0 4 0 a2 2 0 1 0 -4 0"),
        ["Chip"] = Parse("M6 6 H18 V18 H6 Z M9 9 H15 V15 H9 Z M9 3 V6 M15 3 V6 M9 18 V21 M15 18 V21 M3 9 H6 M3 15 H6 M18 9 H21 M18 15 H21"),
        // 03/10/2026 (D5.2, revue) : le BIOS avait la « puce », presque le dessin du processeur. Il prend les
        // curseurs de réglage : c'est là que se règle le matériel.
        ["Firmware"] = Parse("M5 6 H19 M5 12 H19 M5 18 H19 M9 6 m-2 0 a2 2 0 1 0 4 0 a2 2 0 1 0 -4 0 M15 12 m-2 0 a2 2 0 1 0 4 0 a2 2 0 1 0 -4 0 M8 18 m-2 0 a2 2 0 1 0 4 0 a2 2 0 1 0 -4 0"),
        ["Windows"] = Parse("M4 5 L11 4 V11 H4 Z M13 3.7 L20 3 V11 H13 Z M4 13 H11 V20 L4 19 Z M13 13 H20 V21 L13 20.3 Z"),

        // Dimensions
        ["Performance"] = Parse("M4 18 L9 12 L13 15 L20 6 M16 6 H20 V10"),
        ["Latency"] = Parse("M12 12 m-8 0 a8 8 0 1 0 16 0 a8 8 0 1 0 -16 0 M12 7 V12 L15 14"),
        ["Stability"] = Parse("M4 16 H7 L9 9 L12 19 L15 7 L17 16 H20"),

        // Status / actions
        ["ShieldCheck"] = Parse("M12 3 L19 6 V11 C19 16 15.5 19.5 12 21 C8.5 19.5 5 16 5 11 V6 Z M9 11.5 L11.2 13.7 L15.2 9.2"),
        ["Check"] = Parse("M5 12.5 L10 17.5 L19 7"),
        ["Info"] = Parse("M12 12 m-8 0 a8 8 0 1 0 16 0 a8 8 0 1 0 -16 0 M12 11 V16 M12 8 v0.01"),
        ["Warning"] = Parse("M12 4 L21 19 H3 Z M12 10 V14 M12 16.5 v0.01"),
        ["Gear"] = Parse("M12 12 m-3 0 a3 3 0 1 0 6 0 a3 3 0 1 0 -6 0 M12 3 V6 M12 18 V21 M3 12 H6 M18 12 H21 M5.6 5.6 L7.7 7.7 M16.3 16.3 L18.4 18.4 M18.4 5.6 L16.3 7.7 M7.7 16.3 L5.6 18.4"),
        // Sliders — settings read instantly as sliders/controls (used on the header "Réglages" button).
        ["Sliders"] = Parse(
            "M4 7 H13 M17 7 H20 M4 12 H8 M12 12 H20 M4 17 H15 M19 17 H20 " + // 3 rails
            "M15 7 m-2 0 a2 2 0 1 0 4 0 a2 2 0 1 0 -4 0 " +                  // poignée 1
            "M10 12 m-2 0 a2 2 0 1 0 4 0 a2 2 0 1 0 -4 0 " +                 // poignée 2
            "M17 17 m-2 0 a2 2 0 1 0 4 0 a2 2 0 1 0 -4 0"),                  // poignée 3
        ["Clock"] = Parse("M12 12 m-8 0 a8 8 0 1 0 16 0 a8 8 0 1 0 -16 0 M12 7 V12 L16 14"),
        ["More"] = Parse("M6 12 v0.01 M12 12 v0.01 M18 12 v0.01"),
        ["Export"] = Parse("M12 3 V14 M8 7 L12 3 L16 7 M5 14 V20 H19 V14"),

        // Feature cards
        ["Diagnostic"] = Parse("M4 12 H8 L10 7 L14 17 L16 12 H20"),
        ["Gaming"] = Parse(
            "M6 11 H10 M8 9 V13 " +                 // croix directionnelle
            "M15 12 H15.01 M18 10 H18.01 " +        // 2 boutons (points -> ronds via caps Round)
            "M17.32 5 H6.68 A4 4 0 0 0 2.7 8.59 " + // corps : épaulement gauche
            "C2.6 9.42 2 14.46 2 16 A3 3 0 0 0 5 19 " +
            "C6 19 6.5 18.5 7 18 L8.41 16.59 A2 2 0 0 1 9.83 16 H14.17 " +
            "A2 2 0 0 1 15.59 16.59 L17 18 C17.5 18.5 18 19 19 19 " +
            "A3 3 0 0 0 22 16 C22 14.46 21.4 9.42 21.32 8.59 A4 4 0 0 0 17.32 5 Z"),
        ["Security"] = Parse("M12 3 L19 6 V11 C19 16 15.5 19.5 12 21 C8.5 19.5 5 16 5 11 V6 Z"),
        ["Report"] = Parse("M7 3 H15 L19 7 V21 H7 Z M15 3 V7 H19 M10 11 H16 M10 14 H16 M10 17 H14"),
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string ?? string.Empty;
        return Map.TryGetValue(key, out var geo) ? geo : Geometry.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Geometry Parse(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }
}
