using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// The keynote table and how the model actually uses it.
///
/// Keynotes are the one annotation system whose content lives outside the model, in a text file the
/// project shares. Three things go wrong and all three are invisible in the model: the file is
/// missing or points somewhere local, a tag references a key the file no longer contains, and
/// elements carry a keynote value that nothing tags. All three are reported here.
/// </summary>
public sealed class ListKeynotesHandler : IBridgeCommandHandler
{
    /// <summary>Elements inspected for an assigned keynote value, so a large model cannot stall.</summary>
    private const int MaxElementsScanned = 100_000;

    public string Command => Commands.ListKeynotes;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;
        var (limit, offset) = args.Page();

        var table = KeynoteTable.GetKeynoteTable(doc);

        // The entries: key, text, and where each sits in the tree. A keynote file is hierarchical,
        // and a key with no parent is a heading rather than a note.
        var entries = ReadEntries(table, out var entriesError);

        // How often each key is used, by keynote tags and by elements carrying a keynote value.
        var tagUses = CountTagUses(doc);
        var elementUses = CountElementUses(doc, out var scanned, out var scanTruncated);

        var rows = new List<JsonObject>();
        foreach (var entry in entries)
        {
            tagUses.TryGetValue(entry.Key, out var tagCount);
            elementUses.TryGetValue(entry.Key, out var elementCount);

            rows.Add(new JsonObject
            {
                ["key"] = entry.Key,
                ["text"] = entry.Text,
                ["parentKey"] = entry.ParentKey is { Length: > 0 } p ? p : null,
                ["isHeading"] = entry.ChildCount > 0,
                ["childCount"] = entry.ChildCount,
                ["tagCount"] = tagCount,
                ["elementCount"] = elementCount,
                ["isUsed"] = tagCount > 0 || elementCount > 0
            });
        }

        // Keys referenced by the model that the table does not define. These tag as a blank or an
        // error on the sheet, and are the finding most worth acting on.
        var defined = entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var orphans = new JsonArray();

        foreach (var key in tagUses.Keys.Concat(elementUses.Keys).Distinct(StringComparer.Ordinal)
                     .Where(k => !defined.Contains(k))
                     .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            tagUses.TryGetValue(key, out var tagCount);
            elementUses.TryGetValue(key, out var elementCount);
            orphans.Add(new JsonObject
            {
                ["key"] = key,
                ["tagCount"] = tagCount,
                ["elementCount"] = elementCount
            });
        }

        var ordered = rows
            .OrderBy(r => r["key"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var page = Json.Page("keynotes", ordered.Skip(offset).Take(limit).Select(r => (JsonNode?)r),
            ordered.Count, offset, limit);

        page["table"] = DescribeTable(table);
        page["entryCount"] = ordered.Count;
        page["usedCount"] = ordered.Count(r => r["isUsed"]!.GetValue<bool>());
        page["unusedCount"] = ordered.Count(r => !r["isUsed"]!.GetValue<bool>());
        page["keynoteTagCount"] = tagUses.Values.Sum();
        page["elementsWithKeynote"] = elementUses.Values.Sum();
        page["undefinedKeys"] = orphans;
        page["undefinedKeyCount"] = orphans.Count;
        page["elementsScanned"] = scanned;

        if (entriesError is not null) page["entriesError"] = entriesError;

        if (scanTruncated)
        {
            page["truncated"] = true;
            page["note"] = $"Stopped after {MaxElementsScanned:N0} elements, so 'elementCount' is a " +
                           "lower bound.";
        }

        return page;
    }

    /// <summary>
    /// The keynote file itself. A table with no external reference means no keynote file is loaded,
    /// and every keynote in the model will tag blank.
    /// </summary>
    private static JsonObject DescribeTable(KeynoteTable? table)
    {
        if (table is null)
            return new JsonObject { ["isLoaded"] = false, ["path"] = null };

        var o = new JsonObject
        {
            ["id"] = table.Id.Value,
            ["isLoaded"] = false,
            ["path"] = null,
            ["pathType"] = null
        };

        try
        {
            if (table.IsExternalFileReference() && table.GetExternalFileReference() is { } reference)
            {
                o["isLoaded"] = true;
                o["pathType"] = reference.PathType.ToString();
                o["status"] = reference.GetLinkedFileStatus().ToString();

                // Same belt-and-braces as the link paths: the absolute path can come back empty
                // while the stored path is still meaningful.
                var absolute = Describe(reference.GetAbsolutePath());
                var stored = Describe(reference.GetPath());

                o["absolutePath"] = absolute;
                o["storedPath"] = stored;
                o["path"] = absolute ?? stored;
            }
        }
        catch (Exception ex)
        {
            o["pathError"] = ex.Message;
        }

        return o;

        static string? Describe(ModelPath? path)
        {
            if (path is null) return null;
            try
            {
                var visible = ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
                return string.IsNullOrWhiteSpace(visible) ? null : visible;
            }
            catch (Exception) { return null; }
        }
    }

    private sealed record Entry(string Key, string? Text, string? ParentKey, int ChildCount);

    /// <summary>
    /// The table's entries. Each is a <see cref="KeyBasedTreeEntry"/>; only a
    /// <see cref="KeynoteEntry"/> carries the note text, so the cast is what gets the content.
    /// </summary>
    private static List<Entry> ReadEntries(KeynoteTable? table, out string? error)
    {
        error = null;
        var entries = new List<Entry>();
        if (table is null) return entries;

        try
        {
            foreach (var item in table.GetKeyBasedTreeEntries())
            {
                if (item is null) continue;

                var children = 0;
                try { children = item.GetChildrenKeys()?.Count ?? 0; }
                catch (Exception) { /* leaf */ }

                entries.Add(new Entry(
                    item.Key,
                    item is KeynoteEntry keynote ? keynote.KeynoteText : null,
                    item.ParentKey,
                    children));
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return entries;
    }

    /// <summary>
    /// Keynote tags by the key each displays. Keynote tags are their own category, which is the
    /// reliable way to find them — TagMode no longer distinguishes them.
    /// </summary>
    private static Dictionary<string, int> CountTagUses(Document doc)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var element in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_KeynoteTags)
                     .WhereElementIsNotElementType())
        {
            var key = KeyOf(element);
            if (key is null) continue;
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        return counts;
    }

    /// <summary>
    /// Elements that carry a keynote value, whether or not anything tags them. An assigned keynote
    /// with no tag is dormant: correct, invisible, and easy to lose in a type swap.
    /// </summary>
    private static Dictionary<string, int> CountElementUses(Document doc, out int scanned,
        out bool truncated)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        scanned = 0;
        truncated = false;

        foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            if (++scanned > MaxElementsScanned) { truncated = true; break; }
            if (element.Category?.Id.Value == (long)BuiltInCategory.OST_KeynoteTags) continue;

            var key = KeyOf(element);
            if (key is null) continue;
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        // A keynote is normally assigned on the type, so types count too — under the same cap, so
        // neither half of the scan is unbounded.
        foreach (var element in new FilteredElementCollector(doc).WhereElementIsElementType())
        {
            if (++scanned > MaxElementsScanned) { truncated = true; break; }

            var key = KeyOf(element);
            if (key is null) continue;
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        return counts;
    }

    /// <summary>The keynote key assigned to an element, or null when it has none.</summary>
    private static string? KeyOf(Element element)
    {
        try
        {
            var parameter = element.get_Parameter(BuiltInParameter.KEYNOTE_PARAM);
            if (parameter is null || !parameter.HasValue) return null;

            var value = parameter.AsString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
