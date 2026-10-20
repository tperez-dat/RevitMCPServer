using System.Globalization;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Rules;

/// <summary>
/// One value read off an element, in every form a condition might need to compare.
///
/// The distinction between <see cref="Defined"/> and <see cref="HasValue"/> is the whole reason
/// this type exists: "the parameter is not on this element" and "the parameter is there and empty"
/// are different findings, and a check set that cannot tell them apart reports the wrong one.
/// </summary>
public readonly record struct ReadValue(
    bool Defined,
    bool HasValue,
    string? Text,
    string? DisplayText,
    double? Number)
{
    /// <summary>The read found nothing to read — no such parameter, no such relationship.</summary>
    public static readonly ReadValue Undefined = new(false, false, null, null, null);

    /// <summary>Present but empty.</summary>
    public static ReadValue Empty() => new(true, false, null, null, null);

    /// <summary>
    /// Null means the element has no such relationship at all — it is on no level, in no design
    /// option, in a model with no worksets. Whitespace means the relationship is there and the
    /// value is blank. Conditions can tell those apart through 'defined'; both answer 'hasNoValue'.
    /// </summary>
    public static ReadValue OfText(string? text) =>
        text is null
            ? Undefined
            : string.IsNullOrWhiteSpace(text)
                ? Empty()
                : new ReadValue(true, true, text, null,
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                        ? n
                        : null);

    public static ReadValue OfNumber(double value) =>
        new(true, true, value.ToString("0.######", CultureInfo.InvariantCulture), null, value);

    /// <summary>
    /// Every textual form worth comparing, so a caller writing "8.5" and one writing "8' 6&quot;"
    /// both match the same wall. Duplicates and blanks are dropped.
    /// </summary>
    public IEnumerable<string> Texts()
    {
        if (!string.IsNullOrEmpty(Text)) yield return Text!;
        if (!string.IsNullOrEmpty(DisplayText) && DisplayText != Text) yield return DisplayText!;
    }

    /// <summary>The single best text form, for reporting the evidence behind a match.</summary>
    public string? Best => Text ?? DisplayText;
}

/// <summary>
/// Reads the value a filter asks for off an element.
///
/// Parameter lookup follows Revit's own precedence: the instance first, then its type. That is what
/// a user means by "the wall's Fire Rating" — Revit shows the type's value on the instance, and a
/// check that only looked at the instance would report every wall in the model as unfilled.
/// </summary>
public sealed class ElementReader(Document doc)
{
    private readonly Dictionary<long, string?> _worksetNames = [];
    private readonly Dictionary<long, string?> _phaseNames = [];

    public ReadValue Read(Element element, ReadKind kind, string? parameterName) => kind switch
    {
        ReadKind.Category => ReadValue.OfText(element.Category?.Name),
        ReadKind.Parameter => ReadNamedParameter(element, parameterName!),
        ReadKind.ApiParameter => ReadBuiltInParameter(element, parameterName!),
        ReadKind.TypeOrInstance => ReadValue.OfText(element is ElementType ? "Type" : "Instance"),
        ReadKind.Workset => ReadValue.OfText(WorksetName(element)),
        ReadKind.ApiType => ReadValue.OfText(element.GetType().Name),
        ReadKind.Family => ReadValue.OfText(FamilyName(element)),
        ReadKind.Type => ReadValue.OfText(TypeName(element)),
        ReadKind.Name => ReadValue.OfText(Json.SafeName(element)),
        ReadKind.Level => ReadValue.OfText(LevelName(element)),
        ReadKind.DesignOption => ReadValue.OfText(DesignOptionName(element)),
        ReadKind.ElementId => ReadValue.OfNumber(element.Id.Value),
        ReadKind.PhaseCreated => ReadValue.OfText(PhaseName(element, created: true)),
        ReadKind.PhaseDemolished => ReadValue.OfText(PhaseName(element, created: false)),
        _ => ReadValue.Undefined
    };

    /// <summary>
    /// A parameter by the name Revit shows. Instance first, then type: that is the value a user
    /// sees in the properties palette, and therefore the value a check is about.
    /// </summary>
    private ReadValue ReadNamedParameter(Element element, string name)
    {
        var parameter = Find(element, name);

        if (parameter is null && element is not ElementType)
        {
            var type = SafeType(element);
            if (type is not null) parameter = Find(type, name);
        }

        return parameter is null ? ReadValue.Undefined : FromParameter(parameter);

        static Parameter? Find(Element on, string name)
        {
            try
            {
                var direct = on.LookupParameter(name);
                if (direct is not null) return direct;

                // LookupParameter returns the first match; GetParameters finds a name bound more
                // than once, which happens when a project parameter shadows a shared one.
                var all = on.GetParameters(name);
                return all.Count > 0 ? all.FirstOrDefault(p => p.HasValue) ?? all[0] : null;
            }
            catch (Exception) { return null; }
        }
    }

