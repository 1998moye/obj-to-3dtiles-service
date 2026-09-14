using System.Security.Cryptography;
using ModelConversion.Service.Application;
using ModelConversion.Service.Domain;
using NetVips;

namespace ModelConversion.Service.Infrastructure.Textures;

// 纹理规范化深模块：把只读源输入准备成任务专属的规范化输入。
// Interface 只暴露 PrepareAsync 与准备结果（PreparedConversionInput），
// libvips 顺序流式解码、临时 MTL 重写、命名冲突处理全部隐藏在 Implementation 内。
// 同一 Seam 的两个 Adapter：无需变换时直通（零拷贝、不建临时目录），
// 需要时顺序流式规范化（libvips Access.Sequential，单张超大纹理不完整常驻内存）。
public sealed class TextureNormalizationPipeline(ILogger<TextureNormalizationPipeline> logger)
{
    // 规范化副本目录名：位于暂存目录内，成功转换后由清理句柄在发布前删除，
    // 失败/取消由暂存清理兜底，不会随输出发布。
    private const string NormalizedDirectoryName = "normalized-input";

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static int _libVipsConfigured;

    public async Task<PreparedConversionInput> PrepareAsync(
        ConversionRunRequest request,
        InputModelSummary input,
        ConversionResourcePlan plan,
        CancellationToken cancellationToken)
    {
        // 直通 Adapter：没有超过边长上限的可读纹理时零拷贝，转换器直接读源输入。
        // 头部不可读的纹理保持直通，与预检口径一致，不在此引入新的失败面。
        var oversized = input.Textures
            .Where(texture => texture.HeaderReadable && texture.MaxEdge > plan.MaxSourceTextureEdge)
            .ToList();
        if (oversized.Count == 0)
            return PreparedConversionInput.PassThrough(request.InputPath);

        var normalizedRoot = Path.Combine(request.StagingPath, NormalizedDirectoryName);
        var result = await Task.Run(
            () => PrepareNormalizedCopy(request, input, plan, oversized, normalizedRoot, cancellationToken),
            cancellationToken);
        logger.LogInformation(
            "转换运行 {RunId} 纹理规范化完成：{Normalized}/{Total} 张超上限纹理降到 {MaxEdge}px 以内",
            request.RunId, result.NormalizedTextureCount, oversized.Count, plan.MaxSourceTextureEdge);
        return result;
    }

    private PreparedConversionInput PrepareNormalizedCopy(
        ConversionRunRequest request,
        InputModelSummary input,
        ConversionResourcePlan plan,
        List<InputTextureInfo> oversized,
        string normalizedRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(normalizedRoot);
        var objDirectory = Path.GetDirectoryName(Path.GetFullPath(request.InputPath)) ?? string.Empty;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tempNameByFullPath = new Dictionary<string, string>(PathComparer);

        // 1. 流式复制 OBJ 并重写 mtllib：MTL 副本平铺到规范化目录，同名冲突加短哈希后缀。
        var mtlCopyBySourcePath = new Dictionary<string, string>(PathComparer);
        var preparedObjPath = Path.Combine(normalizedRoot, Path.GetFileName(request.InputPath));
        CopyObjWithRewrittenMtlReferences(request.InputPath, preparedObjPath, objDirectory,
            usedNames, mtlCopyBySourcePath, cancellationToken);

        // 2. 逐张顺序流式规范化超大纹理；libvips 无法解码的退化引用源文件（与旧行为一致）。
        foreach (var texture in oversized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetName = AllocateTempName(usedNames, texture.FullPath);
            var targetPath = Path.Combine(normalizedRoot, targetName);
            try
            {
                DownscaleStreaming(texture.FullPath, targetPath, plan.MaxSourceTextureEdge);
                tempNameByFullPath[texture.FullPath] = targetName;
                logger.LogInformation("转换运行 {RunId} 纹理规范化 {Name}: {Width}x{Height} → 上限 {MaxEdge}px",
                    request.RunId, texture.RelativePath, texture.Width, texture.Height, plan.MaxSourceTextureEdge);
            }
            catch (VipsException exception)
            {
                // 头部可读但解码失败的纹理直通源文件：转换器看到的是与旧版本一致的输入。
                logger.LogWarning(exception, "转换运行 {RunId} 纹理 {Name} 规范化失败，改为引用源文件",
                    request.RunId, texture.RelativePath);
            }
        }

        // 3. 流式重写每个被引用的 MTL：已规范化纹理指向临时副本文件名，
        // 其余可解析纹理改写为源文件绝对路径（临时目录下的相对引用已失效），
        // 解析失败的引用保持原行不变（转换器行为与旧版本一致）。
        foreach (var (sourceMtlPath, tempMtlName) in mtlCopyBySourcePath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyMtlWithRewrittenTextureReferences(sourceMtlPath,
                Path.Combine(normalizedRoot, tempMtlName), objDirectory, tempNameByFullPath);
        }

        return PreparedConversionInput.Normalized(preparedObjPath, normalizedRoot, tempNameByFullPath.Count);
    }

