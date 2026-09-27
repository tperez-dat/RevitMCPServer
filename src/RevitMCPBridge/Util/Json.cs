using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge;

/// <summary>Shorthand for building the JSON payloads handlers return.</summary>
public static class Json
{
    public static JsonObject Obj() => new();

    public static JsonNode? From(object? value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, Protocol.Json);

    /// <summary>
    /// A paged list payload. Every list command answers in this shape so a caller learns the same
    /// way each time whether more results remain.
    /// </summary>
    public static JsonObject Page(string itemsKey, IEnumerable<JsonNode?> pageItems,
        int totalMatched, int offset, int limit)
    {
        var array = new JsonArray();
        foreach (var item in pageItems) array.Add(item);

        var next = offset + array.Count;
        var result = new JsonObject
        {
            [itemsKey] = array,
            ["count"] = array.Count,
            ["totalMatched"] = totalMatched,
            ["offset"] = offset,
            ["limit"] = limit,
            ["hasMore"] = next < totalMatched
        };

        if (next < totalMatched)
            result["cursor"] = Paging.EncodeCursor(next);

        return result;
    }

    /// <summary>Identity fields shared by every element the bridge reports.</summary>
    public static JsonObject ElementRef(Element element)
    {
        var o = new JsonObject
        {
            ["id"] = element.Id.Value,
            ["name"] = SafeName(element),
            ["category"] = element.Category?.Name,
            ["class"] = element.GetType().Name
        };

        if (element.GetTypeId() is { } typeId && typeId != ElementId.InvalidElementId)
        {
            o["typeId"] = typeId.Value;
            o["typeName"] = (element.Document.GetElement(typeId) as ElementType)?.Name;
        }

        return o;
    }

    /// <summary>Element.Name throws for a few element kinds rather than returning empty.</summary>
    public static string? SafeName(Element element)
    {
        try { return element.Name; }
        catch (Exception) { return null; }
    }

    /// <summary>Serialises every parameter on an element, sorted so output is stable between calls.</summary>
    public static JsonArray Parameters(Element element)
    {
        var array = new JsonArray();

        var ordered = element.GetOrderedParameters()
            .Where(p => p is not null)
            .OrderBy(p => p.Definition?.Name ?? "", StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in ordered)
        {
            var definition = parameter.Definition;
            if (definition is null) continue;

            var entry = new JsonObject
            {
                ["name"] = definition.Name,
                ["storageType"] = parameter.StorageType.ToString(),
                ["isReadOnly"] = parameter.IsReadOnly,
                ["hasValue"] = parameter.HasValue,
                ["value"] = From(Units.RawValue(parameter)),
                ["displayValue"] = Units.DisplayValue(parameter),
                ["isShared"] = parameter.IsShared
            };

            if (parameter.IsShared) entry["guid"] = parameter.GUID.ToString();

            // The built-in id tells a caller this is a standard Revit parameter, not a project one.
            if (definition is InternalDefinition internalDefinition
                && internalDefinition.BuiltInParameter != BuiltInParameter.INVALID)
            {
                entry["builtInParameter"] = internalDefinition.BuiltInParameter.ToString();
            }

            array.Add(entry);
        }

        return array;
    }
}
