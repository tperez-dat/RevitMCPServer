using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// Reports what Revit's own Purge Unused would remove.
///
/// Uses Document.GetUnusedElements rather than inferring "a type with no instances", which is the
/// usual approximation and is wrong in both directions: it misses unused line patterns, fill
/// patterns and materials, and it falsely condemns types referenced only by a schedule or a filter.
/// </summary>
public sealed class GetPurgeableHandler : IBridgeCommandHandler
{
    public string Command => Commands.GetPurgeable;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();

        ICollection<ElementId> unused;
        try
        {
            // An empty filter set means "consider everything", which is what Purge Unused does.
            unused = doc.GetUnusedElements(new HashSet<ElementId>());
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            throw new BridgeException(BridgeErrorCode.RevitException,
                "Revit could not compute the purgeable set.", ex.Message);
        }

        // Grouping by category is what makes this actionable: "412 purgeable" means nothing,
        // "380 of them are line patterns" tells you where the bloat came from.
        var byCategory = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<JsonObject>();

        foreach (var id in unused)
        {
            var element = doc.GetElement(id);
            if (element is null) continue;

            var bucket = element.Category?.Name ?? element.GetType().Name;
            byCategory[bucket] = byCategory.GetValueOrDefault(bucket) + 1;

            rows.Add(new JsonObject
            {
                ["id"] = id.Value,
                ["name"] = Json.SafeName(element),
                ["category"] = element.Category?.Name,
                ["class"] = element.GetType().Name
            });
        }

        var ordered = rows.OrderBy(r => r["category"]?.GetValue<string>() ?? "",
                                   StringComparer.OrdinalIgnoreCase)
                          .ThenBy(r => r["name"]?.GetValue<string>() ?? "",
                                  StringComparer.OrdinalIgnoreCase)
                          .ToList();

        var result = Json.Page("purgeable", ordered.Skip(offset).Take(limit).Select(r => (JsonNode?)r),
            ordered.Count, offset, limit);

        var summary = new JsonObject();
        foreach (var (name, count) in byCategory.OrderByDescending(e => e.Value))
            summary[name] = count;

        result["totalPurgeable"] = ordered.Count;
        result["byCategory"] = summary;
        result["note"] = "This is what Revit's Purge Unused would offer to remove. Purging is a " +
                         "manual step; nothing here changes the model.";
        return result;
    }
}

/// <summary>
/// DWG/DXF content in the model, separating imports from links.
///
/// The distinction matters: a link can be reloaded or unloaded, whereas an import is baked in and
/// drags its line patterns and text styles into the project permanently. Imports are the ones that
/// bloat a model, and an import placed on a 3D view rather than a single view is worse still.
/// </summary>
public sealed class GetCadImportsHandler : IBridgeCommandHandler
{
    public string Command => Commands.GetCadImports;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();

        var instances = new FilteredElementCollector(doc)
            .OfClass(typeof(ImportInstance))
            .Cast<ImportInstance>()
            .ToList();

        var rows = new List<JsonObject>();
        var imported = 0;
        var linked = 0;

        foreach (var instance in instances)
        {
            var isLink = instance.IsLinked;
            if (isLink) linked++; else imported++;

            var row = new JsonObject
            {
                ["id"] = instance.Id.Value,
                ["name"] = Json.SafeName(instance),
                ["isLinked"] = isLink,
                ["kind"] = isLink ? "link" : "import",
                ["isViewSpecific"] = instance.ViewSpecific,
                ["ownerView"] = instance.ViewSpecific
                    ? Json.SafeName(doc.GetElement(instance.OwnerViewId))
                    : null,
                ["pinned"] = instance.Pinned
            };

            // The type carries the source file; the instance only knows it was placed.
            if (doc.GetElement(instance.GetTypeId()) is CADLinkType type)
            {
                row["typeId"] = type.Id.Value;
                row["typeName"] = Json.SafeName(type);

                try
                {
                    var reference = type.GetExternalFileReference();
                    if (reference is not null)
                    {
                        row["pathType"] = reference.PathType.ToString();
                        var path = ModelPathUtils.ConvertModelPathToUserVisiblePath(
                            reference.GetAbsolutePath());
                        row["path"] = string.IsNullOrWhiteSpace(path) ? null : path;
                    }
                }
                catch (Exception)
                {
                    // An import has no external reference once it is baked in.
                }
            }

            rows.Add(row);
        }

        var ordered = rows.OrderBy(r => r["isLinked"]!.GetValue<bool>())   // imports first: worse
                          .ThenBy(r => r["name"]?.GetValue<string>() ?? "",
                                  StringComparer.OrdinalIgnoreCase)
                          .ToList();

