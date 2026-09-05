using System.Text.Json;
using Obj2Tiles.Library.Geometry;

namespace Obj2Tiles.Stages;

public sealed record HierarchicalTileMetric(
    string Name,
    int Lod,
    int SpatialDepth,
    int InputTriangles,
    int OutputTriangles,
    double TargetError,
    double ResultError,
    double GeometricError);

public sealed class HierarchicalBuildResult
{
    public required int LodCount { get; init; }
    public required Dictionary<string, Box3>[] Bounds { get; init; }
    public required Dictionary<string, double>[] GeometricErrors { get; init; }
    public required IReadOnlyList<HierarchicalTileMetric> Metrics { get; init; }
    public double RootGeometricError => GeometricErrors
        .SelectMany(level => level.Values)
        .DefaultIfEmpty(1)
        .Max();
}

public static partial class StagesFacade
{
    /// <summary>
    /// Builds a spatial-first HLOD pyramid.  Unlike the legacy Decimation -> Splitting pipeline,
    /// this keeps the source geometry local before simplification.  Each spatial node is written
    /// from its high-resolution source region and then simplified with an absolute error bound.
    /// </summary>
    public static async Task<HierarchicalBuildResult> BuildHierarchical(
        string sourcePath,
        string destination,
        int lodCount,
        SplitPointStrategy splitPointStrategy,
        double errorDivisor,
        double targetRatio,
        float lodTextureScale,
        int maxTextureSize,
        int textureQuality,
        TextureFormat textureFormat)
    {
        if (lodCount < 1) throw new ArgumentOutOfRangeException(nameof(lodCount));
        if (errorDivisor <= 0) throw new ArgumentOutOfRangeException(nameof(errorDivisor));
        if (targetRatio is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(targetRatio));

        Directory.CreateDirectory(destination);
        Console.WriteLine($" -> Loading source mesh once for {lodCount}-level spatial-first HLOD");
        var root = MeshUtils.LoadMesh(sourcePath, out _);
        Console.WriteLine($" ?> Source: {root.VertexCount} vertices, {root.FacesCount} triangles, bounds {root.Bounds}");

        var bounds = Enumerable.Range(0, lodCount).Select(_ => new Dictionary<string, Box3>()).ToArray();
        var errors = Enumerable.Range(0, lodCount).Select(_ => new Dictionary<string, double>()).ToArray();
        var metrics = new List<HierarchicalTileMetric>();
        var currentLevel = new List<IMesh> { root };
        // The content-less global wrapper is an extra hierarchy level. It must not consume one of
        // the user-requested LODs: --lods 5 emits LOD-0 through LOD-4, not only LOD-0 through LOD-3.
        var maximumDepth = lodCount;

        for (var spatialDepth = 0; spatialDepth <= maximumDepth; spatialDepth++)
        {
            var lod = maximumDepth - spatialDepth;
            Console.WriteLine($" -> HLOD spatial depth {spatialDepth}/{maximumDepth}: {currentLevel.Count} nodes (LOD-{lod})");

            // Split before writing.  MeshT.WriteObj may repack its material/texture state; children
            // must therefore be derived from the untouched high-resolution parent mesh.
            var nextLevel = spatialDepth < maximumDepth
                ? SplitLevel(currentLevel, splitPointStrategy)
                : [];

            // Mature city-scale tilesets do not ship one globally simplified mesh as their first
            // payload.  Keep the wrapper root content-less and start with four independently
            // bounded regional tiles.  This avoids both global feature collapse and a large
            // bootstrap download while retaining a complete overview through the children.
            if (spatialDepth == 0 && maximumDepth > 0)
            {
                Console.WriteLine(" ?> Omitting global root mesh; regional HLOD nodes become the top-level contents");
                currentLevel = nextLevel;
                continue;
            }

            var lodDirectory = Path.Combine(destination, $"LOD-{lod}");
            Directory.CreateDirectory(lodDirectory);

            foreach (var mesh in currentLevel.OrderBy(mesh => mesh.Name, StringComparer.Ordinal))
            {
                var originalBounds = mesh.Bounds;
                bounds[lod][mesh.Name] = originalBounds;

                if (mesh is MeshT textured)
                {
                    textured.TexturesStrategy = lod == 0 ? TexturesStrategy.Repack : TexturesStrategy.RepackCompressed;
                    textured.TextureDownscale = lod == 0 ? 1 : (float)Math.Pow(lodTextureScale, lod);
                    textured.MaxTextureSize = maxTextureSize;
                    textured.TextureQuality = textureQuality;
                    textured.TextureFormat = textureFormat;
                }

                var objPath = Path.Combine(lodDirectory, $"{mesh.Name}.obj");
                Console.WriteLine($" -> [HLOD-{spatialDepth}] Writing '{mesh.Name}' ({mesh.FacesCount} triangles)");
                mesh.WriteObj(objPath);

                if (lod == 0)
                {
                    errors[lod][mesh.Name] = 0;
                    metrics.Add(new HierarchicalTileMetric(
                        mesh.Name, lod, spatialDepth, mesh.FacesCount, mesh.FacesCount, 0, 0, 0));
                    continue;
                }

                // A 50 m tile with the default divisor 200 gets a conservative 0.25 m error
                // budget, matching the progression observed in mature photogrammetry tilesets.
                // This builder uses an XY quadtree.  Z is deliberately not subdivided for
                // photogrammetry sites, so including its full range would keep the error budget
                // artificially large at every deeper level.  Drive refinement from the horizontal
                // tile footprint, just like the observed mature city tilesets.
                var maximumSpan = Math.Max(originalBounds.Width, originalBounds.Height);
                var targetError = Math.Max(maximumSpan / errorDivisor, 1e-6);
                var simplified = MeshOptimizerSimplifier.SimplifyFile(objPath, targetError, targetRatio);

                // geometricError is a guaranteed upper bound, not a triangle-percentage guess.
                // result_error is retained in the manifest for diagnostics and regression tests.
                var geometricError = Math.Max(targetError, simplified.ResultError);
                errors[lod][mesh.Name] = geometricError;
                metrics.Add(new HierarchicalTileMetric(
                    mesh.Name,
                    lod,
                    spatialDepth,
                    simplified.InputTriangles,
                    simplified.OutputTriangles,
                    targetError,
                    simplified.ResultError,
                    geometricError));

                Console.WriteLine(
                    $" ?> [HLOD-{spatialDepth}] {mesh.Name}: {simplified.InputTriangles} -> {simplified.OutputTriangles} triangles, " +
                    $"target error {targetError:0.######} m, result error {simplified.ResultError:0.######} m");
            }

            currentLevel = nextLevel;
            await Task.Yield();
        }

        var manifestPath = Path.Combine(destination, "hlod-metadata.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
        {
            version = 1,
            mode = "spatial-first-absolute-error",
            lodCount,
            errorDivisor,
            targetRatio,
            borderLocked = true,
            metrics
        }, new JsonSerializerOptions { WriteIndented = true }));

