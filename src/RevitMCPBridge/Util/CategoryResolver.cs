using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge;

/// <summary>
/// Turns the category names a model actually uses ("Walls", "Structural Framing", "OST_Walls")
/// into a <see cref="BuiltInCategory"/>. A model cannot be queried by category without this, and a
/// language model will not reliably guess the OST_ spelling.
/// </summary>
public static class CategoryResolver
{
    private static readonly Lazy<Dictionary<string, BuiltInCategory>> BuiltInByName = new(() =>
    {
        var map = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in Enum.GetValues<BuiltInCategory>())
        {
            var name = value.ToString();
            map[name] = value;                                   // OST_Walls

            if (name.StartsWith("OST_", StringComparison.Ordinal))
                map.TryAdd(name.Substring(4), value);            // Walls
        }

        return map;
    });

    /// <summary>
    /// Resolves a category by the document's own localised names first (what the user sees in the
    /// UI), then by the API's OST_ enum names.
    /// </summary>
    public static Category Resolve(Document doc, string name)
    {
        foreach (Category category in doc.Settings.Categories)
        {
            if (string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase))
                return category;
        }

        if (BuiltInByName.Value.TryGetValue(name, out var builtIn))
        {
            var category = Category.GetCategory(doc, builtIn);
            if (category is not null) return category;

            throw new BridgeException(BridgeErrorCode.NotFound,
                $"Category '{name}' is valid in the Revit API but not present in this document " +
                "(the relevant discipline may be switched off).");
        }

        throw new BridgeException(BridgeErrorCode.NotFound,
            $"'{name}' is not a category in this document. Call LIST_CATEGORIES to see the " +
            "available names.");
    }

    /// <summary>
    /// A collector already filtered to the named category. Subcategories are included, which is
    /// what a user means by "list the walls".
    /// </summary>
    public static FilteredElementCollector CollectInstances(Document doc, string categoryName, View? view = null)
    {
        var category = Resolve(doc, categoryName);

        var collector = view is null
            ? new FilteredElementCollector(doc)
            : new FilteredElementCollector(doc, view.Id);

        return collector
            .WhereElementIsNotElementType()
            .WherePasses(new ElementCategoryFilter(category.Id));
    }
}