        var result = Json.Page("cadInstances", ordered.Skip(offset).Take(limit).Select(r => (JsonNode?)r),
            ordered.Count, offset, limit);

        result["importedCount"] = imported;
        result["linkedCount"] = linked;
        result["note"] = "Imports are listed first. An import is permanent and brings its line " +
                         "patterns and text styles with it; a link can be unloaded or reloaded.";
        return result;
    }
}

/// <summary>Raster images and PDFs placed in the model, a common and invisible source of file size.</summary>
public sealed class ListImagesHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListImages;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();

        var types = new FilteredElementCollector(doc)
            .OfClass(typeof(ImageType))
            .Cast<ImageType>()
            .ToList();

        // Placements are separate elements, so a type with none is an unused image still in the file.
        var placementsByType = new FilteredElementCollector(doc)
            .OfClass(typeof(ImageInstance))
            .Cast<ImageInstance>()
            .GroupBy(i => i.GetTypeId().Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = types.Select(type =>
        {
            var placements = placementsByType.TryGetValue(type.Id.Value, out var list) ? list : [];

            var row = new JsonObject
            {
                ["id"] = type.Id.Value,
                ["name"] = Json.SafeName(type),
                ["placementCount"] = placements.Count,
                ["isUnplaced"] = placements.Count == 0
            };

            try { row["path"] = type.Path; }
            catch (Exception) { row["path"] = null; }

            // Linked vs embedded is not exposed on ImageType in a way worth relying on, so it is
            // deliberately not reported rather than guessed at. Path and placement count are the
            // parts that identify an unused or oversized image anyway.

            return (JsonNode?)row;
        }).ToList();

        var result = Json.Page("images", rows.Skip(offset).Take(limit), rows.Count, offset, limit);
        result["unplacedCount"] = rows.Count(r => r?["isUnplaced"]?.GetValue<bool>() == true);
        return result;
    }
}

