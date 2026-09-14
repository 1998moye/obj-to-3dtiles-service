using System.Runtime.InteropServices;
using MeshDecimatorCore.Math;
using Obj2Tiles.Stages.Model;

namespace Obj2Tiles.Stages;

/// <summary>
/// Error-driven local mesh simplification backed by meshoptimizer.  Every material is simplified
/// independently and topological borders are locked, preserving both spatial-tile seams and
/// material/UV boundaries.
/// </summary>
internal static class MeshOptimizerSimplifier
{
    private const uint SimplifyLockBorder = 1u << 0;
    private const uint SimplifySparse = 1u << 1;
    private const uint SimplifyErrorAbsolute = 1u << 2;

    internal sealed record Result(
        int InputTriangles,
        int OutputTriangles,
        double TargetError,
        double ResultError,
        int SafetyRetries,
        int SafetyFallbacks);

    public static unsafe Result SimplifyFile(string path, double targetError, double targetRatio)
    {
        if (targetError <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetError));
        if (targetRatio is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(targetRatio));

        var mesh = new ObjMesh();
        mesh.ReadFile(path);

        var vertices = mesh.Vertices ?? throw new InvalidDataException($"Mesh '{path}' has no vertices.");
        var sourceSubMeshes = mesh.SubMeshIndices ?? throw new InvalidDataException($"Mesh '{path}' has no faces.");
        var inputTriangles = sourceSubMeshes.Sum(indices => indices.Length / 3);

        // Subtracting the local center before conversion to float avoids precision loss for OBJ
        // coordinates expressed in projected/map coordinate systems.
        var center = mesh.Bounds.Center;
        var positions = new float[vertices.Length * 3];
        for (var i = 0; i < vertices.Length; i++)
        {
            positions[i * 3] = (float)(vertices[i].x - center.X);
            positions[i * 3 + 1] = (float)(vertices[i].y - center.Y);
            positions[i * 3 + 2] = (float)(vertices[i].z - center.Z);
        }

        var attributes = BuildAttributes(mesh, vertices.Length, out var attributeWeights);
        var outputSubMeshes = new int[sourceSubMeshes.Length][];
        var maximumResultError = 0f;
        var safetyRetries = 0;
        var safetyFallbacks = 0;

        fixed (float* positionsPtr = positions)
        fixed (float* attributesPtr = attributes)
        fixed (float* weightsPtr = attributeWeights)
        {
            for (var subMeshIndex = 0; subMeshIndex < sourceSubMeshes.Length; subMeshIndex++)
            {
                var source = sourceSubMeshes[subMeshIndex];
                if (source.Length <= 3)
                {
                    outputSubMeshes[subMeshIndex] = source.ToArray();
                    continue;
                }

                var sourceIndices = Array.ConvertAll(source, value => checked((uint)value));
                var targetIndexCount = Math.Max(3, (int)Math.Floor(sourceIndices.Length * targetRatio / 3.0) * 3);
                var candidate = SimplifySubMesh(
                    sourceIndices, targetIndexCount, (float)targetError, positionsPtr, vertices.Length,
                    attributesPtr, attributes.Length, weightsPtr, attributeWeights.Length, path);

                var sourceEnvelope = MeasureTriangleEnvelope(vertices, sourceIndices);
                if (!IsTriangleEnvelopeSafe(sourceEnvelope, MeasureTriangleEnvelope(vertices, candidate.Indices)))
                {
                    safetyRetries++;
                    // [2026-09-07 修复 HLOD 跨洞封面] 原代码：简化结果不经几何校验直接写盘。
                    // 原因：绝对误差只约束表面偏差，不约束新边跨度；开放摄影测量网格会被连接成几十米的大三角形。
                    // 先提高保留比例并收紧误差重试；仍越过输入三角形包络时仅回退当前材质子网格，保证孔洞不被封死。
                    var retryIndexCount = Math.Max(targetIndexCount,
                        (int)Math.Floor(sourceIndices.Length * Math.Max(targetRatio, 0.5) / 3.0) * 3);
                    candidate = SimplifySubMesh(
                        sourceIndices, retryIndexCount, (float)(targetError * 0.25), positionsPtr, vertices.Length,
                        attributesPtr, attributes.Length, weightsPtr, attributeWeights.Length, path);
                    if (!IsTriangleEnvelopeSafe(sourceEnvelope, MeasureTriangleEnvelope(vertices, candidate.Indices)))
                    {
                        safetyFallbacks++;
                        candidate = new SimplificationCandidate(sourceIndices, 0);
                    }
                }

                var output = new int[candidate.Indices.Length];
                for (var i = 0; i < output.Length; i++) output[i] = checked((int)candidate.Indices[i]);
                outputSubMeshes[subMeshIndex] = output;
                maximumResultError = Math.Max(maximumResultError, candidate.ResultError);
            }
        }

