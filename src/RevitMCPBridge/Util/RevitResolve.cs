using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge;

/// <summary>
/// Resolves the levels and types creation commands need, by id or by name, with error messages
/// that name the alternatives. A caller that guessed a wall type wrong should learn what the
/// options are from the failure rather than having to make a second exploratory call.
/// </summary>
public static class RevitResolve
{
    /// <summary>
    /// The level to build on: an explicit id, an explicit name, or the active view's level.
    /// </summary>
    public static Level Level(CommandContext context, string idArg = "levelId", string nameArg = "levelName")
    {
        var doc = context.Doc;

        if (context.Args.LongOrNull(idArg) is { } id)
        {
            return doc.GetElement(new ElementId(id)) as Level
                   ?? throw new BridgeException(BridgeErrorCode.NotFound,
                       $"Element {id} is not a level.");
        }

        if (context.Args.StringOrNull(nameArg) is { } name)
        {
            var matches = Levels(doc)
                .Where(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 1) return matches[0];

            if (matches.Count == 0)
                throw new BridgeException(BridgeErrorCode.NotFound,
                    $"No level is named '{name}'. Available levels: {NameList(Levels(doc))}");

            throw new BridgeException(BridgeErrorCode.BadRequest,
                $"'{name}' matches {matches.Count} levels. Pass '{idArg}' instead.");
        }

        // Fall back to the active view's level, which is what a user means by "on this floor".
        var viewLevel = doc.ActiveView?.GenLevel;
        if (viewLevel is not null) return viewLevel;

        var lowest = Levels(doc).OrderBy(l => l.Elevation).FirstOrDefault();
        if (lowest is not null) return lowest;

        throw new BridgeException(BridgeErrorCode.InvalidState,
            "This model has no levels, so nothing level-hosted can be created.");
    }

    public static IEnumerable<Level> Levels(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();

    /// <summary>
    /// An <see cref="ElementType"/> of the requested class, by id or name, defaulting to the
    /// document's current default type for that family when neither is given.
    /// </summary>
    public static T ElementTypeOf<T>(CommandContext context, string idArg, string nameArg,
        Func<Document, ElementId>? defaultTypeId = null) where T : ElementType
    {
        var doc = context.Doc;

        if (context.Args.LongOrNull(idArg) is { } id)
        {
            var byId = doc.GetElement(new ElementId(id));
            return byId as T
                   ?? throw new BridgeException(BridgeErrorCode.NotFound,
                       $"Element {id} is not a {typeof(T).Name} " +
                       $"(it is {byId?.GetType().Name ?? "missing"}).");
        }

        var available = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().ToList();

        if (context.Args.StringOrNull(nameArg) is { } name)
        {
            var matches = available
                .Where(t => string.Equals(Json.SafeName(t), name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 1) return matches[0];

            if (matches.Count == 0)
                throw new BridgeException(BridgeErrorCode.NotFound,
                    $"No {typeof(T).Name} is named '{name}'. Available: {NameList(available)}");

            throw new BridgeException(BridgeErrorCode.BadRequest,
                $"'{name}' matches {matches.Count} types. Pass '{idArg}' instead.");
        }

        // The document's default type is what Revit itself would use for a manual placement.
        if (defaultTypeId is not null)
        {
            var defaultId = defaultTypeId(doc);
            if (defaultId != ElementId.InvalidElementId && doc.GetElement(defaultId) is T fromDefault)
                return fromDefault;
        }

        return available.FirstOrDefault()
               ?? throw new BridgeException(BridgeErrorCode.NotFound,
                   $"This model contains no {typeof(T).Name}, so nothing of that kind can be created.");
    }

    /// <summary>
    /// A loadable family symbol, filtered to a category so "W12x26" cannot resolve to a door.
    /// Activates the symbol, which Revit requires before the first placement or the instance is
    /// created without geometry.
    /// </summary>
    public static FamilySymbol FamilySymbol(CommandContext context, BuiltInCategory category,
        string idArg = "typeId", string nameArg = "typeName", string familyArg = "familyName")
    {
        var doc = context.Doc;

        var candidates = new FilteredElementCollector(doc)
            .OfClass(typeof(Autodesk.Revit.DB.FamilySymbol))
            .OfCategory(category)
            .Cast<FamilySymbol>()
            .ToList();

        FamilySymbol symbol;

        if (context.Args.LongOrNull(idArg) is { } id)
        {
            symbol = doc.GetElement(new ElementId(id)) as FamilySymbol
                     ?? throw new BridgeException(BridgeErrorCode.NotFound,
                         $"Element {id} is not a family type.");

            if (symbol.Category?.Id.Value != (long)category)
                throw new BridgeException(BridgeErrorCode.BadRequest,
                    $"Family type {id} ('{Json.SafeName(symbol)}') is in category " +
                    $"'{symbol.Category?.Name}', not '{category}'.");
        }
        else
        {
            var typeName = context.Args.StringOrNull(nameArg);
            var familyName = context.Args.StringOrNull(familyArg);

            var matches = candidates
                .Where(s => typeName is null
                            || string.Equals(Json.SafeName(s), typeName, StringComparison.OrdinalIgnoreCase))
                .Where(s => familyName is null
                            || string.Equals(s.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                var wanted = typeName ?? familyName;
                throw new BridgeException(BridgeErrorCode.NotFound,
                    wanted is null
                        ? $"This model has no loaded family types in category '{category}'. " +
                          "Load a family first."
                        : $"No '{category}' family type matches '{wanted}'. " +
                          $"Available: {NameList(candidates.Cast<ElementType>())}");
            }

            if (matches.Count > 1 && typeName is not null)
                throw new BridgeException(BridgeErrorCode.BadRequest,
                    $"'{typeName}' matches {matches.Count} types. Pass '{familyArg}' too, or '{idArg}'.");

            symbol = matches[0];
        }

        // A symbol must be active before placement; Revit otherwise places an empty instance.
        if (!symbol.IsActive)
        {
            symbol.Activate();
            context.Doc.Regenerate();
        }

        return symbol;
    }

    private static string NameList(IEnumerable<Element> elements)
    {
        var names = elements
            .Select(Json.SafeName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToList();

        return names.Count == 0 ? "(none)" : string.Join(", ", names);
    }
}
