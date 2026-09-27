using Autodesk.Revit.DB;

namespace RevitMCPBridge;

/// <summary>
/// Unit handling. Revit stores lengths internally in decimal feet, which is also the imperial unit
/// this project reports, so coordinates need no conversion. Parameter values are additionally
/// reported as the formatted string Revit itself would display, so a reader sees
/// 8' 6" rather than 8.5.
/// </summary>
public static class Units
{
    /// <summary>Internal (decimal feet) coordinates, rounded to a sane precision for JSON.</summary>
    public static Dictionary<string, object> PointToJson(XYZ p) => new()
    {
        ["x"] = Round(p.X),
        ["y"] = Round(p.Y),
        ["z"] = Round(p.Z),
        ["units"] = "feet"
    };

    /// <summary>1/32" is finer than any modelling tolerance that matters here.</summary>
    public static double Round(double feet) => Math.Round(feet, 6);

    /// <summary>
    /// Revit's own display string for a parameter, honouring the project's unit settings.
    /// Falls back to the raw value when Revit cannot format it.
    /// </summary>
    public static string? DisplayValue(Parameter parameter)
    {
        try
        {
            var text = parameter.AsValueString();
            if (!string.IsNullOrEmpty(text)) return text;
        }
        catch (Exception)
        {
            // Some parameters throw rather than report that they have no display form.
        }

        return parameter.StorageType switch
        {
            StorageType.String  => parameter.AsString(),
            StorageType.Integer => parameter.AsInteger().ToString(),
            StorageType.Double  => Round(parameter.AsDouble()).ToString("0.######"),
            StorageType.ElementId => parameter.AsElementId().Value.ToString(),
            _ => null
        };
    }

    /// <summary>The raw stored value, so a caller can do arithmetic without re-parsing a string.</summary>
    public static object? RawValue(Parameter parameter) => parameter.StorageType switch
    {
        StorageType.String    => parameter.AsString(),
        StorageType.Integer   => parameter.AsInteger(),
        StorageType.Double    => Round(parameter.AsDouble()),
        StorageType.ElementId => parameter.AsElementId().Value,
        _ => null
    };
}
