using Rhino.Geometry;
using Rhino3dmToIfc.Console.Models;
using Xbim.Ifc4.GeometricModelResource;
using Xbim.Ifc4.GeometryResource;
using Xbim.Ifc4.Kernel;
using Xbim.Ifc4.MeasureResource;
using Xbim.Ifc4.PresentationOrganizationResource;
using Xbim.Ifc4.RepresentationResource;

namespace Rhino3dmToIfc.Console.Services;

public sealed class IfcGeometryBuilder
{
    private readonly LogService _log;

    public IfcGeometryBuilder(LogService log)
    {
        _log = log;
    }

    public bool TryAttachMeshRepresentation(
        IfcProduct product,
        RhinoBimObject source,
        IfcGeometricRepresentationContext context,
        IDictionary<string, IfcPresentationLayerAssignment> layers,
        out string skipReason)
    {
        skipReason = string.Empty;

        if (source.Geometry is Curve curve)
        {
            return TryAttachCurveRepresentation(product, source, curve, context, layers, out skipReason);
        }

        if (source.Geometry is Brep brep)
        {
            return TryAttachBrepPreferredRepresentation(product, source, brep, context, layers, out skipReason);
        }

        if (source.Geometry is Surface surface)
        {
            var surfaceBrep = surface.ToBrep();
            if (surfaceBrep is not null)
            {
                return TryAttachBrepPreferredRepresentation(product, source, surfaceBrep, context, layers, out skipReason);
            }
        }

        var mesh = ExtractMesh(source.Geometry);
        if (mesh is null)
        {
            skipReason = source.Geometry switch
            {
                Extrusion => "Extrusion has no saved render/preview/analysis mesh and could not be converted to Brep topology.",
                Surface => "Surface could not be converted to Brep or mesh geometry.",
                _ => string.IsNullOrWhiteSpace(source.SkipReason) ? "Geometry could not be converted to an IFC representation." : source.SkipReason
            };
            _log.Warning($"Skipping {source.RhinoObjectId}: {skipReason}");
            return false;
        }

        return TryAttachMesh(product, source, mesh, context, layers, out skipReason);
    }

    private bool TryAttachBrepPreferredRepresentation(
        IfcProduct product,
        RhinoBimObject source,
        Brep brep,
        IfcGeometricRepresentationContext context,
        IDictionary<string, IfcPresentationLayerAssignment> layers,
        out string skipReason)
    {
        if (TryAttachBrepPolygonalRepresentation(product, source, brep, context, layers, false, out skipReason))
        {
            return true;
        }

        var polygonalSkipReason = skipReason;
        var fallbackMesh = ExtractBrepMesh(brep);
        if (fallbackMesh is not null && TryAttachMesh(product, source, fallbackMesh, context, layers, out skipReason))
        {
            return true;
        }

        skipReason = string.IsNullOrWhiteSpace(skipReason) ? polygonalSkipReason : $"{polygonalSkipReason} Mesh fallback also failed: {skipReason}";
        _log.Warning($"Skipping {source.RhinoObjectId}: {skipReason}");
        return false;
    }

