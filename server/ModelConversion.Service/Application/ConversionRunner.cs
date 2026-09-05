using System.Diagnostics;
using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Application;

// 转换核心：唯一负责“一次转换从暂存到发布”的完整事务，CLI 与 HTTP Worker 都只是它的适配器。
public sealed class ConversionRunner(
    IModelConverter converter,
    ITilesetValidator validator,
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

        ResetStagingDirectory(request.StagingPath);
        try
        {
            ReportProgress(10, ConversionProgressStage.Converting, "Obj2Tiles 转换中");
            var conversion = await converter.ConvertAsync(
                new ModelConversionContext(request.RunId, request.InputPath, request.StagingPath,
                    request.LogPath, request.Settings, request.GeoReference,
                    update => ReportProgress(update.Percent, update.Stage, update.Message)),
                cancellationToken);
            if (conversion.ExitCode != 0)
            {
                ReportProgress(10, ConversionProgressStage.Failed, conversion.Diagnostic);
                return Fail(request, new ConversionRunResult(ConversionRunOutcome.ConversionFailed,
                    ExitCode: conversion.ExitCode,
                    Diagnostic: conversion.Diagnostic ?? $"Obj2Tiles 退出码为 {conversion.ExitCode}",
                    Duration: stopwatch.Elapsed));
            }

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
