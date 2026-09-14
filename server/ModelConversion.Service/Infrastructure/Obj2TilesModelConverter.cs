using System.Diagnostics;
using ModelConversion.Service.Application;
using ModelConversion.Service.Configuration;
using ModelConversion.Service.Domain;
using ModelConversion.Service.Infrastructure.Resources;

namespace ModelConversion.Service.Infrastructure;

public sealed class Obj2TilesModelConverter(
    Obj2TilesCommandBuilder commandBuilder,
    ConversionOptions options,
    IRuntimeResourceProbe resourceProbe,
    ILogger<Obj2TilesModelConverter> logger) : IModelConverter
{
    public async Task<ModelConversionResult> ConvertAsync(ModelConversionContext context, CancellationToken cancellationToken)
    {
        var command = commandBuilder.Build(context.InputPath, context.StagingOutputPath, context.Settings, context.GeoReference,
            context.ResourcePlan);
        var startInfo = new ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(context.InputPath)!
        };
        foreach (var argument in command.Arguments) startInfo.ArgumentList.Add(argument);

        // [2026-09-07 GC 堆预算] 按资源计划给 Obj2Tiles 受管堆设上限（十六进制字节数），
        // 让托管内存超配表现为可诊断的托管 OOM，而不是容器被整体杀死。GC 限制只是保护措施，
        // 超大纹理仍由规范化流水线控制（见 TextureNormalizationPipeline）。
        if (context.ResourcePlan is { } plan)
            startInfo.Environment["DOTNET_GCHeapHardLimit"] = "0x" + plan.GcHeapBudgetBytes.ToString("X");

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

        // [2026-09-07 内存看门狗] 周期采样容器内存用量，连续超过硬水位则主动终止进程树，
        // 避免容器被 OOM Killer 无提示杀死。采样失败时不误杀。
        // 仅容器限制口径下启用：进程回退口径的用量是整机值，本服务无法控制自己的“用量”，
        // 启用看门狗会因机器上其他进程占用内存而误杀健康转换。
        var protectionTriggered = false;
        long? sampledPeakBytes = null;
        ConversionProgressStage? lastStage = null;
        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        var watchdog = context.ResourcePlan is not { ContainerLimited: true }
            ? Task.CompletedTask
            : Task.Run(() => WatchMemoryAsync(process, context.ResourcePlan, watchdogCts.Token), CancellationToken.None);

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
        finally
        {
            watchdogCts.Cancel();
            try { await watchdog; } catch (OperationCanceledException) { }
        }

        if (protectionTriggered)
            throw new MemoryProtectionException(
                $"容器内存连续超过硬水位 {ConversionResourceReport.FormatBytes(context.ResourcePlan!.HardWatermarkBytes)}，已主动终止转换进程");

        var diagnostic = process.ExitCode == 0 ? null : ReadTail(context.LogPath, 40);

        // [2026-09-07 疑似 OOM 结构化诊断] 退出码 137=128+SIGKILL，在容器内几乎总是
        // OOM Killer 所为（看门狗主动终止走的是上面的 MemoryProtectionException 路径）。
        // 诊断附带容器限制、看门狗采样峰值与终止前阶段，便于区分"规格不足"与"实现泄漏"。
        if (process.ExitCode == 137)
        {
            var resourcePlan = context.ResourcePlan;
            var oomDiagnostic =
                $"Obj2Tiles 进程被强制终止（退出码 137=SIGKILL，疑似容器 OOMKilled）：" +
                $"内存上限={(resourcePlan != null ? ConversionResourceReport.FormatBytes(resourcePlan.MemoryLimitBytes) : "未知")}" +
                $"({(resourcePlan?.ContainerLimited == true ? "容器限制" : "进程可见内存")})，" +
                $"看门狗采样峰值={(sampledPeakBytes.HasValue ? ConversionResourceReport.FormatBytes(sampledPeakBytes.Value) : "未采样")}，" +
                $"终止前阶段={lastStage?.ToString() ?? "未知"}。" +
                $"建议：提高容器内存限制，或降低输入模型/纹理规格后重试。";
            logger.LogWarning("转换任务 {JobId} {Diagnostic}", context.JobId, oomDiagnostic);
            return new ModelConversionResult(process.ExitCode,
                diagnostic != null ? oomDiagnostic + Environment.NewLine + "日志尾部: " + Environment.NewLine + diagnostic : oomDiagnostic)
            {
                SuspectedOom = true,
                SampledPeakMemoryBytes = sampledPeakBytes,
                LastStage = lastStage
            };
        }

        if (sampledPeakBytes.HasValue)
            logger.LogInformation("转换任务 {JobId} 容器内存采样峰值 {Peak}", context.JobId,
                ConversionResourceReport.FormatBytes(sampledPeakBytes.Value));
        return new ModelConversionResult(process.ExitCode, diagnostic)
        {
            SampledPeakMemoryBytes = sampledPeakBytes,
            LastStage = lastStage
        };

        void WriteLine(string? line)
        {
            if (line == null) return;
            lock (writeGate) writer.WriteLine(line);
            logger.LogInformation("[Obj2Tiles:{JobId}] {Line}", context.JobId, line);
            if (Obj2TilesProgressParser.TryParse(line, out var progress))
            {
                lastStage = progress.Stage;
                context.Progress?.Invoke(progress);
            }
        }

        async Task WatchMemoryAsync(Process watched, ConversionResourcePlan plan, CancellationToken stopToken)
        {
            var interval = TimeSpan.FromSeconds(options.Resources.MemoryWatchdogIntervalSeconds);
            var consecutiveBreaches = 0;
            while (!stopToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, stopToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (watched.HasExited) return;

                var current = resourceProbe.TryReadCurrentMemoryBytes();
                if (current == null)
                {
                    consecutiveBreaches = 0;
                    continue;
                }
                // 记录采样峰值：用于 137 疑似 OOM 诊断与转换报告的回归指标。
                if (current.Value > (sampledPeakBytes ?? 0)) sampledPeakBytes = current.Value;
                if (current.Value > plan.HardWatermarkBytes)
                {
                    // 连续两次采样越线才动手，避免启动瞬时尖峰误杀。
                    consecutiveBreaches++;
                    if (consecutiveBreaches < 2) continue;
                    protectionTriggered = true;
                    logger.LogWarning(
                        "转换任务 {JobId} 内存用量 {Current} 连续超过硬水位 {HardWatermark}，主动终止转换进程",
                        context.JobId,
                        ConversionResourceReport.FormatBytes(current.Value),
                        ConversionResourceReport.FormatBytes(plan.HardWatermarkBytes));
                    TryKillProcessTree(watched);
                    return;
                }
                consecutiveBreaches = 0;
            }
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
