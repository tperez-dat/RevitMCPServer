using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge;

/// <summary>Curve and loop construction with the validation Revit's own factories assume.</summary>
public static class Geometry
{
    /// <summary>
    /// Revit rejects a curve shorter than its own short-curve tolerance, with a message that does
    /// not say which points were at fault. Check it here so the caller gets a useful failure.
    /// </summary>
    public static Line BoundLine(Document doc, XYZ start, XYZ end, string what)
    {
        var tolerance = doc.Application.ShortCurveTolerance;
        var distance = start.DistanceTo(end);

        if (distance <= tolerance)
        {
            throw new BridgeException(BridgeErrorCode.BadRequest,
                $"{what}: start and end are {distance:0.#####} ft apart, which is at or below " +
                $"Revit's short-curve tolerance ({tolerance:0.#####} ft). Give two distinct points.");
        }

        return Line.CreateBound(start, end);
    }

    /// <summary>
    /// Builds a closed loop from a polygon's vertices. The last point may repeat the first; it is
    /// dropped rather than producing a zero-length segment.
    /// </summary>
    public static CurveLoop ClosedLoop(Document doc, IList<XYZ> points, string what)
    {
        var vertices = new List<XYZ>(points);

        if (vertices.Count > 2
            && vertices[0].DistanceTo(vertices[^1]) <= doc.Application.ShortCurveTolerance)
        {
            vertices.RemoveAt(vertices.Count - 1);
        }

        if (vertices.Count < 3)
        {
            throw new BridgeException(BridgeErrorCode.BadRequest,
                $"{what}: a closed boundary needs at least 3 distinct points (got {vertices.Count}).");
        }

        var loop = new CurveLoop();
        for (var i = 0; i < vertices.Count; i++)
        {
            var next = (i + 1) % vertices.Count;
            try
            {
                loop.Append(BoundLine(doc, vertices[i], vertices[next], $"{what} segment {i + 1}"));
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                // Appending fails when a segment does not meet the previous one end-to-end, which
                // for a polygon means the points are not in order around the boundary.
                throw new BridgeException(BridgeErrorCode.BadRequest,
                    $"{what}: segment {i + 1} does not form a continuous boundary. " +
                    "List the polygon's points in order around its perimeter.", ex.Message);
            }
        }

        return loop;
    }

    /// <summary>
    /// Confirms a loop is planar and horizontal, which floors created from a level require.
    /// </summary>
    public static void RequireHorizontal(IList<XYZ> points, string what)
    {
        if (points.Count == 0) return;

        var z = points[0].Z;
        var offenders = points.Where(p => Math.Abs(p.Z - z) > 1e-6).ToList();

        if (offenders.Count > 0)
        {
            throw new BridgeException(BridgeErrorCode.BadRequest,
                $"{what}: all boundary points must share one elevation. Found z values from " +
                $"{points.Min(p => p.Z):0.####} to {points.Max(p => p.Z):0.####} ft. " +
                "Use the level and an offset to set height, not per-point z.");
        }
    }
}
