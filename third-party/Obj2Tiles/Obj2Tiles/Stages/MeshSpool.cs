using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Library.Materials;

namespace Obj2Tiles.Stages;

// [2026-09-07 HLOD 磁盘暂存] 极低内存档位下，HLOD 深度优先遍历把暂未处理的子网格序列化到
// 任务专属目录而不是驻留内存，处理时再逐个读回（读回即删文件）。二进制格式只为进程内中转，
// 不做跨版本兼容；纹理只存引用路径，不复制像素数据。PeakBytes 记录暂存磁盘峰值作为回归指标。
public sealed class MeshSpool : IDisposable
{
    private const int Magic = 0x4F423253; // "OB2S"
    private const int Version = 1;

    private readonly string _directory;
    private long _currentBytes;
    private bool _disposed;

    public MeshSpool(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public string DirectoryPath => _directory;
    public long CurrentBytes { get { lock (this) return _currentBytes; } }
    public long PeakBytes { get; private set; }
    public int SaveCount { get; private set; }
    public int LoadCount { get; private set; }

    public string Save(IMesh mesh)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.bin");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream))
        {
            WriteMesh(writer, mesh);
        }
        var length = new FileInfo(path).Length;
        lock (this)
        {
            _currentBytes += length;
            if (_currentBytes > PeakBytes) PeakBytes = _currentBytes;
        }
        SaveCount++;
        return path;
    }

    public IMesh Load(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IMesh mesh;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var reader = new BinaryReader(stream))
        {
            mesh = ReadMesh(reader);
        }
        var length = new FileInfo(path).Length;
        File.Delete(path);
        lock (this) _currentBytes -= length;
        LoadCount++;
        return mesh;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 成功、失败与取消路径都必须清掉暂存目录（其上级任务暂存区的清理是最后兜底）。
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }

    private static void WriteMesh(BinaryWriter writer, IMesh mesh)
    {
        if (mesh is not Mesh && mesh is not MeshT)
            throw new NotSupportedException($"不支持的网格类型: {mesh.GetType().Name}");

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(mesh is MeshT ? (byte)1 : (byte)0);
        writer.Write(mesh.Name);
        writer.Write(mesh.DebugName);

        writer.Write(mesh.Vertices.Count);
        foreach (var v in mesh.Vertices)
        {
            writer.Write(v.X);
            writer.Write(v.Y);
            writer.Write(v.Z);
        }

        // IMesh 接口不暴露顶点颜色与面集合，按具体类型读取。
        var colors = mesh is MeshT t0 ? t0.VertexColors : ((Mesh)mesh).VertexColors;
        writer.Write(colors?.Count ?? -1);
        if (colors != null)
            foreach (var c in colors)
            {
                writer.Write(c.R);
                writer.Write(c.G);
                writer.Write(c.B);
            }

        if (mesh is MeshT textured)
        {
            writer.Write(textured.TextureVertices.Count);
            foreach (var vt in textured.TextureVertices)
            {
                writer.Write(vt.X);
                writer.Write(vt.Y);
            }

            writer.Write(textured.Faces.Count);
            foreach (var f in textured.Faces)
            {
                writer.Write(f.IndexA);
                writer.Write(f.IndexB);
                writer.Write(f.IndexC);
                writer.Write(f.TextureIndexA);
                writer.Write(f.TextureIndexB);
                writer.Write(f.TextureIndexC);
                writer.Write(f.MaterialIndex);
            }

            writer.Write(textured.Materials.Count);
            foreach (var m in textured.Materials)
                WriteMaterial(writer, m);
        }
        else
        {
            var plain = (Mesh)mesh;
            writer.Write(plain.Faces.Count);
            foreach (var f in plain.Faces)
            {
                writer.Write(f.IndexA);
                writer.Write(f.IndexB);
                writer.Write(f.IndexC);
            }
        }
    }

    private static IMesh ReadMesh(BinaryReader reader)
    {
        if (reader.ReadInt32() != Magic) throw new InvalidDataException("不是有效的网格暂存文件");
        if (reader.ReadInt32() != Version) throw new InvalidDataException("网格暂存格式版本不匹配");
        var isTextured = reader.ReadByte() == 1;
        var name = reader.ReadString();
        var debugName = reader.ReadString();

        var vertexCount = reader.ReadInt32();
        var vertices = new Vertex3[vertexCount];
        for (var i = 0; i < vertexCount; i++)
            vertices[i] = new Vertex3(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());

        var colorCount = reader.ReadInt32();
        RGB[]? colors = null;
        if (colorCount >= 0)
        {
            colors = new RGB[colorCount];
            for (var i = 0; i < colorCount; i++)
                colors[i] = new RGB(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
        }

        if (isTextured)
        {
            var uvCount = reader.ReadInt32();
            var uvs = new Vertex2[uvCount];
            for (var i = 0; i < uvCount; i++)
                uvs[i] = new Vertex2(reader.ReadDouble(), reader.ReadDouble());

            var faceCount = reader.ReadInt32();
            var faces = new FaceT[faceCount];
            for (var i = 0; i < faceCount; i++)
                faces[i] = new FaceT(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
                    reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());

            var materialCount = reader.ReadInt32();
            var materials = new Material[materialCount];
            for (var i = 0; i < materialCount; i++)
                materials[i] = ReadMaterial(reader);

            return new MeshT(vertices, uvs, faces, materials, colors)
            {
                Name = name,
                DebugName = debugName
            };
        }

        var plainFaceCount = reader.ReadInt32();
        var plainFaces = new Face[plainFaceCount];
        for (var i = 0; i < plainFaceCount; i++)
            plainFaces[i] = new Face(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());

        return new Mesh(vertices, plainFaces, colors)
        {
            Name = name,
            DebugName = debugName
        };
    }

    private static void WriteMaterial(BinaryWriter writer, Material m)
    {
        writer.Write(m.Name);
        writer.Write(m.Texture ?? "");
        writer.Write(m.NormalMap ?? "");
        WriteColor(writer, m.AmbientColor);
        WriteColor(writer, m.DiffuseColor);
        WriteColor(writer, m.SpecularColor);
        writer.Write(m.SpecularExponent ?? double.NaN);
        writer.Write(m.Dissolve ?? double.NaN);
        writer.Write(m.IlluminationModel.HasValue ? (int)m.IlluminationModel.Value : -1);
    }

    private static Material ReadMaterial(BinaryReader reader)
    {
        var name = reader.ReadString();
        var texture = reader.ReadString();
        var normalMap = reader.ReadString();
        var ambient = ReadColor(reader);
        var diffuse = ReadColor(reader);
        var specular = ReadColor(reader);
        var specularExponent = reader.ReadDouble();
        var dissolve = reader.ReadDouble();
        var illumination = reader.ReadInt32();
        return new Material(name,
            texture.Length > 0 ? texture : null,
            normalMap.Length > 0 ? normalMap : null,
            ambient, diffuse, specular,
            double.IsNaN(specularExponent) ? null : specularExponent,
            double.IsNaN(dissolve) ? null : dissolve,
            illumination >= 0 ? (IlluminationModel)illumination : null);
    }

    private static void WriteColor(BinaryWriter writer, RGB? color)
    {
        writer.Write(color != null);
        if (color != null)
        {
            writer.Write(color.R);
            writer.Write(color.G);
            writer.Write(color.B);
        }
    }

    private static RGB? ReadColor(BinaryReader reader) =>
        reader.ReadBoolean() ? new RGB(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()) : null;
}