    // 逐行复制 OBJ，仅改写 mtllib 行指向平铺后的 MTL 副本文件名；几何内容原样保留。
    private static void CopyObjWithRewrittenMtlReferences(
        string sourceObjPath,
        string targetObjPath,
        string objDirectory,
        HashSet<string> usedNames,
        Dictionary<string, string> mtlCopyBySourcePath,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(sourceObjPath);
        using var writer = new StreamWriter(targetObjPath);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            if ((++lineNumber & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("mtllib ", StringComparison.Ordinal))
            {
                var mtlReference = trimmed["mtllib ".Length..].Trim();
                var sourceMtlPath = Path.GetFullPath(Path.Combine(objDirectory, mtlReference));
                if (File.Exists(sourceMtlPath))
                {
                    if (!mtlCopyBySourcePath.TryGetValue(sourceMtlPath, out var tempName))
                    {
                        tempName = AllocateTempName(usedNames, sourceMtlPath);
                        mtlCopyBySourcePath[sourceMtlPath] = tempName;
                    }
                    writer.WriteLine(line[..(line.Length - trimmed.Length)] + "mtllib " + tempName);
                    continue;
                }
                // MTL 源文件缺失时保持原行，转换器按旧行为报错或跳过。
            }
            writer.WriteLine(line);
        }
    }

    private static void CopyMtlWithRewrittenTextureReferences(
        string sourceMtlPath,
        string targetMtlPath,
        string objDirectory,
        IReadOnlyDictionary<string, string> tempNameByFullPath)
    {
        var mtlDirectory = Path.GetDirectoryName(sourceMtlPath) ?? string.Empty;
        using var reader = new StreamReader(sourceMtlPath);
        using var writer = new StreamWriter(targetMtlPath);
        while (reader.ReadLine() is { } line)
        {
            if (!MtlTextureReferenceParser.TryParseLine(line, mtlDirectory, objDirectory, out var reference)
                || reference!.ResolvedPath is null)
            {
                writer.WriteLine(line);
                continue;
            }
            var newPath = tempNameByFullPath.TryGetValue(reference.ResolvedPath, out var tempName)
                ? tempName
                : reference.ResolvedPath;
            writer.WriteLine($"{reference.LeadingWhitespace}{reference.Keyword} {reference.OptionsPrefix}{newPath}");
        }
    }

    // libvips 顺序访问 + 按需解码：像素按行流过缩放管线，单张超大纹理无需完整 RGBA 常驻。
    private static void DownscaleStreaming(string sourcePath, string targetPath, int maxEdge)
    {
        EnsureLibVipsConfigured();
        using var image = Image.NewFromFile(sourcePath, access: Enums.Access.Sequential);
        var scale = (double)maxEdge / Math.Max(image.Width, image.Height);
        if (scale >= 1.0)
        {
            // 防御：预检与 libvips 尺寸口径不一致时直接复制，绝不放大。
            File.Copy(sourcePath, targetPath, overwrite: true);
            return;
        }
        using var resized = image.Resize(scale);
        // 中间副本 JPEG 用 Q=90 减轻二次编码损失；PNG/WebP 按扩展名默认参数（PNG 无损）。
        resized.WriteToFile(targetPath + (targetPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || targetPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "[Q=90]" : string.Empty));
    }

    private static void EnsureLibVipsConfigured()
    {
        if (Interlocked.CompareExchange(ref _libVipsConfigured, 1, 0) != 0) return;
        // 收紧 libvips 全局缓存：任务级纹理由资源计划预算控制，native 缓存只留小余量。
        Cache.MaxMem = 32L << 20;
        Cache.Max = 64;
        Cache.MaxFiles = 0;
    }

    // 规范化目录平铺命名：保留原文件名，同名冲突时追加源路径短哈希，保证确定性。
    private static string AllocateTempName(HashSet<string> usedNames, string sourcePath)
    {
        var name = Path.GetFileName(sourcePath);
        if (usedNames.Add(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sourcePath)))
            .Substring(0, 8);
        var candidate = $"{stem}-{hash}{extension}";
        var counter = 0;
        while (!usedNames.Add(candidate)) candidate = $"{stem}-{hash}-{++counter}{extension}";
        return candidate;
    }
}
