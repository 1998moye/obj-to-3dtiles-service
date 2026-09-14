using System.Buffers.Binary;
using ModelConversion.Service.Domain;
using ModelConversion.Service.Infrastructure.Textures;

namespace ModelConversion.Service.Infrastructure.Resources;

// 输入模型预检：流式扫描 OBJ 行统计几何规模，解析 MTL 纹理引用，
// 对每张纹理只读取文件头识别尺寸，不完整解码像素。
// 纹理引用解析统一走 MtlTextureReferenceParser（与 Obj2Tiles 语义一致），
// 保证预检、规范化流水线与转换器实际加载的是同一个纹理集合。
public sealed class InputModelInspector
{
    // 图片头扫描最多读取的字节数：JPEG 的 SOF 标记通常在文件很靠前的位置。
    private const int MaxHeaderScanBytes = 256 * 1024;

    public async Task<InputModelSummary> InspectAsync(string inputPath, CancellationToken cancellationToken)
    {
        var objBytes = new FileInfo(inputPath).Length;
        var objDirectory = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? string.Empty;

        var vertexCount = 0;
        var textureVertexCount = 0;
        var faceCount = 0;
        var mtlReferences = new List<string>();

        // 流式逐行扫描，不把 OBJ 全文读入内存。
        using (var reader = new StreamReader(inputPath))
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var span = line.AsSpan().TrimStart();
                if (span.StartsWith("v ", StringComparison.Ordinal)) vertexCount++;
                else if (span.StartsWith("vt ", StringComparison.Ordinal)) textureVertexCount++;
                else if (span.StartsWith("f ", StringComparison.Ordinal)) faceCount++;
                else if (span.StartsWith("mtllib ", StringComparison.Ordinal))
                    mtlReferences.Add(span["mtllib ".Length..].Trim().ToString());
            }
        }

        var textures = new List<InputTextureInfo>();
        // 同一文件被多个材质重复引用只统计一次；Linux 文件系统区分大小写。
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        var totalInputBytes = objBytes;

        foreach (var mtlName in mtlReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mtlPath = Path.GetFullPath(Path.Combine(objDirectory, mtlName));
            if (!File.Exists(mtlPath))
                continue;
            totalInputBytes += new FileInfo(mtlPath).Length;

            var mtlDirectory = Path.GetDirectoryName(mtlPath) ?? string.Empty;
            foreach (var line in File.ReadLines(mtlPath))
            {
                // 与规范化流水线共用同一个引用解析器；找不到文件的引用跳过。
                if (!MtlTextureReferenceParser.TryParseLine(line, mtlDirectory, objDirectory, out var reference)
                    || reference!.ResolvedPath is null)
                    continue;
                if (!seen.Add(reference.ResolvedPath)) continue;
                textures.Add(InspectTexture(reference.ResolvedPath, objDirectory));
                totalInputBytes += new FileInfo(reference.ResolvedPath).Length;
            }
        }

        return new InputModelSummary(
            inputPath, objBytes, vertexCount, textureVertexCount, faceCount,
            mtlReferences, textures, totalInputBytes);
    }

    // 只读取文件头识别格式与尺寸；头部不可识别时按原样直通（HeaderReadable=false）。
    private static InputTextureInfo InspectTexture(string fullPath, string objDirectory)
    {
        var fileBytes = new FileInfo(fullPath).Length;
        var relative = Path.GetRelativePath(objDirectory, fullPath);

        var headerLength = (int)Math.Min(fileBytes, MaxHeaderScanBytes);
        var buffer = new byte[headerLength];
        using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var offset = 0;
            while (offset < headerLength)
            {
                var read = stream.Read(buffer, offset, headerLength - offset);
                if (read == 0) break;
                offset += read;
            }
            if (offset < headerLength) Array.Resize(ref buffer, offset);
        }

        if (TryReadPng(buffer, out var width, out var height))
            return new InputTextureInfo(relative, InputTextureFormat.Png, true, width, height, fileBytes, fullPath);
        if (TryReadJpeg(buffer, out width, out height))
            return new InputTextureInfo(relative, InputTextureFormat.Jpeg, true, width, height, fileBytes, fullPath);
        if (TryReadWebp(buffer, out width, out height))
            return new InputTextureInfo(relative, InputTextureFormat.Webp, true, width, height, fileBytes, fullPath);
        return new InputTextureInfo(relative, InputTextureFormat.Unknown, false, 0, 0, fileBytes, fullPath);
    }

    private static bool TryReadPng(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length < 26 || !data[..8].SequenceEqual(signature)) return false;
        if (!data.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4));
        return width > 0 && height > 0;
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;
        var offset = 2;
        while (offset + 3 < data.Length)
        {
            if (data[offset] != 0xFF) { offset++; continue; }
            var marker = data[offset + 1];
            // 无负载标记（RSTn/TEM/DNL 等）直接跳过。
            if (marker is >= 0xD0 and <= 0xD8 or 0x01) { offset += 2; continue; }
            if (offset + 4 > data.Length) return false;
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
            if (segmentLength < 2) return false;
            // SOFn：C0–CF，排除 DHT(C4)/JPG(C8)/DAC(CC)。
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (offset + 8 > data.Length) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 5, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 7, 2));
                return width > 0 && height > 0;
            }
            offset += 2 + segmentLength;
        }
        return false;
    }

    private static bool TryReadWebp(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        // 全局只挡到能读 chunk 四字节码的长度；各编码分支再按自身布局精确校验长度，
        // 否则最简 VP8L（25 字节）会被 VP8X 需要的 30 字节门槛误杀。
        if (data.Length < 20 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WEBP"u8))
            return false;
        var chunk = data.Slice(12, 4);
        if (chunk.SequenceEqual("VP8X"u8) && data.Length >= 30)
        {
            // VP8X：chunk 尺寸(16-19)之后是 1 字节标志(20) + 3 字节保留(21-23)，
            // 宽高减一各 3 字节小端，位于 24-26 与 27-29。
            width = (data[24] | (data[25] << 8) | (data[26] << 16)) + 1;
            height = (data[27] | (data[28] << 8) | (data[29] << 16)) + 1;
            return true;
        }
        if (chunk.SequenceEqual("VP8 "u8) && data.Length >= 30)
        {
            // 有损 VP8：帧标记 3 字节 + 起始码 9D 01 2A 后，宽高各 2 字节小端（低 14 位）。
            if (data[23] != 0x9D || data[24] != 0x01 || data[25] != 0x2A) return false;
            width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(26, 2)) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(28, 2)) & 0x3FFF;
            return width > 0 && height > 0;
        }
        if (chunk.SequenceEqual("VP8L"u8) && data.Length >= 25)
        {
            // 无损 VP8L：载荷首字节(20)为签名 0x2F，随后 4 字节按位打包 14 位宽减一、14 位高减一。
            if (data[20] != 0x2F) return false;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(21, 4));
            width = (int)(bits & 0x3FFF) + 1;
            height = (int)((bits >> 14) & 0x3FFF) + 1;
            return true;
        }
        return false;
    }
}