        return new HierarchicalBuildResult
        {
            LodCount = lodCount,
            Bounds = bounds,
            GeometricErrors = errors,
            Metrics = metrics
        };
    }

    private static List<IMesh> SplitLevel(IReadOnlyList<IMesh> parents, SplitPointStrategy strategy)
    {
        var children = new List<IMesh>(parents.Count * 4);
        var xUtils = new VertexUtilsX();
        var yUtils = new VertexUtilsY();

        foreach (var parent in parents)
        {
            var splitX = GetSplitPoint(parent, strategy);
            parent.Split(xUtils, splitX.X, out var left, out var right);

            foreach (var half in new[] { left, right })
            {
                if (half.FacesCount == 0) continue;
                var splitY = GetSplitPoint(half, strategy);
                half.Split(yUtils, splitY.Y, out var low, out var high);
                if (low.FacesCount > 0) children.Add(low);
                if (high.FacesCount > 0) children.Add(high);
            }
        }

        return children;
    }

    private static Vertex3 GetSplitPoint(IMesh mesh, SplitPointStrategy strategy) => strategy switch
    {
        SplitPointStrategy.AbsoluteCenter => mesh.Bounds.Center,
        SplitPointStrategy.VertexBaricenter => mesh.GetVertexBaricenter(),
        SplitPointStrategy.VertexMedian => mesh.GetVertexMedian(),
        _ => throw new ArgumentOutOfRangeException(nameof(strategy))
    };
}
