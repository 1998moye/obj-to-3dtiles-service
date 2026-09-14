using System.Text.Json;
using System.Text.Json.Serialization;
using ModelConversion.Service.Application;
using ModelConversion.Service.Configuration;
using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Infrastructure;

public sealed class FileConversionJobRepository : IConversionJobRepository
{
    private readonly string _jobsRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FileConversionJobRepository(ConversionOptions options)
    {
        _jobsRoot = Path.Combine(options.StateRoot, "jobs");
        Directory.CreateDirectory(_jobsRoot);
    }

    public async Task SaveAsync(ConversionJob job, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        var target = GetPath(job.Id);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(job, _jsonOptions);
            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            // [2026-09-04 状态持久化] 先写同目录临时文件再原子替换，避免断电留下半截 JSON。
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }

    public async Task<ConversionJob?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = GetPath(id);
            if (!File.Exists(path)) return null;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ConversionJob>(stream, _jsonOptions, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ConversionJob>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var jobs = new List<ConversionJob>();
            foreach (var path in Directory.EnumerateFiles(_jobsRoot, "*.json"))
            {
                await using var stream = File.OpenRead(path);
                var job = await JsonSerializer.DeserializeAsync<ConversionJob>(stream, _jsonOptions, cancellationToken);
                if (job != null) jobs.Add(job);
            }
            return jobs.OrderByDescending(job => job.CreatedAt).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    // [2026-09-09 Issue01 终态清理] 与 Save/Get 同闸门，删除不存在的文件视为成功（幂等重试）。
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = GetPath(id);
            if (File.Exists(path)) File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
        await Task.CompletedTask;
    }

    private string GetPath(Guid id) => Path.Combine(_jobsRoot, id.ToString("N") + ".json");
}
