using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// Where this model sits in space: survey point, project base point, shared coordinates, and the
/// geographic site.
///
/// This is the first thing to check when links land in the wrong place, and the one check that
/// cannot be answered by looking at elements — the answer lives on the project itself. A model
/// whose survey point has been moved, or whose base point is unclipped, will misplace every link
/// placed by shared coordinates, and nothing in the model's own geometry gives that away.
/// </summary>
public sealed class GetProjectCoordinatesHandler : IBridgeCommandHandler
{
    public string Command => Commands.GetProjectCoordinates;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;

        var basePoint = DescribePoint(doc, isSurvey: false);
        var surveyPoint = DescribePoint(doc, isSurvey: true);
        var site = DescribeSite(doc);

        var result = new JsonObject
        {
            ["projectBasePoint"] = basePoint,
            ["surveyPoint"] = surveyPoint,
            ["activeLocation"] = DescribeActiveLocation(doc),
            ["namedLocations"] = DescribeNamedLocations(doc),
            ["site"] = site,

            // Flat, so the two facts a dashboard wants do not have to be dug out of the nesting.
            ["isGeoReferenced"] = site?["isGeoReferenced"]?.GetValue<bool>() ?? false,
            ["surveyPointClipped"] = surveyPoint?["clipped"]?.GetValue<bool?>(),
            ["projectBasePointClipped"] = basePoint?["clipped"]?.GetValue<bool?>(),

            ["note"] =
                "Positions are in decimal feet. 'position' is relative to the project's internal " +
                "origin; 'sharedPosition' is in shared coordinates, which is what a link placed " +
                "by shared coordinates uses. A clipped survey point is pinned to shared " +
                "coordinates and moving it moves the shared system for everyone; an unclipped one " +
                "moves on its own. Check 'clipped' before trusting either number."
        };