        mesh.SubMeshIndices = outputSubMeshes;
        mesh.CompactUsedVertices();
        mesh.WriteFile(path);

        return new Result(
            inputTriangles,
            outputSubMeshes.Sum(indices => indices.Length / 3),
            targetError,
            maximumResultError,
            safetyRetries,
            safetyFallbacks);
    }

    private static unsafe SimplificationCandidate SimplifySubMesh(
        uint[] sourceIndices,
        int targetIndexCount,
        float targetError,
        float* positionsPtr,
        int vertexCount,
        float* attributesPtr,
        int attributesLength,
        float* weightsPtr,
        int attributeCount,
        string path)
    {
        var destination = new uint[sourceIndices.Length];
        float resultError = 0;
        nuint outputIndexCount;
        fixed (uint* sourcePtr = sourceIndices)
        fixed (uint* destinationPtr = destination)
        {
            if (attributesLength > 0)
            {
                outputIndexCount = Native.SimplifyWithAttributes(
                    destinationPtr,
                    sourcePtr,
                    (nuint)sourceIndices.Length,
                    positionsPtr,
                    (nuint)vertexCount,
                    3 * sizeof(float),
                    attributesPtr,
                    (nuint)(attributeCount * sizeof(float)),
                    weightsPtr,
                    (nuint)attributeCount,
                    null,
                    (nuint)targetIndexCount,
                    targetError,
                    SimplifyLockBorder | SimplifySparse | SimplifyErrorAbsolute,
                    &resultError);
            }
            else
            {
                outputIndexCount = Native.Simplify(
                    destinationPtr,
                    sourcePtr,
                    (nuint)sourceIndices.Length,
                    positionsPtr,
                    (nuint)vertexCount,
                    3 * sizeof(float),
                    (nuint)targetIndexCount,
                    targetError,
                    SimplifyLockBorder | SimplifySparse | SimplifyErrorAbsolute,
                    &resultError);
            }
        }

        if (outputIndexCount < 3 || outputIndexCount > (nuint)destination.Length)
            throw new InvalidDataException($"meshoptimizer returned an invalid index count for '{path}'.");
        Array.Resize(ref destination, checked((int)outputIndexCount));
        return new SimplificationCandidate(destination, resultError);
    }

    private static TriangleEnvelope MeasureTriangleEnvelope(Vector3d[] vertices, IReadOnlyList<uint> indices)
    {
        var maximumEdgeSquared = 0d;
        var maximumDoubleAreaSquared = 0d;
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            var a = vertices[indices[i]];
            var b = vertices[indices[i + 1]];
            var c = vertices[indices[i + 2]];
            maximumEdgeSquared = Math.Max(maximumEdgeSquared, Math.Max(
                SquaredDistance(a, b), Math.Max(SquaredDistance(b, c), SquaredDistance(c, a))));

            var abX = b.x - a.x;
            var abY = b.y - a.y;
            var abZ = b.z - a.z;
            var acX = c.x - a.x;
            var acY = c.y - a.y;
            var acZ = c.z - a.z;
            var crossX = abY * acZ - abZ * acY;
            var crossY = abZ * acX - abX * acZ;
            var crossZ = abX * acY - abY * acX;
            maximumDoubleAreaSquared = Math.Max(maximumDoubleAreaSquared,
                crossX * crossX + crossY * crossY + crossZ * crossZ);
        }
        return new TriangleEnvelope(maximumEdgeSquared, maximumDoubleAreaSquared);
    }

    private static double SquaredDistance(Vector3d a, Vector3d b)
    {
        var x = b.x - a.x;
        var y = b.y - a.y;
        var z = b.z - a.z;
        return x * x + y * y + z * z;
    }

    private static bool IsTriangleEnvelopeSafe(TriangleEnvelope source, TriangleEnvelope candidate)
    {
        // 面积比使用“二倍面积的平方”，所以 1.5 倍面积对应 2.25 倍平方值。
        const double maximumEdgeGrowthSquared = 1.25 * 1.25;
        const double maximumAreaGrowthSquared = 1.5 * 1.5;
        return candidate.MaximumEdgeSquared <= source.MaximumEdgeSquared * maximumEdgeGrowthSquared + 1e-9 &&
               candidate.MaximumDoubleAreaSquared <= source.MaximumDoubleAreaSquared * maximumAreaGrowthSquared + 1e-9;
    }

    private sealed record SimplificationCandidate(uint[] Indices, float ResultError);
    private sealed record TriangleEnvelope(double MaximumEdgeSquared, double MaximumDoubleAreaSquared);

    private static float[] BuildAttributes(ObjMesh mesh, int vertexCount, out float[] weights)
    {
        var normals = mesh.Normals;
        var uv2 = mesh.TexCoords2D;
        var uv3 = mesh.TexCoords3D;

        var attributeCount = (normals != null ? 3 : 0) + (uv2 != null ? 2 : uv3 != null ? 3 : 0);
        if (attributeCount == 0)
        {
            weights = [];
            return [];
        }

        weights = new float[attributeCount];
        var attributes = new float[vertexCount * attributeCount];
        for (var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            var offset = vertexIndex * attributeCount;
            var attributeIndex = 0;
            if (normals != null)
            {
                var normal = normals[vertexIndex];
                attributes[offset + attributeIndex] = normal.x;
                attributes[offset + attributeIndex + 1] = normal.y;
                attributes[offset + attributeIndex + 2] = normal.z;
                weights[attributeIndex++] = 0.5f;
                weights[attributeIndex++] = 0.5f;
                weights[attributeIndex++] = 0.5f;
            }

            if (uv2 != null)
            {
                attributes[offset + attributeIndex] = uv2[vertexIndex].x;
                attributes[offset + attributeIndex + 1] = uv2[vertexIndex].y;
                weights[attributeIndex++] = 0.1f;
                weights[attributeIndex] = 0.1f;
            }
            else if (uv3 != null)
            {
                attributes[offset + attributeIndex] = uv3[vertexIndex].x;
                attributes[offset + attributeIndex + 1] = uv3[vertexIndex].y;
                attributes[offset + attributeIndex + 2] = uv3[vertexIndex].z;
                weights[attributeIndex++] = 0.1f;
                weights[attributeIndex++] = 0.1f;
                weights[attributeIndex] = 0.1f;
            }
        }

        return attributes;
    }

    private static class Native
    {
        [DllImport("meshoptimizer", EntryPoint = "meshopt_simplify", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe nuint Simplify(
            uint* destination,
            uint* indices,
            nuint indexCount,
            float* vertexPositions,
            nuint vertexCount,
            nuint vertexPositionsStride,
            nuint targetIndexCount,
            float targetError,
            uint options,
            float* resultError);

        [DllImport("meshoptimizer", EntryPoint = "meshopt_simplifyWithAttributes", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe nuint SimplifyWithAttributes(
            uint* destination,
            uint* indices,
            nuint indexCount,
            float* vertexPositions,
            nuint vertexCount,
            nuint vertexPositionsStride,
            float* vertexAttributes,
            nuint vertexAttributesStride,
            float* attributeWeights,
            nuint attributeCount,
            byte* vertexLock,
            nuint targetIndexCount,
            float targetError,
            uint options,
            float* resultError);
    }
}
