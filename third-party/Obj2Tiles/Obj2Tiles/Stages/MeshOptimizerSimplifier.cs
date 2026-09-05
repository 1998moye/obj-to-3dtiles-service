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
        double ResultError);

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
                var destination = new uint[sourceIndices.Length];
                var targetIndexCount = Math.Max(3, (int)Math.Floor(sourceIndices.Length * targetRatio / 3.0) * 3);
                float resultError = 0;
                nuint outputIndexCount;

                fixed (uint* sourcePtr = sourceIndices)
                fixed (uint* destinationPtr = destination)
                {
                    if (attributes.Length > 0)
                    {
                        outputIndexCount = Native.SimplifyWithAttributes(
                            destinationPtr,
                            sourcePtr,
                            (nuint)sourceIndices.Length,
                            positionsPtr,
                            (nuint)vertices.Length,
                            3 * sizeof(float),
                            attributesPtr,
                            (nuint)(attributeWeights.Length * sizeof(float)),
                            weightsPtr,
                            (nuint)attributeWeights.Length,
                            null,
                            (nuint)targetIndexCount,
                            (float)targetError,
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
                            (nuint)vertices.Length,
                            3 * sizeof(float),
                            (nuint)targetIndexCount,
                            (float)targetError,
                            SimplifyLockBorder | SimplifySparse | SimplifyErrorAbsolute,
                            &resultError);
                    }
                }

                if (outputIndexCount < 3 || outputIndexCount > (nuint)destination.Length)
                    throw new InvalidDataException($"meshoptimizer returned an invalid index count for '{path}'.");

                var output = new int[(int)outputIndexCount];
                for (var i = 0; i < output.Length; i++) output[i] = checked((int)destination[i]);
                outputSubMeshes[subMeshIndex] = output;
                maximumResultError = Math.Max(maximumResultError, resultError);
            }
        }

        mesh.SubMeshIndices = outputSubMeshes;
        mesh.CompactUsedVertices();
        mesh.WriteFile(path);

        return new Result(
            inputTriangles,
            outputSubMeshes.Sum(indices => indices.Length / 3),
            targetError,
            maximumResultError);
    }

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
