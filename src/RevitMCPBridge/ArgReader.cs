using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge;

/// <summary>
/// Typed, validating access to a request's arguments. Every failure is a
/// <see cref="BridgeErrorCode.BadRequest"/> naming the offending argument, because the caller is a
/// language model and a precise message is what lets it correct itself on the next attempt.
/// </summary>
public sealed class ArgReader(JsonObject args, string command)
{
    private readonly JsonObject _args = args;

    private BridgeException Bad(string message) =>
        new(BridgeErrorCode.BadRequest, $"{command}: {message}");

    public bool Has(string name) => _args.TryGetPropertyValue(name, out var n) && n is not null;

    private JsonNode Require(string name) =>
        _args.TryGetPropertyValue(name, out var node) && node is not null
            ? node
            : throw Bad($"required argument '{name}' is missing.");

    public string String(string name)
    {
        var s = Require(name).GetValue<string?>() ?? throw Bad($"'{name}' must be a string.");
        return s.Length > 0 ? s : throw Bad($"'{name}' must not be empty.");
    }

    public string? StringOrNull(string name) =>
        Has(name) ? _args[name]!.GetValue<string?>() : null;

    public string StringOr(string name, string fallback) => StringOrNull(name) ?? fallback;

    public double Double(string name)
    {
        var node = Require(name);
        try { return node.GetValue<double>(); }
        catch (Exception) when (node.GetValueKind() == JsonValueKind.String
                                && double.TryParse(node.GetValue<string>(), NumberStyles.Float,
                                                   CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        catch (Exception) { throw Bad($"'{name}' must be a number."); }
    }

    public double DoubleOr(string name, double fallback) => Has(name) ? Double(name) : fallback;

    public double PositiveDouble(string name)
    {
        var v = Double(name);
        return v > 0 ? v : throw Bad($"'{name}' must be greater than zero (got {v}).");
    }

    public long Long(string name)
    {
        var node = Require(name);
        try { return node.GetValue<long>(); }
        catch (Exception) when (node.GetValueKind() == JsonValueKind.String
                                && long.TryParse(node.GetValue<string>(), out var parsed))
        {
            return parsed;
        }
        catch (Exception) { throw Bad($"'{name}' must be an integer."); }
    }

    public long? LongOrNull(string name) => Has(name) ? Long(name) : null;

    public bool Bool(string name, bool fallback = false)
    {
        if (!Has(name)) return fallback;
        var node = _args[name]!;
        try { return node.GetValue<bool>(); }
        catch (Exception) { throw Bad($"'{name}' must be true or false."); }
    }

    public int? IntOrNull(string name) => Has(name) ? (int)Long(name) : null;

    /// <summary>Reads an array of element ids, rejecting an empty list.</summary>
    public IList<ElementId> ElementIds(string name)
    {
        if (Require(name) is not JsonArray array)
            throw Bad($"'{name}' must be an array of element ids.");

        var ids = new List<ElementId>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            var item = array[i] ?? throw Bad($"'{name}[{i}]' is null.");
            long value;
            try { value = item.GetValue<long>(); }
            catch (Exception)
            {
                if (item.GetValueKind() != JsonValueKind.String
                    || !long.TryParse(item.GetValue<string>(), out value))
                    throw Bad($"'{name}[{i}]' must be an integer element id.");
            }
            ids.Add(new ElementId(value));
        }

        return ids.Count > 0 ? ids : throw Bad($"'{name}' must contain at least one element id.");
    }

    /// <summary>
    /// Reads an {x,y,z} point in feet (the project's display unit and Revit's internal unit alike,
    /// so no conversion is applied). 'z' is optional and defaults to 0.
    /// </summary>
    public XYZ Point(string name)
    {
        if (Require(name) is not JsonObject o)
            throw Bad($"'{name}' must be an object like {{ \"x\": 0, \"y\": 0, \"z\": 0 }}.");

        var reader = new ArgReader(o, $"{command}.{name}");
        return new XYZ(reader.Double("x"), reader.Double("y"), reader.DoubleOr("z", 0));
    }

    /// <summary>Reads an array of points, requiring at least <paramref name="minimum"/> of them.</summary>
    public IList<XYZ> Points(string name, int minimum)
    {
        if (Require(name) is not JsonArray array)
            throw Bad($"'{name}' must be an array of points.");
        if (array.Count < minimum)
            throw Bad($"'{name}' needs at least {minimum} points (got {array.Count}).");

        var points = new List<XYZ>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject o)
                throw Bad($"'{name}[{i}]' must be an object like {{ \"x\": 0, \"y\": 0 }}.");
            var reader = new ArgReader(o, $"{command}.{name}[{i}]");
            points.Add(new XYZ(reader.Double("x"), reader.Double("y"), reader.DoubleOr("z", 0)));
        }
        return points;
    }

    public JsonArray Array(string name) =>
        Require(name) as JsonArray ?? throw Bad($"'{name}' must be an array.");

    public JsonObject Object(string name) =>
        Require(name) as JsonObject ?? throw Bad($"'{name}' must be an object.");

    /// <summary>Limit/cursor pair shared by every list command.</summary>
    public (int Limit, int Offset) Page()
    {
        var limit = Paging.Clamp(IntOrNull("limit"));
        if (!Paging.TryDecodeCursor(StringOrNull("cursor"), out var offset))
            throw Bad("'cursor' is not a cursor returned by a previous call.");
        return (limit, offset);
    }
}