    private bool TryAttachMesh(
        IfcProduct product,
        RhinoBimObject source,
        Mesh mesh,
        IfcGeometricRepresentationContext context,
        IDictionary<string, IfcPresentationLayerAssignment> layers,
        out string skipReason)
    {
        skipReason = string.Empty;

        if (mesh.Vertices.Count == 0 || mesh.Faces.Count == 0)
        {
            skipReason = "Mesh has no vertices or faces.";
            _log.Warning($"Skipping {source.RhinoObjectId}: {skipReason}");
            return false;
        }

        var exportMesh = PrepareMeshForExport(mesh);
        var model = product.Model;
        var pointList = model.Instances.New<IfcCartesianPointList3D>();
        foreach (var vertex in exportMesh.Vertices)
        {
            var coords = pointList.CoordList.GetAt(pointList.CoordList.Count);
            coords.Add(new IfcLengthMeasure(vertex.X));
            coords.Add(new IfcLengthMeasure(vertex.Y));
            coords.Add(new IfcLengthMeasure(vertex.Z));
        }

        var faceSet = model.Instances.New<IfcTriangulatedFaceSet>();
        faceSet.Coordinates = pointList;
        faceSet.Closed = exportMesh.IsClosed;

        var triangleCount = 0;
        foreach (var face in exportMesh.Faces)
        {
            if (face.IsTriangle)
            {
                AddTriangle(faceSet, face.A, face.B, face.C);
                triangleCount++;
            }
            else if (face.IsQuad)
            {
                AddTriangle(faceSet, face.A, face.B, face.C);
                AddTriangle(faceSet, face.A, face.C, face.D);
                triangleCount += 2;
            }
        }

        if (triangleCount == 0)
        {
            skipReason = "Mesh has no triangle or quad faces.";
            _log.Warning($"Skipping {source.RhinoObjectId}: {skipReason}");
            return false;
        }

        var shapeRepresentation = model.Instances.New<IfcShapeRepresentation>();
        shapeRepresentation.ContextOfItems = context;
        shapeRepresentation.RepresentationIdentifier = "Body";
        shapeRepresentation.RepresentationType = "Tessellation";
        shapeRepresentation.Items.Add(faceSet);
        AssignToLayers(faceSet, source, layers);

        var productShape = model.Instances.New<IfcProductDefinitionShape>();
        productShape.Representations.Add(shapeRepresentation);
        product.Representation = productShape;
        return true;
    }

    private bool TryAttachBrepPolygonalRepresentation(
        IfcProduct product,
        RhinoBimObject source,
        Brep brep,
        IfcGeometricRepresentationContext context,
        IDictionary<string, IfcPresentationLayerAssignment> layers,
        bool logFailure,
        out string skipReason)
    {
        skipReason = string.Empty;
        var model = product.Model;
        var pointList = model.Instances.New<IfcCartesianPointList3D>();
        var pointIndex = new Dictionary<PointKey, int>();
        var faceSet = model.Instances.New<IfcPolygonalFaceSet>();
        faceSet.Coordinates = pointList;
        faceSet.Closed = brep.IsSolid;

        foreach (var face in brep.Faces)
        {
            var loops = GetFaceLoops(face);
            if (loops.Count == 0 || loops[0].Count < 3)
            {
                continue;
            }

            var projectedLoops = ProjectLoops(loops);
            if (projectedLoops.Count == 0 || projectedLoops[0].Count < 3)
            {
                continue;
            }

            if (projectedLoops.Count == 1)
            {
                var polygonalFace = model.Instances.New<IfcIndexedPolygonalFace>();
                AddPolygonLoop(pointList, pointIndex, polygonalFace.CoordIndex, projectedLoops[0]);
                faceSet.Faces.Add(polygonalFace);
            }
            else
            {
                var polygonalFace = model.Instances.New<IfcIndexedPolygonalFaceWithVoids>();
                AddPolygonLoop(pointList, pointIndex, polygonalFace.CoordIndex, projectedLoops[0]);

                for (var i = 1; i < projectedLoops.Count; i++)
                {
                    var inner = polygonalFace.InnerCoordIndices.GetAt(polygonalFace.InnerCoordIndices.Count);
                    AddPolygonLoop(pointList, pointIndex, inner, projectedLoops[i]);
                }

                faceSet.Faces.Add(polygonalFace);
            }
        }

        if (faceSet.Faces.Count == 0)
        {
            skipReason = "Brep has no exportable polygonal faces.";
            if (logFailure)
            {
                _log.Warning($"Skipping {source.RhinoObjectId}: {skipReason}");
            }

            return false;
        }

        var shapeRepresentation = model.Instances.New<IfcShapeRepresentation>();
        shapeRepresentation.ContextOfItems = context;
        shapeRepresentation.RepresentationIdentifier = "Body";
        shapeRepresentation.RepresentationType = "Tessellation";
        shapeRepresentation.Items.Add(faceSet);
        AssignToLayers(faceSet, source, layers);

        var productShape = model.Instances.New<IfcProductDefinitionShape>();
        productShape.Representations.Add(shapeRepresentation);
        product.Representation = productShape;
        return true;
    }

    private static void AddPolygonLoop(
        IfcCartesianPointList3D pointList,
        Dictionary<PointKey, int> pointIndex,
        Xbim.Common.IItemSet<IfcPositiveInteger> target,
        List<ProjectedPoint> loop)
    {
        foreach (var projectedPoint in loop)
        {
            target.Add(new IfcPositiveInteger(GetOrAddPointIndex(pointList, pointIndex, projectedPoint.Point)));
        }
    }

