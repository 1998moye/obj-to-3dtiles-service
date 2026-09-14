using System.Diagnostics;
using ModelConversion.Service.Domain;
using ModelConversion.Service.Infrastructure.Geometry;
using ModelConversion.Service.Infrastructure.Resources;
using ModelConversion.Service.Infrastructure.Textures;

namespace ModelConversion.Service.Application;

// 转换核心：唯一负责“一次转换从暂存到发布”的完整事务，CLI 与 HTTP Worker 都只是它的适配器。
// 资源预检也在此统一执行：任何入口都先经过同一套资源决策再进入重型转换。
public sealed class ConversionRunner(
    IModelConverter converter,
    ITilesetValidator validator,
    IRuntimeResourceProbe resourceProbe,
    InputModelInspector inputInspector,
    ConversionResourcePlanner resourcePlanner,
    TextureNormalizationPipeline textureNormalization,
    DeepBottomGeometryFilter deepBottomFilter,
    ILogger<ConversionRunner> logger) : IConversionRunner
{
    public async Task<ConversionRunResult> RunAsync(ConversionRunRequest request, CancellationToken cancellationToken)
    {
        if (!File.Exists(request.InputPath))
            throw new FileNotFoundException("输入文件不存在", request.InputPath);

        var stopwatch = Stopwatch.StartNew();
        var reportedPercent = 0;
        void ReportProgress(int percent, ConversionProgressStage stage, string? message)
        {
            reportedPercent = Math.Max(reportedPercent, percent);
            request.Progress?.Invoke(new ConversionProgressUpdate(reportedPercent, stage, message));
        }

        ReportProgress(2, ConversionProgressStage.Preparing, "准备转换目录");
        if (Directory.Exists(request.FinalOutputPath))
        {
            if (!request.RevalidateExistingOutput)
                return new ConversionRunResult(ConversionRunOutcome.OutputConflict,
                    Diagnostic: "最终输出目录已存在，拒绝覆盖", Duration: stopwatch.Elapsed);

            // 重启恢复场景：输出已发布但成功状态没落盘，复核通过后视为成功，不重复转换。
            try
            {
                ReportProgress(90, ConversionProgressStage.Validating, "复核已发布输出");
                var existing = await validator.ValidateAsync(request.FinalOutputPath, cancellationToken);
                logger.LogInformation("转换运行 {RunId} 检测到已发布输出，复核通过，跳过重复转换", request.RunId);
                ReportProgress(100, ConversionProgressStage.Completed, "已发布输出复核通过");
                return new ConversionRunResult(ConversionRunOutcome.Succeeded,
                    Validation: existing, ReusedExistingOutput: true, Duration: stopwatch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                ReportProgress(90, ConversionProgressStage.Canceled, "任务已取消");
                return new ConversionRunResult(ConversionRunOutcome.Canceled,
                    Diagnostic: "任务已取消", Duration: stopwatch.Elapsed);
            }
            catch (Exception exception)
            {
                ReportProgress(90, ConversionProgressStage.Failed, exception.Message);
                return new ConversionRunResult(ConversionRunOutcome.ValidationFailed,
                    Diagnostic: exception.Message, Duration: stopwatch.Elapsed);
            }
        }

        // 资源预检：在启动任何重型工作前固化资源快照并生成资源计划或结构化拒绝。
        ReportProgress(4, ConversionProgressStage.Preparing, "资源预检");
        ConversionResourceReport resourceReport;
        InputModelSummary inputSummary;
        try
        {
            var snapshot = resourceProbe.Snapshot(request.StagingPath);
            inputSummary = await inputInspector.InspectAsync(request.InputPath, cancellationToken);
            resourceReport = resourcePlanner.Plan(request, inputSummary, snapshot);
        }
        catch (OperationCanceledException)
        {
            ReportProgress(4, ConversionProgressStage.Canceled, "任务已取消");
            return new ConversionRunResult(ConversionRunOutcome.Canceled,
                Diagnostic: "任务已取消", Duration: stopwatch.Elapsed);
        }
        LogResourceReport(request, resourceReport);
        if (resourceReport.IsRejected)
        {
            var rejection = resourceReport.Rejection!;
            var outcome = rejection.Kind == ConversionResourceRejectionKind.InsufficientTemporaryDisk
                ? ConversionRunOutcome.InsufficientDisk
                : ConversionRunOutcome.ResourceRejected;
            ReportProgress(4, ConversionProgressStage.Failed, rejection.Reason);
            logger.LogWarning("转换运行 {RunId} 资源预检拒绝: {Reason}", request.RunId, rejection.Reason);
            return new ConversionRunResult(outcome,
                Diagnostic: $"[{rejection.Kind}] {rejection.Reason}", Duration: stopwatch.Elapsed);
        }

        var plan = resourceReport.Plan!;
        ReportProgress(6, ConversionProgressStage.Preparing,
            $"资源计划: {plan.Mode}，内存上限 {ConversionResourceReport.FormatBytes(plan.MemoryLimitBytes)}");

        ResetStagingDirectory(request.StagingPath);
        try
        {
            // [2026-09-07 纹理规范化] 超大源纹理在任务专属副本中降到计划边长上限，
            // 源 OBJ/MTL/纹理全程只读；直通模式下不创建任何副本。
            ReportProgress(8, ConversionProgressStage.Preparing, "纹理规范化");
            PreparedConversionInput preparedInput;
            try
            {
                preparedInput = await textureNormalization.PrepareAsync(request, inputSummary, plan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 规范化失败（IO/解码）映射为普通转换失败并保留诊断，不引入新结果分类。
                ReportProgress(8, ConversionProgressStage.Failed, exception.Message);
                return Fail(request, new ConversionRunResult(ConversionRunOutcome.ConversionFailed,
                    Diagnostic: $"纹理规范化失败: {exception.Message}", Duration: stopwatch.Elapsed));
            }

            // [2026-09-07 深层封底剥离] 纹理规范化之后、Obj2Tiles 之前剥离深层封洞/封底面片，
            // 避免灰色块进入产物并修复高度回正被深层几何带偏；直通时零拷贝、不建临时目录。
            ReportProgress(9, ConversionProgressStage.Preparing, "深层封底剥离");
            PreparedConversionInput filteredInput;
            try
            {
                filteredInput = await deepBottomFilter.FilterAsync(request, preparedInput.InputPath, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 与纹理规范化同口径：IO/解析失败映射为普通转换失败并保留诊断，不引入新结果分类。
                ReportProgress(9, ConversionProgressStage.Failed, exception.Message);
                return Fail(request, new ConversionRunResult(ConversionRunOutcome.ConversionFailed,
                    Diagnostic: $"深层封底剥离失败: {exception.Message}", Duration: stopwatch.Elapsed));
            }

            ReportProgress(10, ConversionProgressStage.Converting, "Obj2Tiles 转换中");
            var conversion = await converter.ConvertAsync(
                new ModelConversionContext(request.RunId, filteredInput.InputPath, request.StagingPath,
                    request.LogPath, request.Settings, request.GeoReference,
                    update => ReportProgress(update.Percent, update.Stage, update.Message),
                    plan),
                cancellationToken);
            if (conversion.SuspectedOom || conversion.ExitCode == 137)
            {
                // [2026-09-07 疑似 OOM] 137=SIGKILL 单独成类：与准入拒绝（未启动）、
                // 主动保护（看门狗拦截）和普通失败区分开，指导运维扩容而非排查算法。
                ReportProgress(10, ConversionProgressStage.Failed, conversion.Diagnostic);
                logger.LogWarning("转换运行 {RunId} 疑似 OOMKilled: {Diagnostic}", request.RunId, conversion.Diagnostic);
                return Fail(request, new ConversionRunResult(ConversionRunOutcome.SuspectedOomKilled,
                    ExitCode: conversion.ExitCode,
                    Diagnostic: conversion.Diagnostic ?? "Obj2Tiles 疑似被 OOM Killer 终止（退出码 137）",
                    Duration: stopwatch.Elapsed));
            }
            if (conversion.ExitCode != 0)
            {
                ReportProgress(10, ConversionProgressStage.Failed, conversion.Diagnostic);
                return Fail(request, new ConversionRunResult(ConversionRunOutcome.ConversionFailed,
                    ExitCode: conversion.ExitCode,
                    Diagnostic: conversion.Diagnostic ?? $"Obj2Tiles 退出码为 {conversion.ExitCode}",
                    Duration: stopwatch.Elapsed));
            }

            // 转换成功后、校验与发布前删除规范化输入副本：暂存目录整体原子发布，
            // 输入副本不能混入输出；其余路径由暂存清理兜底。
            filteredInput.Dispose();
            preparedInput.Dispose();

            TilesetValidationReport report;
            try
            {
                ReportProgress(88, ConversionProgressStage.Validating, "校验 3D Tiles 输出");
                report = await validator.ValidateAsync(request.StagingPath, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                ReportProgress(88, ConversionProgressStage.Failed, exception.Message);
                return Fail(request, new ConversionRunResult(ConversionRunOutcome.ValidationFailed,
                    Diagnostic: exception.Message, Duration: stopwatch.Elapsed));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(request.FinalOutputPath)!);
            if (Directory.Exists(request.FinalOutputPath))
                return Fail(request, new ConversionRunResult(ConversionRunOutcome.OutputConflict,
                    Diagnostic: "最终输出目录已存在，拒绝覆盖", Duration: stopwatch.Elapsed));

            // [2026-09-04 原子发布] 校验通过后才把同盘暂存目录移动为最终目录，客户端不会看到半成品瓦片集。
            ReportProgress(96, ConversionProgressStage.Publishing, "发布最终输出");
            PublishOutput(request.StagingPath, request.FinalOutputPath);
            logger.LogInformation("转换运行 {RunId} 完成，输出已发布到 {FinalOutputPath}", request.RunId, request.FinalOutputPath);
            ReportProgress(100, ConversionProgressStage.Completed, "转换完成");
            return new ConversionRunResult(ConversionRunOutcome.Succeeded,
                ExitCode: 0, Validation: report, Duration: stopwatch.Elapsed);
        }
        catch (TimeoutException exception)
        {
            ReportProgress(10, ConversionProgressStage.Failed, exception.Message);
            return Fail(request, new ConversionRunResult(ConversionRunOutcome.TimedOut,
                Diagnostic: exception.Message, Duration: stopwatch.Elapsed));
        }
        catch (MemoryProtectionException exception)
        {
            // [2026-09-07 主动内存保护] 达到硬水位时由看门狗主动终止转换进程，
            // 映射为资源类失败而不是普通转换失败，避免被误判为算法问题。
            ReportProgress(10, ConversionProgressStage.Failed, exception.Message);
            logger.LogWarning("转换运行 {RunId} 触发主动内存保护: {Message}", request.RunId, exception.Message);
            return Fail(request, new ConversionRunResult(ConversionRunOutcome.ResourceRejected,
                Diagnostic: $"[MemoryProtection] {exception.Message}", Duration: stopwatch.Elapsed));
        }
        catch (OperationCanceledException)
        {
            ResetStagingDirectory(request.StagingPath, recreate: false);
            ReportProgress(10, ConversionProgressStage.Canceled, "任务已取消");
            return new ConversionRunResult(ConversionRunOutcome.Canceled,
                Diagnostic: "任务已取消", Duration: stopwatch.Elapsed);
        }
        catch
        {
            ResetStagingDirectory(request.StagingPath, recreate: false);
            throw;
        }

        ConversionRunResult Fail(ConversionRunRequest failedRequest, ConversionRunResult result)
        {
            ResetStagingDirectory(failedRequest.StagingPath, recreate: false);
            return result;
        }
    }

    private static void ResetStagingDirectory(string path, bool recreate = true)
    {
        // [2026-09-04 暂存清理] path 由适配层按运行标识在状态目录下生成，不接受调用方任意路径。
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        if (recreate) Directory.CreateDirectory(path);
    }

    // 资源报告同时进入服务控制台与任务日志文件，解释任务为什么被执行或拒绝。
    private void LogResourceReport(ConversionRunRequest request, ConversionResourceReport report)
    {
        foreach (var line in report.ToDisplayString().Split(Environment.NewLine))
            logger.LogInformation("转换运行 {RunId} 资源预检 {Line}", request.RunId, line);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.LogPath)!);
            File.AppendAllText(request.LogPath,
                $"[{DateTimeOffset.UtcNow:u}] 资源预检报告{Environment.NewLine}" +
                report.ToDisplayString() + Environment.NewLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 报告落盘失败不阻断转换；控制台日志已保留同样内容。
            logger.LogWarning("转换运行 {RunId} 资源报告写入任务日志失败: {Message}", request.RunId, exception.Message);
        }
    }

    private static void PublishOutput(string stagingPath, string finalOutputPath)
    {
        try
        {
            Directory.Move(stagingPath, finalOutputPath);
        }
        catch (IOException)
        {
            // Linux 上 StateRoot 与 OutputRoot 分属不同挂载点时 rename 报 EXDEV，退化为复制后删除。
            CopyDirectory(stagingPath, finalOutputPath);
            Directory.Delete(stagingPath, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
    }
}
