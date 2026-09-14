using System.Text.Json;
using Obj2Tiles.Library;
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
    // [2026-09-07 HLOD 有界生命周期] 回归指标：遍历过程中同时驻留内存的网格数峰值、
    // 磁盘暂存的字节峰值（未启用暂存时为 0）。
    public required int PeakResidentMeshes { get; init; }
    public required long SpoolPeakBytes { get; init; }
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
        TextureFormat textureFormat,
        // [2026-09-07 资源约束] 任务级纹理缓存与阶段并发上限由调用方（Program/资源计划）注入。
        TextureCache? textureCache = null,
        int stageConcurrency = 1,
        // [2026-09-07 HLOD 有界生命周期] 极低内存档位启用：拆分出的子网格先暂存到任务专属
        // 磁盘目录，处理时才逐个读回，把驻留网格数从 O(深度×4) 降到 O(1)。
        bool spoolIntermediateMeshes = false,
        string? spoolDirectory = null)
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
        // The content-less global wrapper is an extra hierarchy level. It must not consume one of
        // the user-requested LODs: --lods 5 emits LOD-0 through LOD-4, not only LOD-0 through LOD-3.
        var maximumDepth = lodCount;

        // [2026-09-07 HLOD 有界生命周期] 深度优先遍历替代按层全驻留：旧实现同一层的全部节点
        // 与下一层全部子网格同时驻留内存（O(4^深度) 级增长），是深四叉树的内存尖峰来源。
        // 现在任一时刻只驻留当前路径与其未处理兄弟（O(深度×4)），节点在拆分出子网格并完成
        // 自身写盘后立即释放；启用暂存时子网格落盘，驻留降到 O(1)。
        using var spool = spoolIntermediateMeshes
            ? new MeshSpool(spoolDirectory ?? Path.Combine(destination, ".hlod-spool"))
            : null;
        var residentMeshes = 1; // root
        var peakResidentMeshes = 1;
        Console.WriteLine(spool != null
            ? $" -> HLOD depth-first bounded traversal, spooling intermediates to {spool.DirectoryPath}"
            : " -> HLOD depth-first bounded traversal (in-memory frontier)");

        try
        {
            ProcessNode(root, 0);

            // 清单按 LOD 降序 + 名称排序，与旧按层处理的输出顺序保持一致（回归可比）。
            var orderedMetrics = metrics
                .OrderByDescending(m => m.Lod)
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .ToList();

            var manifestPath = Path.Combine(destination, "hlod-metadata.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
            {
                version = 1,
                mode = "spatial-first-absolute-error",
                lodCount,
                errorDivisor,
                targetRatio,
                borderLocked = true,
                metrics = orderedMetrics
            }, new JsonSerializerOptions { WriteIndented = true }));

            if (residentMeshes != 0)
                Console.WriteLine($" !> HLOD traversal ended with {residentMeshes} mesh(es) still resident (expected 0)");

            return new HierarchicalBuildResult
            {
                LodCount = lodCount,
                Bounds = bounds,
                GeometricErrors = errors,
                Metrics = orderedMetrics,
                PeakResidentMeshes = peakResidentMeshes,
                SpoolPeakBytes = spool?.PeakBytes ?? 0
            };
        }
        finally
        {
            // 成功、失败与取消都必须清掉暂存目录；若此处遗漏，服务端的任务暂存区清理兜底。
            spool?.Dispose();
        }

        void ProcessNode(IMesh mesh, int spatialDepth)
        {
            var lod = maximumDepth - spatialDepth;

            // Split before writing.  MeshT.WriteObj may repack its material/texture state; children
            // must therefore be derived from the untouched high-resolution parent mesh.
            List<IMesh>? children = null;
            List<string>? spooledPaths = null;
            if (spatialDepth < maximumDepth)
            {
                children = SplitLevel([mesh], splitPointStrategy);
                residentMeshes += children.Count;
                peakResidentMeshes = Math.Max(peakResidentMeshes, residentMeshes);

                if (spool != null)
                {
                    spooledPaths = new List<string>(children.Count);
                    foreach (var child in children)
                    {
                        spooledPaths.Add(spool.Save(child));
                        residentMeshes--; // 落盘后立即释放
                    }
                    children = null;
                }
            }

            // Mature city-scale tilesets do not ship one globally simplified mesh as their first
            // payload.  Keep the wrapper root content-less and start with four independently
            // bounded regional tiles.  This avoids both global feature collapse and a large
            // bootstrap download while retaining a complete overview through the children.
            var isOmittedWrapper = spatialDepth == 0 && maximumDepth > 0;
            if (!isOmittedWrapper)
                WriteNode(mesh, lod, spatialDepth);
            residentMeshes--; // 拆分与自身写盘完成，父网格不再被需要

            if (children != null)
            {
                for (var i = 0; i < children.Count; i++)
                {
                    ProcessNode(children[i], spatialDepth + 1);
                    children[i] = null!; // 子树处理完成后立即释放引用
                }
            }
            else if (spooledPaths != null)
            {
                foreach (var path in spooledPaths)
                {
                    var child = spool!.Load(path);
                    residentMeshes++;
                    peakResidentMeshes = Math.Max(peakResidentMeshes, residentMeshes);
                    ProcessNode(child, spatialDepth + 1);
                }
            }
        }

        void WriteNode(IMesh mesh, int lod, int spatialDepth)
        {
            var lodDirectory = Path.Combine(destination, $"LOD-{lod}");
            Directory.CreateDirectory(lodDirectory);

            var originalBounds = mesh.Bounds;
            bounds[lod][mesh.Name] = originalBounds;

            if (mesh is MeshT textured)
            {
                textured.TexturesStrategy = lod == 0 ? TexturesStrategy.Repack : TexturesStrategy.RepackCompressed;
                textured.TextureDownscale = lod == 0 ? 1 : (float)Math.Pow(lodTextureScale, lod);
                textured.MaxTextureSize = maxTextureSize;
                textured.TextureQuality = textureQuality;
                textured.TextureFormat = textureFormat;
                textured.TextureCache = textureCache;
                textured.StageConcurrency = stageConcurrency;
            }

            var objPath = Path.Combine(lodDirectory, $"{mesh.Name}.obj");
            Console.WriteLine($" -> [HLOD-{spatialDepth}] Writing '{mesh.Name}' ({mesh.FacesCount} triangles)");
            mesh.WriteObj(objPath);

            if (lod == 0)
            {
                errors[lod][mesh.Name] = 0;
                metrics.Add(new HierarchicalTileMetric(
                    mesh.Name, lod, spatialDepth, mesh.FacesCount, mesh.FacesCount, 0, 0, 0));
                return;
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
            if (simplified.SafetyRetries > 0)
            {
                Console.WriteLine(
                    $" ?> [HLOD-{spatialDepth}] {mesh.Name}: topology safety retried {simplified.SafetyRetries} " +
                    $"submesh(es), preserved {simplified.SafetyFallbacks} original submesh(es)");
            }

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