    private bool TryAttachCurveRepresentation(
        IfcProduct product,
        RhinoBimObject source,
        Curve curve,
        IfcGeometricRepresentationContext context,
        IDictionary<string, IfcPresentationLayerAssignment> layers,
        out string skipReason)
    {
        skipReason = string.Empty;
        var points = GetCurvePolylinePoints(curve);
        if (points.Count < 2)
        {
            skipReason = "Curve is invalid or has fewer than two exportable points.";
            _log.Warning($"Skipping {source.RhinoObjectId}: {skipReason}");
            return false;
        }

        var model = product.Model;
        var polyline = model.Instances.New<IfcPolyline>();
        foreach (var point in points)
        {
            var ifcPoint = model.Instances.New<IfcCartesianPoint>();
            ifcPoint.SetXYZ(point.X, point.Y, point.Z);
            polyline.Points.Add(ifcPoint);
        }

        AssignToLayers(polyline, source, layers);

        var shapeRepresentation = model.Instances.New<IfcShapeRepresentation>();
        shapeRepresentation.ContextOfItems = context;
        shapeRepresentation.RepresentationIdentifier = "Axis";
        shapeRepresentation.RepresentationType = "Curve3D";
        shapeRepresentation.Items.Add(polyline);

        var productShape = model.Instances.New<IfcProductDefinitionShape>();
        productShape.Representations.Add(shapeRepresentation);
        product.Representation = productShape;
        return true;
    }

    private static void AssignToLayers(IfcRepresentationItem item, RhinoBimObject source, IDictionary<string, IfcPresentationLayerAssignment> layers)
    {
        foreach (var layerName in GetObjectLayerNames(source))
        {
            AssignToLayer(item, layerName, layers);
        }
    }

    private static IEnumerable<string> GetObjectLayerNames(RhinoBimObject source)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primaryLayerName = string.IsNullOrWhiteSpace(source.LayerName) ? "Default" : source.LayerName.Trim();
        if (seen.Add(primaryLayerName))
        {
            yield return primaryLayerName;
        }

