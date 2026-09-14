using System.Globalization;
using ModelConversion.Service.Application;
using ModelConversion.Service.Domain;
using ModelConversion.Service.Infrastructure.Textures;

namespace ModelConversion.Service.Infrastructure.Geometry;

// [2026-09-07 深层封底剥离] 摄影测量（ODM/OpenMVS）封闭网格常带深层封洞/封底几何：
// 实测用户模型 146506 个顶点中一半埋在地下 227 米以下（最深 -567m），渲染成灰色块，
// 且 95% 的分格代表高度落在深层簇，把自动高度回正带偏到悬空几百米。
// 本模块在纹理规范化之后、Obj2Tiles 转换之前，把远低于地表带的面片从任务专属 OBJ 副本中
// 剔除；源输入全程只读。默认开启，StripDeepBottom=false 直通。
//
// 算法（顶点 Z 直方图 + 密度悬崖，与后端 ImportedTilesetGroundEstimator 的分格口径不同：
// 该模型裙边顶点从 -435m 连续分布到地表，无任何 2m 空桶，最大落差法与空桶法都会失效；
// 唯一稳定特征是密度——地表带每桶 1.6%~3.3%，封底帘每桶 ≤0.6%，崖口清晰）：
//   1. 2m 桶直方图，找峰值桶，向下游走到桶密度 < 峰值×DensityCliffRatio，得地表带底；
//   2. 带底距模型最低点 < DeepBottomMinDropMeters 时不存在可识别的封底（保护屋顶主导
//      的城市模型：薄墙体密度低会误判带底，但此时带底紧邻模型底部，落差不足直接直通）；
//   3. 剥离阈值 = 带底 - DeepBottomMarginMeters，任一顶点低于阈值的面片剔除；默认余量
//      等于一个直方图桶（2m），避免旧默认 15m 在地表下保留一圈可见浅层裙边；
//   4. 剥离后压缩未被剩余面引用的 v 顶点并重映射面索引，使 bounds/HLOD 只观察有效几何。
//      阈值落在地表与封底帘之间的宽大稀疏区内，区域内具体位置不影响剥离结果。
// 护栏：剥离 0 张或超过 MaxStripFraction 都直通（后者说明结构判断失误，宁直通不毁模型）。
public sealed class DeepBottomGeometryFilter(ILogger<DeepBottomGeometryFilter> logger)
{
    // 剥离副本目录名：位于暂存目录内，转换成功后由清理句柄在发布前删除，不混入输出。
    private const string FilteredDirectoryName = "stripped-input";
    private const double HistogramBinMeters = 2.0;
    private const double DensityCliffRatio = 0.15;
    private const double MaxStripFraction = 0.9;
    // 与高度估算器同源的最小样本量：顶点过少时直方图不可靠
    private const int MinVertexCount = 120;

    public Task<PreparedConversionInput> FilterAsync(
        ConversionRunRequest request, string objPath, CancellationToken cancellationToken)
    {
        if (!request.Settings.StripDeepBottom)
            return Task.FromResult(PreparedConversionInput.PassThrough(objPath));
        return Task.Run(() => Filter(request, objPath, cancellationToken), cancellationToken);
    }

