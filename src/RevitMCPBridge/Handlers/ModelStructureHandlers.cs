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
        var omitted = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Category category in doc.Settings.Categories)
        {
            if (onlyModel && category.CategoryType != CategoryType.Model)
            {
                // Track what the filter hid. Annotation categories - revision clouds, dimensions,
                // text notes, tags - are real, queryable categories, and a caller told only about
                // model categories may wrongly conclude they must be found some other way.
                var type = category.CategoryType.ToString();
                omitted[type] = omitted.GetValueOrDefault(type) + 1;
                continue;
            }

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

        var result = Json.Page("categories", rows.Skip(offset).Take(limit), rows.Count, offset, limit);

        if (omitted.Count > 0)
        {
            var summary = new JsonObject();
            foreach (var (type, count) in omitted.OrderBy(e => e.Key, StringComparer.Ordinal))
                summary[type] = count;

            result["omittedByModelOnlyFilter"] = summary;
            result["note"] = "Only model categories are listed. " +
                             $"{omitted.Values.Sum()} further categories exist and are fully " +
                             "queryable, including annotation categories such as Revision Clouds, " +
                             "Dimensions, Text Notes, Grids and tags. Call again with " +
                             "'modelOnly': false to see them.";
        }

        return result;
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

        // Counting elements per workset is what identifies an empty workset, which is the usual
        // finding. One pass over the model beats a filtered collector per workset.
        var includeCounts = context.Args.Bool("includeElementCounts", true);
        var counts = includeCounts ? CountByWorkset(doc) : null;

        var rows = worksets.Select(workset =>
        {
            var row = new JsonObject
            {
                ["id"] = workset.Id.IntegerValue,
                ["name"] = workset.Name,
                ["kind"] = workset.Kind.ToString(),
                ["isOpen"] = workset.IsOpen,
                ["isEditable"] = workset.IsEditable,
                ["isDefaultWorkset"] = workset.IsDefaultWorkset,
                ["owner"] = string.IsNullOrEmpty(workset.Owner) ? null : workset.Owner
            };

            if (counts is not null)
            {
                var count = counts.GetValueOrDefault(workset.Id.IntegerValue);
                row["elementCount"] = count;
                row["isEmpty"] = count == 0;
            }

            return (JsonNode?)row;
        }).ToList();

        var result = Json.Page("worksets", rows.Skip(offset).Take(limit), rows.Count, offset, limit);
        result["isWorkshared"] = true;

        if (counts is not null)
            result["emptyWorksetCount"] = rows.Count(r => r?["isEmpty"]?.GetValue<bool>() == true);

        return result;
    }

    /// <summary>Elements per workset, from a single pass over the model.</summary>
    private static Dictionary<int, int> CountByWorkset(Document doc)
    {
        var counts = new Dictionary<int, int>();

        foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            var id = element.WorksetId.IntegerValue;
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }

        return counts;
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

            // Path reporting is deliberately belt-and-braces. GetAbsolutePath returns an EMPTY
            // ModelPath - not null - for a cloud-hosted link and for one Revit cannot currently
            // resolve, which converts to an empty string and reads as "no path". The stored path
            // is still there in those cases, and a cloud link is identified by its GUIDs instead.
            try
            {
                var reference = linkType.GetExternalFileReference();
                if (reference is not null)
                {
                    row["pathType"] = reference.PathType.ToString();
                    row["absolutePath"] = DescribePath(reference.GetAbsolutePath());
                    row["storedPath"] = DescribePath(reference.GetPath());

                    // 'path' stays the single best answer, so callers need not choose.
                    row["path"] = row["absolutePath"]?.GetValue<string>() is { Length: > 0 } absolute
                        ? absolute
                        : row["storedPath"];
                }
            }
            catch (Exception ex)
            {
                row["pathError"] = ex.Message;
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

    /// <summary>
    /// A ModelPath as text. Returns null rather than an empty string when there is genuinely no
    /// path, and reports a cloud model by its GUIDs, which is the only identity it has.
    /// </summary>
    private static JsonNode? DescribePath(ModelPath? path)
    {
        if (path is null) return null;

        try
        {
            if (path.ServerPath)
            {
                // A cloud or server path has no meaningful file system path; the GUIDs are it.
                var cloud = $"cloud://{path.GetProjectGUID()}/{path.GetModelGUID()}";
                return JsonValue.Create(cloud);
            }

            var visible = ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
            return string.IsNullOrWhiteSpace(visible) ? null : JsonValue.Create(visible);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
