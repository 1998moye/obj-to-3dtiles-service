namespace ModelConversion.Service.Domain;

// 预检只读取图片头部与元数据识别格式，不完整解码像素。
public enum InputTextureFormat
{
    Png,
    Jpeg,
    Webp,
    Unknown
}

// 单张被引用纹理的预检信息；EstimatedDecodedBytes 是按 RGBA32 展开的字节估算。
// FullPath 是解析后的绝对路径，供规范化流水线按路径匹配（RelativePath 仅供展示）。
public sealed record InputTextureInfo(
    string RelativePath,
    InputTextureFormat Format,
    bool HeaderReadable,
    int Width,
    int Height,
    long FileBytes,
    string FullPath = "")
{
    public long EstimatedDecodedBytes => HeaderReadable ? (long)Width * Height * 4L : 0L;
    public int MaxEdge => Math.Max(Width, Height);
}

// 输入模型的元数据摘要：几何规模来自 OBJ 流式行扫描，纹理来自 MTL 引用与头部预检。
// 同一文件被多个材质重复引用时只统计一次（按解析后的绝对路径去重）。
public sealed record InputModelSummary(
    string InputPath,
    long ObjBytes,
    int VertexCount,
    int TextureVertexCount,
    int FaceCount,
    IReadOnlyList<string> MaterialLibraries,
    IReadOnlyList<InputTextureInfo> Textures,
    long TotalInputBytes)
{
    public long TotalDecodedTextureBytes => Textures.Sum(texture => texture.EstimatedDecodedBytes);
    public long MaxTextureDecodedBytes => Textures.Count == 0 ? 0 : Textures.Max(texture => texture.EstimatedDecodedBytes);
    public int MaxTextureEdge => Textures.Count == 0 ? 0 : Textures.Max(texture => texture.MaxEdge);
    public bool HasUnreadableTextures => Textures.Any(texture => !texture.HeaderReadable);
}