        foreach (var layerName in source.AdditionalLayerNames)
        {
            if (!string.IsNullOrWhiteSpace(layerName) && seen.Add(layerName.Trim()))
            {
                yield return layerName.Trim();
            }
        }
    }

    private static void AssignToLayer(IfcRepresentationItem item, string layerName, IDictionary<string, IfcPresentationLayerAssignment> layers)
    {
        var cleanLayerName = string.IsNullOrWhiteSpace(layerName) ? "Default" : layerName.Trim();
        foreach (var layerPath in GetLayerPathNames(cleanLayerName))
        {
            if (!layers.TryGetValue(layerPath, out var layer))
            {
                layer = item.Model.Instances.New<IfcPresentationLayerAssignment>();
                layer.Name = layerPath;
                layer.Identifier = layerPath;
                layer.Description = $"Rhino layer: {layerPath}";
                layers[layerPath] = layer;
            }

            layer.AssignedItems.Add(item);
        }
    }

    private static IEnumerable<string> GetLayerPathNames(string layerName)
    {
        var parts = layerName.Split("::", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            yield return "Default";
            yield break;
        }

        var current = string.Empty;
        foreach (var part in parts)
        {
            current = string.IsNullOrWhiteSpace(current) ? part : $"{current}::{part}";
            yield return current;
        }
    }

    private static void AddTriangle(IfcTriangulatedFaceSet faceSet, int a, int b, int c)
    {
        var indices = faceSet.CoordIndex.GetAt(faceSet.CoordIndex.Count);
        indices.Add(new IfcPositiveInteger(a + 1));
        indices.Add(new IfcPositiveInteger(b + 1));
        indices.Add(new IfcPositiveInteger(c + 1));
    }

    private static Mesh PrepareMeshForExport(Mesh mesh)
    {
        var exportMesh = mesh.DuplicateMesh();
        exportMesh.Faces.ConvertQuadsToTriangles();
        exportMesh.Vertices.CombineIdentical(true, true);
        exportMesh.Vertices.CullUnused();
        exportMesh.Compact();
        return exportMesh;
    }

    private static Mesh? ExtractMesh(object? geometry)
    {
        return geometry switch
        {
            Mesh mesh => DuplicateMesh(mesh),
            Extrusion extrusion => DuplicateMesh(GetFirstMesh(
                () => extrusion.GetMesh(MeshType.Render),
                () => extrusion.GetMesh(MeshType.Preview),
                () => extrusion.GetMesh(MeshType.Analysis),
                () => extrusion.GetMesh(MeshType.Any),
                () => extrusion.GetMesh(MeshType.Default))) ?? ExtractBrepMesh(extrusion.ToBrep()),
            _ => null
        };
    }

    private static Mesh? ExtractBrepMesh(Brep? brep)
    {
        if (brep is null)
        {
            return null;
        }

        var meshes = new List<Mesh>();
        foreach (var face in brep.Faces)
        {
            var mesh = GetFirstMesh(
                () => face.GetMesh(MeshType.Render),
                () => face.GetMesh(MeshType.Preview),
                () => face.GetMesh(MeshType.Analysis),
                () => face.GetMesh(MeshType.Any),
                () => face.GetMesh(MeshType.Default));

            var duplicate = DuplicateMesh(mesh);
            if (duplicate is not null && duplicate.Vertices.Count > 0 && duplicate.Faces.Count > 0)
            {
                meshes.Add(duplicate);
            }
        }

        if (meshes.Count == 0)
        {
            return null;
        }

        var combined = new Mesh();
        combined.Append(meshes);
        combined.Faces.ConvertQuadsToTriangles();
        combined.Vertices.CombineIdentical(true, true);
        combined.Vertices.CullUnused();
        combined.Compact();
        return combined;
    }

    private static Mesh? BuildMeshFromBrepTopology(Brep brep)
    {
        var mesh = new Mesh();

        foreach (var face in brep.Faces)
        {
            var loops = GetFaceLoops(face);
            if (loops.Count == 0 || loops[0].Count < 3)
            {
                continue;
            }

            var projected = ProjectLoops(loops);
            var polygon = MergeHoles(projected);
            var triangles = EarClip(polygon);
            if (triangles.Count == 0)
            {
                continue;
            }

            var startIndex = mesh.Vertices.Count;
            foreach (var point in polygon)
            {
                mesh.Vertices.Add(point.Point);
            }

            foreach (var triangle in triangles)
            {
                mesh.Faces.AddFace(startIndex + triangle.A, startIndex + triangle.B, startIndex + triangle.C);
            }
        }

        if (mesh.Vertices.Count == 0 || mesh.Faces.Count == 0)
        {
            return null;
        }

        mesh.Compact();
        return mesh;
    }

    private static List<List<Point3d>> GetFaceLoops(BrepFace face)
    {
        var outerLoops = new List<List<Point3d>>();
        var innerLoops = new List<List<Point3d>>();

        foreach (var loop in face.Loops)
        {
            var points = GetLoopPoints(loop);
            if (points.Count < 3)
            {
                continue;
            }

            if (loop.LoopType == BrepLoopType.Inner)
            {
                innerLoops.Add(points);
            }
            else if (loop.LoopType == BrepLoopType.Outer)
            {
                outerLoops.Add(points);
            }
        }

        if (outerLoops.Count == 0 && face.OuterLoop is not null)
        {
            var fallback = GetLoopPoints(face.OuterLoop);
            if (fallback.Count >= 3)
            {
                outerLoops.Add(fallback);
            }
        }

        var result = new List<List<Point3d>>();
        result.AddRange(outerLoops);
        result.AddRange(innerLoops);
        return result;
    }

    private static List<Point3d> GetLoopPoints(BrepLoop loop)
    {
        var points = new List<Point3d>();

        foreach (var trim in loop.Trims)
        {
            var sampled = SampleTrimCurve(trim);
            if (sampled.Count >= 3)
            {
                foreach (var point in sampled)
                {
                    AddDistinctPoint(points, point);
                }

                continue;
            }

            var start = trim.StartVertex.Location;
            var end = trim.EndVertex.Location;

            if (points.Count == 0)
            {
                AddDistinctPoint(points, start);
                AddDistinctPoint(points, end);
                continue;
            }

            if (IsNear(points[^1], start))
            {
                AddDistinctPoint(points, end);
            }
            else if (IsNear(points[^1], end))
            {
                AddDistinctPoint(points, start);
            }
            else
            {
                AddDistinctPoint(points, start);
                AddDistinctPoint(points, end);
            }
        }

        RemoveClosingDuplicate(points);
        RemoveCollinear(points);
        return points;
    }

    private static List<Point3d> SampleTrimCurve(BrepTrim trim)
    {
        var curve = trim.Edge?.EdgeCurve;
        if (curve is null || !curve.IsClosed)
        {
            return [];
        }

        if (curve.TryGetPolyline(out var polyline) && polyline.Count >= 4)
        {
            var points = polyline.ToList();
            RemoveClosingDuplicate(points);
            RemoveCollinear(points);
            return points;
        }

        const int sampleCount = 24;
        var domain = curve.Domain;
        var sampled = new List<Point3d>();
        for (var i = 0; i < sampleCount; i++)
        {
            var t = domain.T0 + ((domain.T1 - domain.T0) * i / sampleCount);
            AddDistinctPoint(sampled, curve.PointAt(t));
        }

        RemoveClosingDuplicate(sampled);
        RemoveCollinear(sampled);
        return sampled.Count >= 3 ? sampled : [];
    }

    private static List<Point3d> GetCurvePolylinePoints(Curve curve)
    {
        if (!curve.IsValid)
        {
            return [];
        }

        if (curve.TryGetPolyline(out var polyline) && polyline.Count >= 2)
        {
            return CleanPolylinePoints(polyline);
        }

        if (curve is LineCurve lineCurve)
        {
            return CleanPolylinePoints([lineCurve.Line.From, lineCurve.Line.To]);
        }

        var domain = curve.Domain;
        if (domain.T1 <= domain.T0)
        {
            return [];
        }

        const int segmentCount = 48;
        var points = new List<Point3d>();
        for (var i = 0; i <= segmentCount; i++)
        {
            var t = domain.T0 + ((domain.T1 - domain.T0) * i / segmentCount);
            points.Add(curve.PointAt(t));
        }

        return CleanPolylinePoints(points);
    }

    private static List<Point3d> CleanPolylinePoints(IEnumerable<Point3d> source)
    {
        var points = new List<Point3d>();
        foreach (var point in source)
        {
            AddDistinctPoint(points, point);
        }

        return points;
    }

    private static int GetOrAddPointIndex(
        IfcCartesianPointList3D pointList,
        Dictionary<PointKey, int> pointIndex,
        Point3d point)
    {
        var key = PointKey.From(point);
        if (pointIndex.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var coords = pointList.CoordList.GetAt(pointList.CoordList.Count);
        coords.Add(new IfcLengthMeasure(point.X));
        coords.Add(new IfcLengthMeasure(point.Y));
        coords.Add(new IfcLengthMeasure(point.Z));

        var index = pointList.CoordList.Count;
        pointIndex[key] = index;
        return index;
    }

    private static List<List<ProjectedPoint>> ProjectLoops(List<List<Point3d>> loops)
    {
        var normal = GetNormal(loops[0]);
        var dropAxis = GetDropAxis(normal);
        var projected = loops
            .Select(loop => loop.Select(point => Project(point, dropAxis)).ToList())
            .Where(loop => loop.Count >= 3)
            .ToList();

        if (projected.Count == 0)
        {
            return projected;
        }

        if (SignedArea(projected[0]) < 0)
        {
            projected[0].Reverse();
        }

        for (var i = 1; i < projected.Count; i++)
        {
            if (SignedArea(projected[i]) > 0)
            {
                projected[i].Reverse();
            }
        }

        return projected;
    }

    private static Vector3d GetNormal(List<Point3d> points)
    {
        for (var i = 0; i < points.Count - 2; i++)
        {
            var a = points[i + 1] - points[i];
            var b = points[i + 2] - points[i];
            var normal = Vector3d.CrossProduct(a, b);
            if (normal.SquareLength > 1e-12)
            {
                normal.Unitize();
                return normal;
            }
        }

        return new Vector3d(0, 0, 1);
    }

    private static int GetDropAxis(Vector3d normal)
    {
        var ax = Math.Abs(normal.X);
        var ay = Math.Abs(normal.Y);
        var az = Math.Abs(normal.Z);

        if (ax >= ay && ax >= az)
        {
            return 0;
        }

        return ay >= az ? 1 : 2;
    }

    private static ProjectedPoint Project(Point3d point, int dropAxis)
    {
        return dropAxis switch
        {
            0 => new ProjectedPoint(point, point.Y, point.Z),
            1 => new ProjectedPoint(point, point.X, point.Z),
            _ => new ProjectedPoint(point, point.X, point.Y)
        };
    }

    private static List<ProjectedPoint> MergeHoles(List<List<ProjectedPoint>> loops)
    {
        var polygon = loops[0].ToList();
        for (var i = 1; i < loops.Count; i++)
        {
            polygon = BridgeHole(polygon, loops[i], loops);
        }

        return polygon;
    }

    private static List<ProjectedPoint> BridgeHole(List<ProjectedPoint> outer, List<ProjectedPoint> hole, List<List<ProjectedPoint>> allLoops)
    {
        if (hole.Count < 3)
        {
            return outer;
        }

        var holeIndex = 0;
        for (var i = 1; i < hole.Count; i++)
        {
            if (hole[i].X > hole[holeIndex].X || (Math.Abs(hole[i].X - hole[holeIndex].X) < 1e-9 && hole[i].Y < hole[holeIndex].Y))
            {
                holeIndex = i;
            }
        }

        var bridgeIndex = FindBridgeVertex(outer, hole[holeIndex], allLoops);
        var merged = new List<ProjectedPoint>();
        for (var i = 0; i <= bridgeIndex; i++)
        {
            merged.Add(outer[i]);
        }

        for (var i = 0; i <= hole.Count; i++)
        {
            merged.Add(hole[(holeIndex + i) % hole.Count]);
        }

        merged.Add(outer[bridgeIndex]);
        for (var i = bridgeIndex + 1; i < outer.Count; i++)
        {
            merged.Add(outer[i]);
        }

        return merged;
    }

    private static int FindBridgeVertex(List<ProjectedPoint> outer, ProjectedPoint holePoint, List<List<ProjectedPoint>> allLoops)
    {
        var bestIndex = 0;
        var bestDistance = double.MaxValue;

        for (var i = 0; i < outer.Count; i++)
        {
            if (!IsVisibleBridge(holePoint, outer[i], allLoops))
            {
                continue;
            }

            var distance = DistanceSquared(holePoint, outer[i]);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static bool IsVisibleBridge(ProjectedPoint a, ProjectedPoint b, List<List<ProjectedPoint>> loops)
    {
        foreach (var loop in loops)
        {
            for (var i = 0; i < loop.Count; i++)
            {
                var c = loop[i];
                var d = loop[(i + 1) % loop.Count];
                if (SamePoint(a, c) || SamePoint(a, d) || SamePoint(b, c) || SamePoint(b, d))
                {
                    continue;
                }

                if (SegmentsIntersect(a, b, c, d))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static List<TriangleIndex> EarClip(List<ProjectedPoint> polygon)
    {
        RemoveConsecutiveDuplicates(polygon);
        var result = new List<TriangleIndex>();
        if (polygon.Count < 3)
        {
            return result;
        }

        if (SignedArea(polygon) < 0)
        {
            polygon.Reverse();
        }

        var indices = Enumerable.Range(0, polygon.Count).ToList();
        var guard = polygon.Count * polygon.Count;
        while (indices.Count > 3 && guard-- > 0)
        {
            var clipped = false;
            for (var i = 0; i < indices.Count; i++)
            {
                var previous = indices[(i - 1 + indices.Count) % indices.Count];
                var current = indices[i];
                var next = indices[(i + 1) % indices.Count];

                if (!IsEar(previous, current, next, indices, polygon))
                {
                    continue;
                }

                result.Add(new TriangleIndex(previous, current, next));
                indices.RemoveAt(i);
                clipped = true;
                break;
            }

            if (!clipped)
            {
                return [];
            }
        }

        if (indices.Count == 3 && Math.Abs(Cross(polygon[indices[0]], polygon[indices[1]], polygon[indices[2]])) > 1e-12)
        {
            result.Add(new TriangleIndex(indices[0], indices[1], indices[2]));
        }

        return result;
    }

    private static bool IsEar(int previous, int current, int next, List<int> indices, List<ProjectedPoint> polygon)
    {
        var a = polygon[previous];
        var b = polygon[current];
        var c = polygon[next];
        if (Cross(a, b, c) <= 1e-12)
        {
            return false;
        }

        foreach (var index in indices)
        {
            if (index == previous || index == current || index == next)
            {
                continue;
            }

            if (PointInTriangle(polygon[index], a, b, c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PointInTriangle(ProjectedPoint p, ProjectedPoint a, ProjectedPoint b, ProjectedPoint c)
    {
        var c1 = Cross(a, b, p);
        var c2 = Cross(b, c, p);
        var c3 = Cross(c, a, p);
        return c1 >= -1e-12 && c2 >= -1e-12 && c3 >= -1e-12;
    }

    private static double SignedArea(List<ProjectedPoint> points)
    {
        var area = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            area += (a.X * b.Y) - (b.X * a.Y);
        }

        return area / 2.0;
    }

    private static double Cross(ProjectedPoint a, ProjectedPoint b, ProjectedPoint c)
    {
        return ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
    }

    private static bool SegmentsIntersect(ProjectedPoint a, ProjectedPoint b, ProjectedPoint c, ProjectedPoint d)
    {
        var d1 = Cross(a, b, c);
        var d2 = Cross(a, b, d);
        var d3 = Cross(c, d, a);
        var d4 = Cross(c, d, b);
        return ((d1 > 1e-12 && d2 < -1e-12) || (d1 < -1e-12 && d2 > 1e-12))
            && ((d3 > 1e-12 && d4 < -1e-12) || (d3 < -1e-12 && d4 > 1e-12));
    }

    private static double DistanceSquared(ProjectedPoint a, ProjectedPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    private static bool SamePoint(ProjectedPoint a, ProjectedPoint b)
    {
        return DistanceSquared(a, b) <= 1e-12;
    }

    private static void RemoveConsecutiveDuplicates(List<ProjectedPoint> points)
    {
        for (var i = points.Count - 1; i >= 0; i--)
        {
            var previous = points[(i - 1 + points.Count) % points.Count];
            if (SamePoint(points[i], previous))
            {
                points.RemoveAt(i);
            }
        }
    }

    private static void AddDistinctPoint(List<Point3d> points, Point3d point)
    {
        if (points.Count == 0 || !IsNear(points[^1], point))
        {
            points.Add(point);
        }
    }

    private static void RemoveClosingDuplicate(List<Point3d> points)
    {
        if (points.Count > 1 && IsNear(points[0], points[^1]))
        {
            points.RemoveAt(points.Count - 1);
        }
    }

    private static void RemoveCollinear(List<Point3d> points)
    {
        for (var i = points.Count - 1; i >= 0 && points.Count >= 3; i--)
        {
            var previous = points[(i - 1 + points.Count) % points.Count];
            var current = points[i];
            var next = points[(i + 1) % points.Count];
            var normal = Vector3d.CrossProduct(current - previous, next - current);
            if (normal.SquareLength <= 1e-12)
            {
                points.RemoveAt(i);
            }
        }
    }

    private static bool IsNear(Point3d a, Point3d b)
    {
        return a.DistanceToSquared(b) <= 1e-12;
    }

    private static Mesh? GetFirstMesh(params Func<Mesh?>[] factories)
    {
        foreach (var factory in factories)
        {
            var mesh = factory();
            if (mesh is not null && mesh.Vertices.Count > 0 && mesh.Faces.Count > 0)
            {
                return mesh;
            }
        }

        return null;
    }

    private static Mesh? DuplicateMesh(Mesh? mesh)
    {
        return mesh?.DuplicateMesh();
    }

    private sealed record ProjectedPoint(Point3d Point, double X, double Y);

    private readonly record struct PointKey(long X, long Y, long Z)
    {
        public static PointKey From(Point3d point)
        {
            const double scale = 1_000_000.0;
            return new PointKey(
                (long)Math.Round(point.X * scale),
                (long)Math.Round(point.Y * scale),
                (long)Math.Round(point.Z * scale));
        }
    }

    private readonly record struct TriangleIndex(int A, int B, int C);
}
