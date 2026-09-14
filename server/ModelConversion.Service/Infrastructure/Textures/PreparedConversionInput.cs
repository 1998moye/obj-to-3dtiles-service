namespace ModelConversion.Service.Infrastructure.Textures;

// 规范化输入的准备结果与清理句柄：Interface 只暴露转换器可用的输入路径与生命周期，
// 不泄漏任何图像处理步骤。Dispose 幂等；直通模式下不拥有任何资源。
public sealed class PreparedConversionInput : IDisposable
{
    private readonly string? _ownedDirectory;

    private PreparedConversionInput(string inputPath, string? ownedDirectory, int normalizedTextureCount)
    {
        InputPath = inputPath;
        _ownedDirectory = ownedDirectory;
        NormalizedTextureCount = normalizedTextureCount;
    }

    // 转换器实际使用的 OBJ 路径：直通时为源文件，规范化后为任务专属临时副本。
    public string InputPath { get; }

    public bool IsPassThrough => _ownedDirectory is null;

    public int NormalizedTextureCount { get; }

    public static PreparedConversionInput PassThrough(string inputPath) => new(inputPath, null, 0);

    internal static PreparedConversionInput Normalized(string inputPath, string ownedDirectory, int count) =>
        new(inputPath, ownedDirectory, count);

    public void Dispose()
    {
        if (_ownedDirectory is null) return;
        try
        {
            if (Directory.Exists(_ownedDirectory)) Directory.Delete(_ownedDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不掩盖转换结果；规范化目录位于暂存内，暂存清理会再次兜底。
        }
    }
}
