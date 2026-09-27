using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

public sealed class ListFamiliesHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListFamilies;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();
        var categoryName = context.Args.StringOrNull("category");
        var nameContains = context.Args.StringOrNull("nameContains");

        ElementId? categoryId = null;
        if (categoryName is not null)
            categoryId = CategoryResolver.Resolve(doc, categoryName).Id;

        var families = new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .Where(f => categoryId is null || f.FamilyCategoryId == categoryId)
            .Where(f => nameContains is null
                        || f.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = families.Select(family => (JsonNode?)new JsonObject
        {
            ["id"] = family.Id.Value,
            ["name"] = family.Name,
            ["category"] = family.FamilyCategory?.Name,
            ["isInPlace"] = family.IsInPlace,
            ["isEditable"] = family.IsEditable,
            // Symbol ids let a caller go straight to placement without a second lookup.
            ["typeCount"] = family.GetFamilySymbolIds().Count
        });

        var result = Json.Page("families", rows, families.Count, offset, limit);
        result["note"] = "System families (walls, floors, roofs) are not Family elements; " +
                         "use LIST_FAMILY_TYPES to see their types.";
        return result;
    }
}

public sealed class ListFamilyTypesHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListFamilyTypes;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();

        var familyName = context.Args.StringOrNull("familyName");
        var categoryName = context.Args.StringOrNull("category");
        var nameContains = context.Args.StringOrNull("nameContains");

        ElementId? categoryId = null;
        if (categoryName is not null)
            categoryId = CategoryResolver.Resolve(doc, categoryName).Id;

        // ElementType covers both loadable FamilySymbols and system types such as WallType,
        // which is what a caller asking for "the wall types" expects to find.
        var types = new FilteredElementCollector(doc)
            .WhereElementIsElementType()
            .Cast<ElementType>()
            .Where(t => familyName is null
                        || string.Equals(t.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
            .Where(t => categoryId is null || t.Category?.Id == categoryId)
            .Where(t => nameContains is null
                        || (Json.SafeName(t)?.Contains(nameContains, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(t => t.FamilyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => Json.SafeName(t), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = types.Select(type =>
        {
            var row = new JsonObject
            {
                ["id"] = type.Id.Value,
                ["name"] = Json.SafeName(type),
                ["familyName"] = type.FamilyName,
                ["category"] = type.Category?.Name,
                ["class"] = type.GetType().Name
            };

            // A loadable symbol must be activated before it can be placed; surfacing this saves a
            // caller from a silent placement failure.
            if (type is FamilySymbol symbol)
            {
                row["isFamilySymbol"] = true;
                row["isActive"] = symbol.IsActive;
            }

            return (JsonNode?)row;
        });

        var result = Json.Page("types", rows, types.Count, offset, limit);
        result["familyNameFilter"] = familyName;
        return result;
    }
}

public sealed class ListMaterialsHandler : IBridgeCommandHandler
{
    public string Command => Commands.ListMaterials;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var (limit, offset) = context.Args.Page();
        var nameContains = context.Args.StringOrNull("nameContains");

        var materials = new FilteredElementCollector(doc)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .Where(m => nameContains is null
                        || m.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = materials.Select(material => (JsonNode?)new JsonObject
        {
            ["id"] = material.Id.Value,
            ["name"] = material.Name,
            ["materialClass"] = material.MaterialClass,
            ["materialCategory"] = material.MaterialCategory,
            ["color"] = MaterialJson.ColorJson(material.Color)
        });

        return Json.Page("materials", rows, materials.Count, offset, limit);
    }
}

public sealed class GetMaterialPropertiesHandler : IBridgeCommandHandler
{
    public string Command => Commands.GetMaterialProperties;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var material = Resolve(context);

        var result = new JsonObject
        {
            ["id"] = material.Id.Value,
            ["name"] = material.Name,
            ["materialClass"] = material.MaterialClass,
            ["materialCategory"] = material.MaterialCategory,
            ["color"] = MaterialJson.ColorJson(material.Color),
            ["transparency"] = material.Transparency,
            ["shininess"] = material.Shininess,
            ["smoothness"] = material.Smoothness,
            ["useRenderAppearanceForShading"] = material.UseRenderAppearanceForShading,
            ["surfaceForegroundPatternId"] = IdOrNull(material.SurfaceForegroundPatternId),
            ["cutForegroundPatternId"] = IdOrNull(material.CutForegroundPatternId),
            ["structuralAssetId"] = IdOrNull(material.StructuralAssetId),
            ["thermalAssetId"] = IdOrNull(material.ThermalAssetId),
            ["appearanceAssetId"] = IdOrNull(material.AppearanceAssetId),
            ["parameters"] = Json.Parameters(material)
        };

        // Structural and thermal assets are separate elements; inline the values an engineer wants.
        result["structuralAsset"] = StructuralAssetJson(doc, material.StructuralAssetId);
        result["thermalAsset"] = ThermalAssetJson(doc, material.ThermalAssetId);
        return result;
    }

    private static Material Resolve(CommandContext context)
    {
        var doc = context.Doc;

        if (context.Args.LongOrNull("materialId") is { } id)
        {
            return doc.GetElement(new ElementId(id)) as Material
                   ?? throw new BridgeException(BridgeErrorCode.NotFound,
                       $"Element {id} is not a material.");
        }

        var name = context.Args.StringOrNull("name")
                   ?? throw new BridgeException(BridgeErrorCode.BadRequest,
                       "Pass 'materialId' or 'name' to identify the material.");

        var matches = new FilteredElementCollector(doc)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new BridgeException(BridgeErrorCode.NotFound,
                $"No material is named '{name}'. Call LIST_MATERIALS to see what exists."),
            _ => throw new BridgeException(BridgeErrorCode.BadRequest,
                $"'{name}' matches {matches.Count} materials. Pass 'materialId' instead.")
        };
    }

    private static long? IdOrNull(ElementId id) =>
        id == ElementId.InvalidElementId ? null : id.Value;

    private static JsonNode? StructuralAssetJson(Document doc, ElementId id)
    {
        if (doc.GetElement(id) is not PropertySetElement element) return null;

        try
        {
            var asset = element.GetStructuralAsset();
            return new JsonObject
            {
                ["name"] = Json.SafeName(element),
                ["behavior"] = asset.Behavior.ToString(),
                ["density"] = Units.Round(asset.Density),
                ["youngModulusX"] = Units.Round(asset.YoungModulus.X),
                ["poissonRatioX"] = Units.Round(asset.PoissonRatio.X),
                ["thermalExpansionCoefficientX"] = asset.ThermalExpansionCoefficient.X,
                ["note"] = "Values are in Revit internal units."
            };
        }
        catch (Exception)
        {
            // Not every PropertySetElement carries a structural asset.
            return null;
        }
    }

    private static JsonNode? ThermalAssetJson(Document doc, ElementId id)
    {
        if (doc.GetElement(id) is not PropertySetElement element) return null;

        try
        {
            var asset = element.GetThermalAsset();
            return new JsonObject
            {
                ["name"] = Json.SafeName(element),
                ["thermalConductivity"] = Units.Round(asset.ThermalConductivity),
                ["specificHeat"] = Units.Round(asset.SpecificHeat),
                ["density"] = Units.Round(asset.Density),
                ["emissivity"] = Units.Round(asset.Emissivity),
                ["note"] = "Values are in Revit internal units."
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}

internal static class MaterialJson
{
    public static JsonNode? ColorJson(Color? color)
    {
        if (color is null || !color.IsValid) return null;

        return new JsonObject
        {
            ["red"] = color.Red,
            ["green"] = color.Green,
            ["blue"] = color.Blue,
            ["hex"] = $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}"
        };
    }
}