/// <summary>
/// Model and detail groups with instance counts. A group type with no instances is dead weight,
/// and a group used once is usually a mistake rather than a reuse strategy.
/// </summary>
public sealed class ListGroupsHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListGroups;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();

        var instancesByType = new FilteredElementCollector(doc)
            .OfClass(typeof(Group))
            .Cast<Group>()
            .GroupBy(g => g.GetTypeId().Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = new FilteredElementCollector(doc)
            .OfClass(typeof(GroupType))
            .Cast<GroupType>()
            .Select(type =>
            {
                var instances = instancesByType.TryGetValue(type.Id.Value, out var list) ? list : [];
                var isDetail = type.Category?.Id.Value == (long)BuiltInCategory.OST_IOSDetailGroups;

                return new JsonObject
                {
                    ["id"] = type.Id.Value,
                    ["name"] = Json.SafeName(type),
                    ["kind"] = isDetail ? "detail" : "model",
                    ["category"] = type.Category?.Name,
                    ["instanceCount"] = instances.Count,
                    ["isUnused"] = instances.Count == 0,
                    ["usedOnce"] = instances.Count == 1,
                    ["memberCount"] = MemberCount(instances.FirstOrDefault())
                };
            })
            .OrderByDescending(r => r["instanceCount"]!.GetValue<int>())
            .ToList();

        var result = Json.Page("groups", rows.Skip(offset).Take(limit).Select(r => (JsonNode?)r),
            rows.Count, offset, limit);

        result["unusedCount"] = rows.Count(r => r["isUnused"]!.GetValue<bool>());
        result["usedOnceCount"] = rows.Count(r => r["usedOnce"]!.GetValue<bool>());
        result["totalInstances"] = rows.Sum(r => r["instanceCount"]!.GetValue<int>());
        return result;
    }

    private static int? MemberCount(Group? instance)
    {
        try { return instance?.GetMemberIds().Count; }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// In-place families. They cannot be reused across projects, schedule poorly, and each one is a
/// separate family definition in the file, so a model with many is both heavy and hard to maintain.
/// </summary>
public sealed class ListInPlaceFamiliesHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListInPlaceFamilies;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();

        var inPlace = new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .Where(f => f.IsInPlace)
            .ToList();

        // Count placements per family so a reviewer can tell a one-off from something load-bearing.
        var instanceCounts = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(i => i.Symbol?.Family is not null)
            .GroupBy(i => i.Symbol.Family.Id.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var rows = inPlace
            .Select(family => (JsonNode?)new JsonObject
            {
                ["id"] = family.Id.Value,
                ["name"] = family.Name,
                ["category"] = family.FamilyCategory?.Name,
                ["typeCount"] = family.GetFamilySymbolIds().Count,
                ["instanceCount"] = instanceCounts.GetValueOrDefault(family.Id.Value)
            })
            .OrderBy(r => r!["category"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r!["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = Json.Page("families", rows.Skip(offset).Take(limit), rows.Count, offset, limit);
        result["totalInPlace"] = rows.Count;
        result["note"] = "In-place families cannot be reused in another project and schedule poorly. " +
                         "A high count usually indicates modelling that belongs in loadable families.";
        return result;
    }
}

/// <summary>Design option sets and their options, with how many elements sit in each.</summary>
public sealed class ListDesignOptionsHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListDesignOptions;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;

        var options = new FilteredElementCollector(doc)
            .OfClass(typeof(DesignOption))
            .Cast<DesignOption>()
            .ToList();

        if (options.Count == 0)
        {
            return new JsonObject
            {
                ["sets"] = new JsonArray(),
                ["count"] = 0,
                ["note"] = "This model has no design options."
            };
        }

        // Group options under their set, which is how Revit presents them and how a reviewer thinks.
        var sets = new Dictionary<long, JsonObject>();

        foreach (var option in options.OrderBy(o => Json.SafeName(o), StringComparer.OrdinalIgnoreCase))
        {
            var setId = option.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId()
                        ?? ElementId.InvalidElementId;

            if (!sets.TryGetValue(setId.Value, out var set))
            {
                set = new JsonObject
                {
                    ["setId"] = setId.Value,
                    ["setName"] = Json.SafeName(doc.GetElement(setId)),
                    ["options"] = new JsonArray()
                };
                sets[setId.Value] = set;
            }

            // Elements belonging to an option are found by filtering on it directly.
            var elementCount = new FilteredElementCollector(doc)
                .WherePasses(new ElementDesignOptionFilter(option.Id))
                .WhereElementIsNotElementType()
                .GetElementCount();

            ((JsonArray)set["options"]!).Add(new JsonObject
            {
                ["id"] = option.Id.Value,
                ["name"] = Json.SafeName(option),
                ["isPrimary"] = option.IsPrimary,
                ["elementCount"] = elementCount
            });
        }

        var array = new JsonArray();
        foreach (var set in sets.Values) array.Add(set);

        return new JsonObject
        {
            ["sets"] = array,
            ["setCount"] = sets.Count,
            ["optionCount"] = options.Count
        };
    }
}

/// <summary>
/// Views not placed on any sheet. A standard QC metric: views accumulate during design and an
/// unplaced one is either work in progress or forgotten clutter that still costs open time.
/// </summary>
public sealed class ListUnplacedViewsHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListUnplacedViews;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();

        // One pass over viewports gives every placed view, rather than a lookup per view.
        var placed = new FilteredElementCollector(doc)
            .OfClass(typeof(Viewport))
            .Cast<Viewport>()
            .Select(v => v.ViewId.Value)
            .ToHashSet();

        // Schedules are placed by a different element and would otherwise all look unplaced.
        foreach (var instance in new FilteredElementCollector(doc)
                     .OfClass(typeof(ScheduleSheetInstance))
                     .Cast<ScheduleSheetInstance>())
        {
            placed.Add(instance.ScheduleId.Value);
        }

        var candidates = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => !v.IsTemplate)
            .Where(v => v is not ViewSheet)                       // a sheet is not placed on a sheet
            .Where(v => v.ViewType != ViewType.DrawingSheet)
            .Where(v => v.ViewType is not (ViewType.ProjectBrowser or ViewType.SystemBrowser
                        or ViewType.Internal or ViewType.Undefined))
            .Where(v => !placed.Contains(v.Id.Value))
            .ToList();

        var byType = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var view in candidates)
        {
            var key = view.ViewType.ToString();
            byType[key] = byType.GetValueOrDefault(key) + 1;
        }

        var rows = candidates
            .OrderBy(v => v.ViewType.ToString(), StringComparer.Ordinal)
            .ThenBy(v => Json.SafeName(v), StringComparer.OrdinalIgnoreCase)
            .Select(v => (JsonNode?)new JsonObject
            {
                ["id"] = v.Id.Value,
                ["name"] = Json.SafeName(v),
                ["viewType"] = v.ViewType.ToString(),
                ["isDependent"] = v.GetPrimaryViewId() != ElementId.InvalidElementId
            });

        var result = Json.Page("views", rows.Skip(offset).Take(limit), candidates.Count, offset, limit);

        var summary = new JsonObject();
        foreach (var (type, count) in byType.OrderByDescending(e => e.Value)) summary[type] = count;

        result["totalUnplaced"] = candidates.Count;
        result["byViewType"] = summary;
        result["note"] = "Sheets, view templates and Revit's internal browser views are excluded. " +
                         "Schedules placed by a schedule instance count as placed.";
        return result;
    }
}