        return result;
    }

    /// <summary>
    /// The base point or survey point. Both are <see cref="BasePoint"/> elements, fetched through
    /// the static accessors rather than a category collector, because a model can also contain
    /// internal-origin base points that would otherwise be mixed in.
    /// </summary>
    private static JsonNode? DescribePoint(Document doc, bool isSurvey)
    {
        BasePoint? point;
        try
        {
            point = isSurvey ? BasePoint.GetSurveyPoint(doc) : BasePoint.GetProjectBasePoint(doc);
        }
        catch (Exception ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }

        if (point is null) return null;

        var o = new JsonObject
        {
            ["id"] = point.Id.Value,
            // Clipped means pinned to shared coordinates. Unclipping a survey point and dragging it
            // is the classic way a project loses its relationship to the site survey.
            ["clipped"] = Safe(() => (JsonNode?)JsonValue.Create(point.Clipped)),
            ["pinned"] = Safe(() => (JsonNode?)JsonValue.Create(point.Pinned)),
            ["position"] = Safe(() => PointJson(point.Position)),
            ["sharedPosition"] = Safe(() => PointJson(point.SharedPosition))
        };

        // The angle to true north and the named elevation base live on the element's parameters,
        // not on properties, so they are read by built-in id.
        o["angleToTrueNorth"] = ParameterDisplay(point, BuiltInParameter.BASEPOINT_ANGLETON_PARAM);
        o["elevationBase"] = ParameterDisplay(point, BuiltInParameter.BASEPOINT_ELEVATION_PARAM);

        return o;
    }

    /// <summary>
    /// The active project location: the named coordinate system in effect, and the shared position
    /// of the internal origin under it. The second is the single number that tells you whether two
    /// models agree on where the world is.
    /// </summary>
    private static JsonNode? DescribeActiveLocation(Document doc)
    {
        try
        {
            var location = doc.ActiveProjectLocation;
            if (location is null) return null;

            var o = new JsonObject
            {
                ["id"] = location.Id.Value,
                ["name"] = Json.SafeName(location)
            };

            // The position of the internal origin expressed in the active shared system. Two models
            // that are coordinated agree on this; two that are not, do not.
            try
            {
                var position = location.GetProjectPosition(XYZ.Zero);
                o["internalOriginSharedPosition"] = new JsonObject
                {
                    ["eastWest"] = Units.Round(position.EastWest),
                    ["northSouth"] = Units.Round(position.NorthSouth),
                    ["elevation"] = Units.Round(position.Elevation),
                    ["angleRadians"] = Math.Round(position.Angle, 9),
                    ["angleDegrees"] = Math.Round(position.Angle * 180.0 / Math.PI, 6),
                    ["units"] = "feet"
                };
            }
            catch (Exception ex)
            {
                o["positionError"] = ex.Message;
            }

            return o;
        }
        catch (Exception ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }
    }

    /// <summary>
    /// Every named project location. More than one means the model carries alternative coordinate
    /// systems, which is legitimate but is also how a model ends up published under the wrong one.
    /// </summary>
    private static JsonNode DescribeNamedLocations(Document doc)
    {
        var array = new JsonArray();

        try
        {
            var activeId = doc.ActiveProjectLocation?.Id;

            foreach (ProjectLocation location in doc.ProjectLocations)
            {
                array.Add(new JsonObject
                {
                    ["id"] = location.Id.Value,
                    ["name"] = Json.SafeName(location),
                    ["isActive"] = activeId is not null && location.Id == activeId
                });
            }
        }
        catch (Exception)
        {
            // A model with no location set has nothing to enumerate.
        }

        return array;
    }

    /// <summary>
    /// The geographic site: latitude, longitude, the named place, and the geo-referencing system if
    /// one is assigned. Latitude and longitude are stored in radians and reported in degrees, which
    /// is the only form anyone can check against a map.
    /// </summary>
    private static JsonNode? DescribeSite(Document doc)
    {
        try
        {
            var site = doc.SiteLocation;
            if (site is null) return null;

            var o = new JsonObject
            {
                ["id"] = site.Id.Value,
                ["placeName"] = Safe(() => (JsonNode?)JsonValue.Create(site.PlaceName)),
                ["latitudeDegrees"] = Safe(() => (JsonNode?)JsonValue.Create(
                    Math.Round(site.Latitude * 180.0 / Math.PI, 8))),
                ["longitudeDegrees"] = Safe(() => (JsonNode?)JsonValue.Create(
                    Math.Round(site.Longitude * 180.0 / Math.PI, 8))),
                ["elevation"] = Safe(() => (JsonNode?)JsonValue.Create(Units.Round(site.Elevation))),
                ["timeZone"] = Safe(() => (JsonNode?)JsonValue.Create(site.TimeZone)),
                ["weatherStation"] = Safe(() => (JsonNode?)JsonValue.Create(site.WeatherStationName))
            };

            // An empty coordinate system id means the model is not geo-referenced. That is a
            // reportable state, not an error, so it comes back as null rather than being omitted.
            var systemId = Safe(() => (JsonNode?)JsonValue.Create(site.GeoCoordinateSystemId));
            o["geoCoordinateSystemId"] = systemId?.GetValue<string>() is { Length: > 0 } id ? id : null;
            o["isGeoReferenced"] = o["geoCoordinateSystemId"] is not null;

            return o;
        }
        catch (Exception ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }
    }

    private static JsonNode PointJson(XYZ p) => new JsonObject
    {
        ["x"] = Units.Round(p.X),
        ["y"] = Units.Round(p.Y),
        ["z"] = Units.Round(p.Z),
        ["units"] = "feet"
    };

    private static JsonNode? ParameterDisplay(Element element, BuiltInParameter id)
    {
        try
        {
            var parameter = element.get_Parameter(id);
            if (parameter is null) return null;

            return new JsonObject
            {
                ["value"] = Json.From(Units.RawValue(parameter)),
                ["displayValue"] = Units.DisplayValue(parameter)
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Several of these properties throw rather than report absence, and one unavailable field must
    /// not cost the whole answer.
    /// </summary>
    private static JsonNode? Safe(Func<JsonNode?> read)
    {
        try { return read(); }
        catch (Exception) { return null; }
    }
}
