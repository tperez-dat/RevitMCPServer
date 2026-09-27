using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// Sets a parameter on one or more elements.
///
/// Units are the subtlety here. Revit stores lengths in decimal feet, but a user thinks in the
/// project's display units. So a JSON number is taken as the raw stored value, while a string is
/// handed to Revit's own parser via SetValueString, which accepts "8' 6\"" and applies display
/// units. Both paths are reported back so the caller can see which one was used.
///
/// To set a type parameter, pass the type's id — GET_ELEMENT_TYPE_PROPERTIES reports it. That
/// changes every instance of the type, so it is deliberately not a flag on an instance id.
/// </summary>
public sealed class SetElementParameterHandler : IBridgeCommandHandler
{
    public string Command => Commands.SetElementParameter;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;

        var ids = args.ElementIds("elementIds");
        var parameterName = args.String("parameterName");

        if (!args.Has("value"))
            throw new BridgeException(BridgeErrorCode.BadRequest,
                "SET_ELEMENT_PARAMETER: 'value' is required. Pass null only via 'clear': true.");

        // Read the raw node rather than a typed accessor: the coercion depends on the parameter's
        // storage type, which is not known until each element is inspected.
        var raw = context.Request.Args!["value"];

        var updated = new JsonArray();
        var failures = new JsonArray();

        foreach (var id in ids)
        {
            var element = doc.GetElement(id);
            if (element is null)
            {
                failures.Add(Failure(id.Value, null, "No element with this id exists."));
                continue;
            }

            var parameter = element.LookupParameter(parameterName);
            if (parameter is null)
            {
                failures.Add(Failure(id.Value, Json.SafeName(element),
                    $"This element has no parameter named '{parameterName}'. " +
                    "GET_ELEMENT_PROPERTIES lists the ones it does have."));
                continue;
            }

            if (parameter.IsReadOnly)
            {
                failures.Add(Failure(id.Value, Json.SafeName(element),
                    $"'{parameterName}' is read-only on this element; Revit computes or derives it."));
                continue;
            }

            var before = Units.DisplayValue(parameter);

            try
            {
                var how = Apply(parameter, raw, parameterName);

                // Read the value back rather than echoing the input: Revit may round, clamp, or
                // reinterpret it, and the stored result is what the caller needs to know.
                var entry = new JsonObject
                {
                    ["elementId"] = id.Value,
                    ["name"] = Json.SafeName(element),
                    ["category"] = element.Category?.Name,
                    ["appliedAs"] = how,
                    ["before"] = before,
                    ["after"] = Units.DisplayValue(parameter),
                    ["storedValue"] = Json.From(Units.RawValue(parameter))
                };

                updated.Add(entry);
            }
            catch (BridgeException ex)
            {
                failures.Add(Failure(id.Value, Json.SafeName(element), ex.Message));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                failures.Add(Failure(id.Value, Json.SafeName(element), ex.Message));
            }
        }

        if (updated.Count == 0)
        {
            throw new BridgeException(BridgeErrorCode.BadRequest,
                "No element was updated. First reason: " +
                (failures.FirstOrDefault()?["error"]?.GetValue<string>() ?? "unknown."));
        }

        return new JsonObject
        {
            ["parameterName"] = parameterName,
            ["updatedCount"] = updated.Count,
            ["failedCount"] = failures.Count,
            ["updated"] = updated,
            ["failures"] = failures
        };
    }

    private static JsonObject Failure(long id, string? name, string error) => new()
    {
        ["elementId"] = id,
        ["name"] = name,
        ["error"] = error
    };

    /// <summary>
    /// Coerces the JSON value onto the parameter's storage type. Returns a short description of the
    /// route taken, because "set as a display-unit string" and "set as a raw internal value" are
    /// materially different outcomes for a length.
    /// </summary>
    private static string Apply(Parameter parameter, JsonNode? raw, string parameterName)
    {
        if (raw is null)
            throw new BridgeException(BridgeErrorCode.BadRequest, "'value' was null.");

        var kind = raw.GetValueKind();

        switch (parameter.StorageType)
        {
            case StorageType.String:
                var text = kind == JsonValueKind.String
                    ? raw.GetValue<string>()
                    : raw.ToJsonString().Trim('"');
                parameter.Set(text);
                return "string";

            case StorageType.Integer:
                // Yes/No parameters are integers in Revit; accept a real boolean for them.
                if (kind is JsonValueKind.True or JsonValueKind.False)
                {
                    parameter.Set(raw.GetValue<bool>() ? 1 : 0);
                    return "boolean as integer (0/1)";
                }

                if (kind == JsonValueKind.Number)
                {
                    parameter.Set((int)raw.GetValue<double>());
                    return "integer";
                }

                // A string may be an enum-ish display value ("Fixed"), which only Revit can map.
                if (TrySetValueString(parameter, raw.GetValue<string>())) return "display string";

                if (int.TryParse(raw.GetValue<string>(), out var parsedInt))
                {
                    parameter.Set(parsedInt);
                    return "integer parsed from string";
                }

                throw new BridgeException(BridgeErrorCode.BadRequest,
                    $"'{parameterName}' stores an integer, and Revit would not accept " +
                    $"'{raw.GetValue<string>()}'.");

            case StorageType.Double:
                if (kind == JsonValueKind.Number)
                {
                    // A bare number is the raw internal value: feet for a length, radians for an angle.
                    parameter.Set(raw.GetValue<double>());
                    return "raw internal units (feet for lengths)";
                }

                var doubleText = raw.GetValue<string>();

                // Prefer Revit's parser: it honours the project's display units, so "8' 6\"" works.
                if (TrySetValueString(parameter, doubleText)) return "display units via Revit's parser";

                if (double.TryParse(doubleText, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var parsedDouble))
                {
                    parameter.Set(parsedDouble);
                    return "raw internal units parsed from string";
                }

                throw new BridgeException(BridgeErrorCode.BadRequest,
                    $"Revit could not interpret '{doubleText}' for '{parameterName}'. Pass a number " +
                    "for raw internal units (feet), or a string Revit can parse such as \"8' 6\\\"\".");

            case StorageType.ElementId:
                if (kind == JsonValueKind.Number)
                {
                    parameter.Set(new ElementId(raw.GetValue<long>()));
                    return "element id";
                }

                if (long.TryParse(raw.GetValue<string>(), out var parsedId))
                {
                    parameter.Set(new ElementId(parsedId));
                    return "element id parsed from string";
                }

                throw new BridgeException(BridgeErrorCode.BadRequest,
                    $"'{parameterName}' stores an element id, so 'value' must be a numeric id.");

            default:
                throw new BridgeException(BridgeErrorCode.BadRequest,
                    $"'{parameterName}' has storage type {parameter.StorageType}, which cannot be set.");
        }
    }

    /// <summary>SetValueString returns false for a value it cannot parse, and throws for some.</summary>
    private static bool TrySetValueString(Parameter parameter, string text)
    {
        try { return parameter.SetValueString(text); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return false; }
    }
}

