using System.Collections.Concurrent;
using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Application;

public sealed class ConversionProgressRegistry(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<Guid, ConversionProgressSnapshot> _progress = new();

    public ConversionProgressSnapshot Report(Guid jobId, ConversionProgressUpdate update)
    {
        var percent = Math.Clamp(update.Percent, 0, 100);
        return _progress.AddOrUpdate(
            jobId,
            _ => new ConversionProgressSnapshot(percent, update.Stage, update.Message, timeProvider.GetUtcNow()),
            (_, current) => new ConversionProgressSnapshot(
                Math.Max(current.Percent, percent), update.Stage, update.Message, timeProvider.GetUtcNow()));
    }

    public ConversionProgressSnapshot Get(ConversionJob job)
    {
        if (_progress.TryGetValue(job.Id, out var current)) return current;
        var (percent, stage, message) = job.State switch
        {
            ConversionJobState.Queued => (0, ConversionProgressStage.Queued, "等待执行"),
            ConversionJobState.Running => (5, ConversionProgressStage.Preparing, "正在恢复转换进度"),
            ConversionJobState.Validating => (90, ConversionProgressStage.Validating, "正在校验输出"),
            ConversionJobState.Succeeded => (100, ConversionProgressStage.Completed, "转换完成"),
            ConversionJobState.Failed => (0, ConversionProgressStage.Failed, job.Diagnostic),
            ConversionJobState.Canceled => (0, ConversionProgressStage.Canceled, job.Diagnostic),
            _ => (0, ConversionProgressStage.Queued, null)
        };
        return new ConversionProgressSnapshot(percent, stage, message, job.UpdatedAt);
    }

    public ConversionProgressSnapshot MarkTerminal(
        ConversionJob job,
        ConversionProgressStage stage,
        string? message)
    {
        var current = Get(job);
        var percent = stage == ConversionProgressStage.Completed ? 100 : current.Percent;
        return Report(job.Id, new ConversionProgressUpdate(percent, stage, message));
    }

    // [2026-09-09 Issue01 终态清理] 作业状态文件被 TTL 清理后同步逐出内存进度，避免残留。
    public void Remove(Guid jobId) => _progress.TryRemove(jobId, out _);
}