    private ReadValue ReadBuiltInParameter(Element element, string name)
    {
        if (!Enum.TryParse<BuiltInParameter>(name, ignoreCase: true, out var builtIn))
            return ReadValue.Undefined;

        var parameter = Get(element, builtIn);

        if (parameter is null && element is not ElementType)
        {
            var type = SafeType(element);
            if (type is not null) parameter = Get(type, builtIn);
        }

        return parameter is null ? ReadValue.Undefined : FromParameter(parameter);

        static Parameter? Get(Element on, BuiltInParameter id)
        {
            try { return on.get_Parameter(id); }
            catch (Exception) { return null; }
        }
    }

    private static ReadValue FromParameter(Parameter parameter)
    {
        try
        {
            if (!parameter.HasValue) return ReadValue.Empty();

            var raw = Units.RawValue(parameter);
            var text = raw is null ? null : Convert.ToString(raw, CultureInfo.InvariantCulture);
            var display = Units.DisplayValue(parameter);

            double? number = parameter.StorageType switch
            {
                StorageType.Double => parameter.AsDouble(),
                StorageType.Integer => parameter.AsInteger(),
                StorageType.ElementId => parameter.AsElementId().Value,
                _ => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                    ? n
                    : null
            };

            // An ElementId parameter pointing at nothing reads as -1, which is "empty", not a value.
            if (parameter.StorageType == StorageType.ElementId
                && parameter.AsElementId() == Autodesk.Revit.DB.ElementId.InvalidElementId)
                return ReadValue.Empty();

            var hasValue = !string.IsNullOrWhiteSpace(text) || !string.IsNullOrWhiteSpace(display);

            return hasValue
                ? new ReadValue(true, true, text, display, number)
                : ReadValue.Empty();
        }
        catch (Exception)
        {
            return ReadValue.Undefined;
        }
    }

    private static ElementType? SafeType(Element element)
    {
        try
        {
            var typeId = element.GetTypeId();
            return typeId == Autodesk.Revit.DB.ElementId.InvalidElementId
                ? null
                : element.Document.GetElement(typeId) as ElementType;
        }
        catch (Exception) { return null; }
    }

    private static string? FamilyName(Element element)
    {
        try
        {
            return element switch
            {
                FamilyInstance instance => instance.Symbol?.FamilyName,
                ElementType type => type.FamilyName,
                _ => SafeType(element)?.FamilyName
            };
        }
        catch (Exception) { return null; }
    }

    private static string? TypeName(Element element)
    {
        try
        {
            if (element is ElementType type) return Json.SafeName(type);
            return SafeType(element) is { } owned ? Json.SafeName(owned) : null;
        }
        catch (Exception) { return null; }
    }

    private string? WorksetName(Element element)
    {
        if (!doc.IsWorkshared) return null;

        try
        {
            var id = (long)element.WorksetId.IntegerValue;
            if (_worksetNames.TryGetValue(id, out var cached)) return cached;

            var name = doc.GetWorksetTable().GetWorkset(element.WorksetId)?.Name;
            _worksetNames[id] = name;
            return name;
        }
        catch (Exception) { return null; }
    }

    private static string? LevelName(Element element)
    {
        try
        {
            if (element.LevelId is { } levelId
                && levelId != Autodesk.Revit.DB.ElementId.InvalidElementId
                && element.Document.GetElement(levelId) is Level level)
            {
                return level.Name;
            }

            // Many categories report their level through a parameter rather than LevelId.
            foreach (var id in new[]
                     {
                         BuiltInParameter.FAMILY_LEVEL_PARAM,
                         BuiltInParameter.SCHEDULE_LEVEL_PARAM,
                         BuiltInParameter.WALL_BASE_CONSTRAINT,
                         BuiltInParameter.LEVEL_PARAM
                     })
            {
                var parameter = element.get_Parameter(id);
                if (parameter is null || !parameter.HasValue) continue;

                if (element.Document.GetElement(parameter.AsElementId()) is Level fromParameter)
                    return fromParameter.Name;
            }
        }
        catch (Exception)
        {
            // Not every element sits on a level.
        }

        return null;
    }

    private static string? DesignOptionName(Element element)
    {
        try { return element.DesignOption?.Name; }
        catch (Exception) { return null; }
    }

    private string? PhaseName(Element element, bool created)
    {
        try
        {
            var id = created ? element.CreatedPhaseId : element.DemolishedPhaseId;
            if (id == Autodesk.Revit.DB.ElementId.InvalidElementId) return null;

            if (_phaseNames.TryGetValue(id.Value, out var cached)) return cached;

            var name = Json.SafeName(doc.GetElement(id));
            _phaseNames[id.Value] = name;
            return name;
        }
        catch (Exception) { return null; }
    }
}