    private PreparedConversionInput Filter(
        ConversionRunRequest request, string objPath, CancellationToken cancellationToken)
    {
        var settings = request.Settings;

        // 第一遍：只收集顶点 Z（面片过滤按索引随机查 Z，XY 不参与判定，不落内存）
        var vertexZ = new List<float>();
        using (var reader = new StreamReader(objPath))
        {
            string? line;
            var lineNumber = 0;
            while ((line = reader.ReadLine()) != null)
            {
                if ((++lineNumber & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (TryParseVertexZ(line, out var z)) vertexZ.Add(z);
            }
        }
        if (vertexZ.Count < MinVertexCount)
        {
            logger.LogInformation("转换运行 {RunId} 深层封底剥离跳过：顶点数 {Count} 不足 {Min}",
                request.RunId, vertexZ.Count, MinVertexCount);
            return PreparedConversionInput.PassThrough(objPath);
        }

        var zArray = vertexZ.ToArray();
        var minZ = zArray.Min();
        var maxZ = zArray.Max();
        var totalRange = maxZ - minZ;
        if (totalRange < settings.DeepBottomMinDropMeters)
        {
            logger.LogInformation("转换运行 {RunId} 深层封底剥离直通：模型总高差 {Range:F1}m 低于判定落差 {MinDrop:F1}m",
                request.RunId, totalRange, settings.DeepBottomMinDropMeters);
            return PreparedConversionInput.PassThrough(objPath);
        }

        // 直方图 + 密度悬崖找地表带底
        var binCount = (int)(totalRange / HistogramBinMeters) + 1;
        var histogram = new int[binCount];
        foreach (var z in zArray) histogram[(int)((z - minZ) / HistogramBinMeters)]++;
        var peakBin = 0;
        for (var i = 1; i < binCount; i++) if (histogram[i] > histogram[peakBin]) peakBin = i;
        var densityFloor = histogram[peakBin] * DensityCliffRatio;
        var bandBottomBin = peakBin;
        while (bandBottomBin > 0 && histogram[bandBottomBin - 1] >= densityFloor) bandBottomBin--;
        var bandBottom = minZ + bandBottomBin * HistogramBinMeters;

        var drop = bandBottom - minZ;
        if (drop < settings.DeepBottomMinDropMeters)
        {
            logger.LogInformation("转换运行 {RunId} 深层封底剥离直通：地表带底 {BandBottom:F1}m 距最低点仅 {Drop:F1}m，无可识别封底",
                request.RunId, bandBottom, drop);
            return PreparedConversionInput.PassThrough(objPath);
        }
        var threshold = bandBottom - settings.DeepBottomMarginMeters;

        // 第二遍：逐行复制，剔除深层面片；mtllib 改写为绝对路径（副本脱离源目录后相对引用会失效）
        var filteredDir = Path.Combine(request.StagingPath, FilteredDirectoryName);
        Directory.CreateDirectory(filteredDir);
        var filteredObjPath = Path.Combine(filteredDir, Path.GetFileName(objPath));
        var objDirectory = Path.GetDirectoryName(Path.GetFullPath(objPath)) ?? string.Empty;
        var totalFaces = 0;
        var strippedFaces = 0;
        var verticesSeen = 0;
        try
        {
            using var reader = new StreamReader(objPath);
            using var writer = new StreamWriter(filteredObjPath);
            string? line;
            var lineNumber = 0;
            while ((line = reader.ReadLine()) != null)
            {
                if ((++lineNumber & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("v ", StringComparison.Ordinal))
                {
                    verticesSeen++;
                    writer.WriteLine(line);
                    continue;
                }
                if (trimmed.StartsWith("f ", StringComparison.Ordinal))
                {
                    totalFaces++;
                    if (IsDeepFace(trimmed, zArray, verticesSeen, threshold))
                    {
                        strippedFaces++;
                        continue;
                    }
                    writer.WriteLine(line);
                    continue;
                }
                if (trimmed.StartsWith("mtllib ", StringComparison.Ordinal))
                {
                    var reference = trimmed["mtllib ".Length..].Trim();
                    var sourceMtlPath = Path.GetFullPath(Path.Combine(objDirectory, reference));
                    // 源 MTL 缺失时保持原行（与纹理规范化同口径：转换器按旧行为报错或跳过）
                    writer.WriteLine(File.Exists(sourceMtlPath)
                        ? line[..(line.Length - trimmed.Length)] + "mtllib " + sourceMtlPath
                        : line);
                    continue;
                }
                writer.WriteLine(line);
            }
        }
        catch
        {
            TryDeleteFilteredCopy(filteredDir);
            throw;
        }

        if (strippedFaces == 0)
        {
            TryDeleteFilteredCopy(filteredDir);
            logger.LogInformation("转换运行 {RunId} 深层封底剥离直通：阈值 {Threshold:F1}m 下无深层面片",
                request.RunId, threshold);
            return PreparedConversionInput.PassThrough(objPath);
        }
        var stripFraction = (double)strippedFaces / totalFaces;
        if (stripFraction > MaxStripFraction)
        {
            TryDeleteFilteredCopy(filteredDir);
            logger.LogWarning("转换运行 {RunId} 深层封底剥离中止：将剥离 {Stripped}/{Total} 张面片（{Fraction:P0}），" +
                              "超过护栏 {Max:P0}，疑似结构误判，保持原样直通",
                request.RunId, strippedFaces, totalFaces, stripFraction, MaxStripFraction);
            return PreparedConversionInput.PassThrough(objPath);
        }

        CompactionResult compaction;
        try
        {
            // [2026-09-07 修复封底残留] 原代码: 删除面片后直接把包含全部 v 行的 OBJ 交给 Obj2Tiles。
            // 原因: 深层面虽已删除，但无引用顶点仍把 bounds 拉到地下数百米，继续污染 HLOD 切分与高度判断。
            compaction = CompactReferencedVertices(filteredObjPath, cancellationToken);
        }
        catch
        {
            TryDeleteFilteredCopy(filteredDir);
            throw;
        }

        logger.LogInformation(
            "转换运行 {RunId} 深层封底剥离完成：地表带底 {BandBottom:F1}m，阈值 {Threshold:F1}m，" +
            "剥离 {Stripped}/{Total} 张面片（{Fraction:P1}），有效顶点 {Retained}/{Original}，" +
            "有效 Z 范围 [{MinZ:F1}, {MaxZ:F1}]",
            request.RunId, bandBottom, threshold, strippedFaces, totalFaces, stripFraction,
            compaction.RetainedVertices, compaction.OriginalVertices, compaction.MinZ, compaction.MaxZ);
        // 复用规范化输入的清理句柄语义：计数位承载剥离面片数，仅用于日志观察
        return PreparedConversionInput.Normalized(filteredObjPath, filteredDir, strippedFaces);
    }

    private static CompactionResult CompactReferencedVertices(
        string objPath, CancellationToken cancellationToken)
    {
        var vertexCount = 0;
        using (var reader = new StreamReader(objPath))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
                if (line.AsSpan().TrimStart().StartsWith("v ", StringComparison.Ordinal)) vertexCount++;
        }

        var referenced = new bool[vertexCount];
        using (var reader = new StreamReader(objPath))
        {
            string? line;
            var verticesSeen = 0;
            var lineNumber = 0;
            while ((line = reader.ReadLine()) != null)
            {
                if ((++lineNumber & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("v ", StringComparison.Ordinal))
                {
                    verticesSeen++;
                    continue;
                }
                if (!trimmed.StartsWith("f ", StringComparison.Ordinal)) continue;
                foreach (var token in trimmed[2..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!TryResolveFaceVertexIndex(token, verticesSeen, vertexCount, out var resolved))
                        throw new InvalidDataException($"无法压缩 OBJ：非法面顶点索引 {token}");
                    referenced[resolved] = true;
                }
            }
        }

        var remap = new int[vertexCount];
        var retainedCount = 0;
        for (var i = 0; i < referenced.Length; i++)
            if (referenced[i]) remap[i] = ++retainedCount;
        if (retainedCount == 0) throw new InvalidDataException("无法压缩 OBJ：剥离后没有有效顶点");

        var compactedPath = objPath + ".compacting";
        var minZ = float.PositiveInfinity;
        var maxZ = float.NegativeInfinity;
        try
        {
            {
                // 写入流必须在原子替换前关闭；Windows 不允许覆盖仍被打开的目标文件。
                using var reader = new StreamReader(objPath);
                using var writer = new StreamWriter(compactedPath);
                string? line;
                var verticesSeen = 0;
                var lineNumber = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    if ((++lineNumber & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("v ", StringComparison.Ordinal))
                    {
                        var oldIndex = verticesSeen++;
                        if (!referenced[oldIndex]) continue;
                        writer.WriteLine(line);
                        if (TryParseVertexZ(line, out var z))
                        {
                            minZ = Math.Min(minZ, z);
                            maxZ = Math.Max(maxZ, z);
                        }
                        continue;
                    }
                    if (trimmed.StartsWith("f ", StringComparison.Ordinal))
                    {
                        var indent = line[..(line.Length - trimmed.Length)];
                        var tokens = trimmed[2..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        writer.Write(indent);
                        writer.Write('f');
                        foreach (var token in tokens)
                        {
                            if (!TryResolveFaceVertexIndex(token, verticesSeen, vertexCount, out var resolved))
                                throw new InvalidDataException($"无法压缩 OBJ：非法面顶点索引 {token}");
                            var slash = token.IndexOf('/');
                            writer.Write(' ');
                            writer.Write(remap[resolved].ToString(CultureInfo.InvariantCulture));
                            if (slash >= 0) writer.Write(token[slash..]);
                        }
                        writer.WriteLine();
                        continue;
                    }
                    // Obj2Tiles 只消费三角面；点、线和曲面索引不进入临时转换副本，避免引用已压缩的旧 v 索引。
                    if (trimmed.StartsWith("p ", StringComparison.Ordinal) ||
                        trimmed.StartsWith("l ", StringComparison.Ordinal) ||
                        trimmed.StartsWith("curv ", StringComparison.Ordinal) ||
                        trimmed.StartsWith("surf ", StringComparison.Ordinal)) continue;
                    writer.WriteLine(line);
                }
                writer.Flush();
            }
            File.Move(compactedPath, objPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(compactedPath)) File.Delete(compactedPath);
        }

        return new CompactionResult(vertexCount, retainedCount, minZ, maxZ);
    }

    private static bool TryResolveFaceVertexIndex(
        string token, int verticesSeen, int totalVertices, out int resolved)
    {
        resolved = -1;
        var slash = token.IndexOf('/');
        var indexText = slash >= 0 ? token[..slash] : token;
        if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index == 0)
            return false;
        resolved = index > 0 ? index - 1 : verticesSeen + index;
        return resolved >= 0 && resolved < totalVertices;
    }

    private sealed record CompactionResult(
        int OriginalVertices, int RetainedVertices, float MinZ, float MaxZ);

    /// 面片任一角点顶点 Z 低于阈值即判为深层面片；解析失败的面片保守保留（不剥离无法确认的）。
    private static bool IsDeepFace(string faceLine, float[] vertexZ, int verticesSeen, double threshold)
    {
        // OBJ 角点格式 v/vt/vn，分隔符兼容空格与制表符
        var span = faceLine.AsSpan(2);
        foreach (var tokenRange in span.SplitAny(' ', '\t'))
        {
            var token = span[tokenRange];
            if (token.IsEmpty) continue;
            var slash = token.IndexOf('/');
            var indexText = slash >= 0 ? token[..slash] : token;
            if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                return false;
            var resolved = index > 0 ? index - 1 : verticesSeen + index;
            if (index == 0 || resolved < 0 || resolved >= vertexZ.Length)
                return false;
            if (vertexZ[resolved] < threshold) return true;
        }
        return false;
    }

    private static bool TryParseVertexZ(string line, out float z)
    {
        z = 0;
        // 前导空白行也要识别（与面片过滤的 TrimStart 口径一致）
        var content = line.AsSpan().TrimStart();
        if (!content.StartsWith("v ", StringComparison.Ordinal)) return false;
        var span = content[2..].TrimStart();
        // v x y z [w]：取第三个分量；逐个字段推进，避免 Split 分配；分隔符兼容空格与制表符
        for (var field = 0; field < 3; field++)
        {
            var end = span.IndexOfAny(' ', '\t');
            if (end < 0)
            {
                if (field != 2) return false;
                return float.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out z);
            }
            if (field == 2)
                return float.TryParse(span[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out z);
            span = span[(end + 1)..].TrimStart();
            if (span.IsEmpty) return false;
        }
        return false;
    }

    private void TryDeleteFilteredCopy(string filteredDir)
    {
        try
        {
            if (Directory.Exists(filteredDir)) Directory.Delete(filteredDir, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不阻断转换；副本位于暂存目录内，由暂存清理兜底
            logger.LogWarning("深层封底剥离副本清理失败: {Message}", exception.Message);
        }
    }
}
