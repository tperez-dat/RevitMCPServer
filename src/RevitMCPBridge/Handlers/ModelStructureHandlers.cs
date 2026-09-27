using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

public sealed class ListCategoriesHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListCategories;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();

        // Default to model categories that actually hold elements; annotation and internal
        // categories otherwise drown out the answer.
        var onlyModel = context.Args.Bool("modelOnly", true);
        var onlyNonEmpty = context.Args.Bool("nonEmptyOnly");

        var categories = new List<Category>();
        foreach (Category category in doc.Settings.Categories)
        {
            if (onlyModel && category.CategoryType != CategoryType.Model) continue;
            categories.Add(category);
        }

        var rows = new List<JsonObject>(categories.Count);
        foreach (var category in categories.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            int? count = null;
            if (onlyNonEmpty || context.Args.Bool("includeCounts"))
            {
                count = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WherePasses(new ElementCategoryFilter(category.Id))
                    .GetElementCount();

                if (onlyNonEmpty && count == 0) continue;
            }

            var row = new JsonObject
            {
                ["id"] = category.Id.Value,
                ["name"] = category.Name,
                ["categoryType"] = category.CategoryType.ToString(),
                ["builtInCategory"] = BuiltInName(category),
                ["hasSubcategories"] = category.SubCategories.Size > 0,
                ["allowsBoundParameters"] = category.AllowsBoundParameters
            };

            if (count.HasValue) row["elementCount"] = count.Value;
            rows.Add(row);
        }

        return Json.Page("categories", rows.Skip(offset).Take(limit), rows.Count, offset, limit);
    }

    private static string? BuiltInName(Category category)
    {
        // Revit 2023+ exposes the enum directly; older code had to cast the id.
        try
        {
            var builtIn = (BuiltInCategory)category.Id.Value;
            return Enum.IsDefined(builtIn) ? builtIn.ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public sealed class ListLevelsHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListLevels;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();

        var levels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .ToList();

        var rows = levels.Select(level => (JsonNode?)new JsonObject
        {
            ["id"] = level.Id.Value,
            ["name"] = level.Name,
            ["elevation"] = Units.Round(level.Elevation),
            ["elevationDisplay"] = ElevationDisplay(level),
            ["projectElevation"] = Units.Round(level.ProjectElevation),
            ["units"] = "feet"
        });

        var result = Json.Page("levels", rows, levels.Count, offset, limit);
        result["note"] = "Elevations are in decimal feet, Revit's internal length unit.";
        return result;
    }

    private static string? ElevationDisplay(Level level) =>
        Units.DisplayValue(level.get_Parameter(BuiltInParameter.LEVEL_ELEV));
}

public sealed class ListPhasesHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListPhases;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var rows = new JsonArray();

        // doc.Phases is already in sequence order, which is what makes phases meaningful.
        var sequence = 0;
        foreach (Phase phase in doc.Phases)
        {
            rows.Add(new JsonObject
            {
                ["id"] = phase.Id.Value,
                ["name"] = phase.Name,
                ["sequenceNumber"] = sequence++
            });
        }

        return new JsonObject
        {
            ["phases"] = rows,
            ["count"] = rows.Count
        };
    }
}

public sealed class ListWorksetsHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListWorksets;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;

        // Asking a non-workshared document for worksets throws, so answer plainly instead.
        if (!doc.IsWorkshared)
        {
            return new JsonObject
            {
                ["isWorkshared"] = false,
                ["worksets"] = new JsonArray(),
                ["count"] = 0,
                ["note"] = "This model is not workshared, so it has no worksets."
            };
        }

        var (limit, offset) = context.Args.Page();
        var kind = context.Args.StringOr("kind", "user");

        var filter = kind.ToLowerInvariant() switch
        {
            "user" => WorksetKind.UserWorkset,
            "standard" => WorksetKind.StandardWorkset,
            "view" => WorksetKind.ViewWorkset,
            "family" => WorksetKind.FamilyWorkset,
            "other" => WorksetKind.OtherWorkset,
            "all" => (WorksetKind?)null,
            _ => throw new BridgeException(BridgeErrorCode.BadRequest,
                $"'kind' must be one of user, standard, view, family, other, all (got '{kind}').")
        };

        var collector = new FilteredWorksetCollector(doc);
        var worksets = (filter.HasValue
                ? collector.OfKind(filter.Value).ToWorksets()
                : collector.ToWorksets())
            .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = worksets.Select(workset => (JsonNode?)new JsonObject
        {
            ["id"] = workset.Id.IntegerValue,
            ["name"] = workset.Name,
            ["kind"] = workset.Kind.ToString(),
            ["isOpen"] = workset.IsOpen,
            ["isEditable"] = workset.IsEditable,
            ["isDefaultWorkset"] = workset.IsDefaultWorkset,
            ["owner"] = workset.Owner
        });

        var result = Json.Page("worksets", rows, worksets.Count, offset, limit);
        result["isWorkshared"] = true;
        return result;
    }
}

public sealed class ListLinkedModelsHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListLinkedModels;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;

        var linkTypes = new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkType))
            .Cast<RevitLinkType>()
            .ToList();

        // Instances carry placement; a type with no instance is a loaded-but-unplaced link.
        var instancesByType = new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .GroupBy(i => i.GetTypeId().Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = new JsonArray();
        foreach (var linkType in linkTypes.OrderBy(t => Json.SafeName(t), StringComparer.OrdinalIgnoreCase))
        {
            var row = new JsonObject
            {
                ["typeId"] = linkType.Id.Value,
                ["name"] = Json.SafeName(linkType),
                ["isNestedLink"] = linkType.IsNestedLink,
                ["attachmentType"] = linkType.AttachmentType.ToString(),
                ["pathType"] = null,
                ["path"] = null,
                ["instanceCount"] = instancesByType.TryGetValue(linkType.Id.Value, out var list) ? list.Count : 0
            };

            try
            {
                var reference = linkType.GetExternalFileReference();
                if (reference is not null)
                {
                    row["pathType"] = reference.PathType.ToString();
                    row["path"] = ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
                }
            }
            catch (Exception)
            {
                // A nested or unloaded link may have no resolvable external reference.
            }

            // GetLinkedFileStatus is the reliable way to tell loaded from unloaded/missing.
            try { row["status"] = linkType.GetLinkedFileStatus().ToString(); }
            catch (Exception) { row["status"] = "Unknown"; }

            rows.Add(row);
        }

        return new JsonObject
        {
            ["links"] = rows,
            ["count"] = rows.Count
        };
    }
}
