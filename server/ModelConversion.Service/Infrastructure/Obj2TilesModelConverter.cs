using System.Diagnostics;
using ModelConversion.Service.Application;
using ModelConversion.Service.Configuration;

namespace ModelConversion.Service.Infrastructure;

public sealed class Obj2TilesModelConverter(
    Obj2TilesCommandBuilder commandBuilder,
    ConversionOptions options,
    ILogger<Obj2TilesModelConverter> logger) : IModelConverter
{
    public async Task<ModelConversionResult> ConvertAsync(ModelConversionContext context, CancellationToken cancellationToken)
    {
        var command = commandBuilder.Build(context.InputPath, context.StagingOutputPath, context.Settings, context.GeoReference);
        var startInfo = new ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(context.InputPath)!
        };
        foreach (var argument in command.Arguments) startInfo.ArgumentList.Add(argument);

        Directory.CreateDirectory(Path.GetDirectoryName(context.LogPath)!);
        await using var logStream = new FileStream(context.LogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(logStream) { AutoFlush = true };
        var writeGate = new object();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) => WriteLine(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => WriteLine(eventArgs.Data);

        if (!process.Start()) throw new InvalidOperationException("无法启动 Obj2Tiles 进程");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        logger.LogInformation("转换任务 {JobId} 已启动 Obj2Tiles，进程 {ProcessId}", context.JobId, process.Id);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(options.JobTimeoutMinutes));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            // [2026-09-04 进程取消] 只终止当前任务创建的完整进程树，避免残留编码子进程继续占用 CPU/内存。
            TryKillProcessTree(process);
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException($"转换超过 {options.JobTimeoutMinutes} 分钟，已终止");
            throw;
        }

        var diagnostic = process.ExitCode == 0 ? null : ReadTail(context.LogPath, 40);
        return new ModelConversionResult(process.ExitCode, diagnostic);

        void WriteLine(string? line)
        {
            if (line == null) return;
            lock (writeGate) writer.WriteLine(line);
            logger.LogInformation("[Obj2Tiles:{JobId}] {Line}", context.JobId, line);
            if (Obj2TilesProgressParser.TryParse(line, out var progress))
                context.Progress?.Invoke(progress);
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已并发退出，无需再次处理。
        }
    }

    private static string ReadTail(string path, int lines)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var tail = new Queue<string>(lines);
        while (reader.ReadLine() is { } line)
        {
            if (tail.Count == lines) tail.Dequeue();
            tail.Enqueue(line);
        }
        return string.Join(Environment.NewLine, tail);
    }
}