/// <summary>
/// Deletes elements.
///
/// Revit cascades: deleting a wall takes its hosted doors and windows with it. That is why this
/// reports every id Revit actually removed, and why 'dryRun' exists — it lists the dependents
/// without touching the model, which is the only way to see the blast radius beforehand.
/// </summary>
public sealed class DeleteElementHandler : IBridgeCommandHandler
{
    public string Command => Commands.DeleteElement;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var requested = context.Args.ElementIds("elementIds");
        var dryRun = context.Args.Bool("dryRun");

        var targets = new List<Element>();
        var missing = new JsonArray();

        foreach (var id in requested)
        {
            var element = doc.GetElement(id);
            if (element is null) missing.Add(id.Value);
            else targets.Add(element);
        }

        if (targets.Count == 0)
        {
            throw new BridgeException(BridgeErrorCode.NotFound,
                "None of the given element ids exist in this document; nothing was deleted.");
        }

        var describing = new JsonArray();
        foreach (var element in targets)
        {
            var entry = Json.ElementRef(element);

            // GetDependentElements includes the element itself, so subtract it for a dependent count.
            var dependents = SafeDependents(element);
            entry["dependentCount"] = Math.Max(0, dependents.Count - 1);

            if (dryRun)
            {
                var ids = new JsonArray();
                foreach (var dependentId in dependents.Where(d => d != element.Id))
                {
                    var dependent = doc.GetElement(dependentId);
                    if (dependent is not null) ids.Add(Json.ElementRef(dependent));
                }
                entry["dependents"] = ids;
            }

            describing.Add(entry);
        }

        if (dryRun)
        {
            return new JsonObject
            {
                ["dryRun"] = true,
                ["deleted"] = false,
                ["wouldDelete"] = describing,
                ["requestedCount"] = targets.Count,
                ["missingElementIds"] = missing,
                ["note"] = "Nothing was changed. Deleting an element also deletes the elements " +
                           "hosted in or derived from it, listed here as 'dependents'."
            };
        }

        ICollection<ElementId> actuallyDeleted;
        try
        {
            actuallyDeleted = doc.Delete(targets.Select(e => e.Id).ToList());
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
        {
            throw new BridgeException(BridgeErrorCode.InvalidState,
                "Revit refused the deletion. Some elements cannot be deleted — the last level, " +
                "a view that is the only one of its kind, or an element a constraint depends on.",
                ex.Message);
        }

        var deletedIds = new JsonArray();
        foreach (var id in actuallyDeleted) deletedIds.Add(id.Value);

        return new JsonObject
        {
            ["deleted"] = true,
            ["requestedCount"] = targets.Count,
            ["deletedCount"] = actuallyDeleted.Count,
            ["deletedElementIds"] = deletedIds,
            ["requested"] = describing,
            ["missingElementIds"] = missing,
            ["note"] = actuallyDeleted.Count > targets.Count
                ? $"{actuallyDeleted.Count - targets.Count} further element(s) were deleted as " +
                  "dependents of the ones requested. Ctrl+Z in Revit reverses the whole deletion."
                : "Ctrl+Z in Revit reverses this deletion."
        };
    }

    private static ICollection<ElementId> SafeDependents(Element element)
    {
        try { return element.GetDependentElements(null); }
        catch (Exception) { return new List<ElementId> { element.Id }; }
    }
}
