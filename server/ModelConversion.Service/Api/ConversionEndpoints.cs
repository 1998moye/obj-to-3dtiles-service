using Microsoft.AspNetCore.Http.Features;
using ModelConversion.Service.Application;
using ModelConversion.Service.Configuration;
using ModelConversion.Service.Domain;
using ModelConversion.Service.Infrastructure;

namespace ModelConversion.Service.Api;

public static class ConversionEndpoints
{
    public static IEndpointRouteBuilder MapConversionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/conversions");
        group.MapPost("/", CreateAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapGet("/{id:guid}/status", GetStatusAsync);
        group.MapGet("/{id:guid}/progress", GetProgressAsync);
        group.MapPost("/{id:guid}/cancel", CancelAsync);
        group.MapPost("/{id:guid}/retry", RetryAsync);
        group.MapGet("/{id:guid}/result", GetResultManifestAsync);
        group.MapGet("/{id:guid}/result.zip", DownloadResultArchiveAsync);
        group.MapGet("/{id:guid}/result/{**path}", GetResultAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        CreateConversionRequest request,
        ConversionJobService service,
        ConversionProgressRegistry progressRegistry,
        CancellationToken cancellationToken)
    {
        try
        {
            var job = await service.CreateAsync(request, cancellationToken);
            return Results.Accepted($"/api/v1/conversions/{job.Id}",
                ConversionJobResponse.FromJob(job, progressRegistry.Get(job)));
        }
        catch (OutputConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FileNotFoundException or UnauthorizedAccessException or KeyNotFoundException)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> ListAsync(
        ConversionJobService service,
        ConversionProgressRegistry progressRegistry,
        CancellationToken cancellationToken) =>
        Results.Ok((await service.ListAsync(cancellationToken))
            .Select(job => ConversionJobResponse.FromJob(job, progressRegistry.Get(job))));

    private static async Task<IResult> GetAsync(
        Guid id,
        ConversionJobService service,
        ConversionProgressRegistry progressRegistry,
        CancellationToken cancellationToken)
    {
        var job = await service.GetAsync(id, cancellationToken);
        return job == null
            ? Results.NotFound()
            : Results.Ok(ConversionJobResponse.FromJob(job, progressRegistry.Get(job)));
    }

    private static async Task<IResult> GetStatusAsync(
        Guid id,
        ConversionJobService service,
        ConversionProgressRegistry progressRegistry,
        CancellationToken cancellationToken)
    {
        var job = await service.GetAsync(id, cancellationToken);
        if (job == null) return Results.NotFound();
        var progress = progressRegistry.Get(job);
        return Results.Ok(new ConversionStatusResponse(
            job.Id, job.State, !job.IsTerminal, job.Diagnostic, job.UpdatedAt, progress));
    }

    private static async Task<IResult> GetProgressAsync(
        Guid id,
        ConversionJobService service,
        ConversionProgressRegistry progressRegistry,
        CancellationToken cancellationToken)
    {
        var job = await service.GetAsync(id, cancellationToken);
        if (job == null) return Results.NotFound();
        var progress = progressRegistry.Get(job);
        return Results.Ok(new ConversionProgressResponse(
            job.Id, job.State, progress.Percent, progress.Stage, progress.Message, progress.UpdatedAt));
    }

    private static async Task<IResult> CancelAsync(
        Guid id,
        ConversionJobService service,
        ConversionProgressRegistry progressRegistry,
        CancellationToken cancellationToken)
    {
        try
        {
            var job = await service.CancelAsync(id, cancellationToken);
            return job == null
                ? Results.NotFound()
                : Results.Accepted($"/api/v1/conversions/{id}",
                    ConversionJobResponse.FromJob(job, progressRegistry.Get(job)));
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    }

    private static async Task<IResult> RetryAsync(
        Guid id,
        ConversionJobService service,
        ConversionProgressRegistry progressRegistry,
        CancellationToken cancellationToken)
    {
        try
        {
            var job = await service.RetryAsync(id, cancellationToken);
            return Results.Accepted($"/api/v1/conversions/{job.Id}",
                ConversionJobResponse.FromJob(job, progressRegistry.Get(job)));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
    }

    private static async Task<IResult> GetResultManifestAsync(
        Guid id,
        ConversionResultService resultService,
        ResultReadLeaseRegistry leaseRegistry,
        CancellationToken cancellationToken)
    {
        var snapshot = await resultService.GetAsync(id, cancellationToken);
        if (snapshot == null) return Results.NotFound();

        return new LeasedResult(id, leaseRegistry, Results.Ok(new
        {
            conversionId = id,
            fileCount = snapshot.Files.Count,
            totalBytes = snapshot.TotalBytes,
            archiveUrl = $"/api/v1/conversions/{id}/result.zip",
            files = snapshot.Files.Select(file => new
            {
                path = file.Path,
                bytes = file.Bytes,
                // [2026-09-09 Issue01 结果清单] SHA-256 来自成功前持久化的清单；旧数据回退时可能为 null
                sha256 = file.Sha256,
                url = $"/api/v1/conversions/{id}/result/{EncodeRelativeUrl(file.Path)}"
            })
        }));
    }

    private static async Task<IResult> DownloadResultArchiveAsync(
        Guid id,
        ConversionResultService resultService,
        ResultReadLeaseRegistry leaseRegistry,
        CancellationToken cancellationToken)
    {
        var snapshot = await resultService.GetAsync(id, cancellationToken);
        return snapshot == null
            ? Results.NotFound()
            : new ConversionResultArchive(id, snapshot, resultService, leaseRegistry);
    }

    // 结果读取授权来自“成功作业 + 该作业已发布输出根”，不暴露整个 OutputRoot。
    private static async Task<IResult> GetResultAsync(
        Guid id, string? path, ConversionJobService service, ConversionOptions options,
        ResultReadLeaseRegistry leaseRegistry, CancellationToken cancellationToken)
    {
        var job = await service.GetAsync(id, cancellationToken);
        if (job == null || job.State != ConversionJobState.Succeeded || string.IsNullOrEmpty(path))
            return Results.NotFound();

        var outputRoot = PathBoundary.ResolveOwnedPath(options.OutputRoot, job.OutputRelativePath);
        string filePath;
        try
        {
            filePath = PathBoundary.ResolveOwnedPath(outputRoot, path);
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException)
        {
            return Results.NotFound();
        }
        if (!File.Exists(filePath)) return Results.NotFound();
        // [2026-09-09 Issue01 Range 下载] 原代码: Results.File(filePath, contentType) 不支持 Range。
        // 原因: 平台按 manifest 断点续传需要 206/Content-Range；越界 Range 由框架稳定拒绝（416）。
        // 响应全程持有读租约，终态清理不得删除正在下载的作业产物。
        return new LeasedResult(id, leaseRegistry,
            Results.File(filePath, ResultContentType(filePath), enableRangeProcessing: true));
    }

    private static string ResultContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".b3dm" => "application/octet-stream",
        ".glb" => "model/gltf-binary",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".ktx2" => "image/ktx2",
        _ => "application/octet-stream"
    };

    private static string EncodeRelativeUrl(string path) => string.Join('/',
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private sealed class ConversionResultArchive(
        Guid conversionId,
        ConversionResultSnapshot snapshot,
        ConversionResultService resultService,
        ResultReadLeaseRegistry leaseRegistry) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            // [2026-09-08 结果目录拉取] ZipArchive 关闭时同步写少量中央目录元数据；文件正文仍全部异步流式传输。
            var bodyControl = httpContext.Features.Get<IHttpBodyControlFeature>();
            if (bodyControl != null) bodyControl.AllowSynchronousIO = true;
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = "application/zip";
            httpContext.Response.Headers.ContentDisposition =
                $"attachment; filename=conversion-{snapshot.ConversionId:N}.zip";
            // [2026-09-09 Issue01 结果读取租约] 整包下载可能持续很久，全程持有租约防止终态清理误删。
            using var lease = leaseRegistry.Acquire(conversionId);
            await resultService.WriteZipAsync(snapshot, httpContext.Response.Body, httpContext.RequestAborted);
        }
    }

    // [2026-09-09 Issue01 结果读取租约] 结果响应统一入口：进入时向 ResultReadLeaseRegistry 登记，
    // 响应结束（含 Range 分段下载）后释放；进程退出即终止在途响应，重启后无虚假租约。
    private sealed class LeasedResult(Guid jobId, ResultReadLeaseRegistry leaseRegistry, IResult inner) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            using var lease = leaseRegistry.Acquire(jobId);
            await inner.ExecuteAsync(httpContext);
        }
    }
}
