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

    // 结果读取授权来自“成功作业 + 该作业已发布输出根”，不暴露整个 OutputRoot。
    private static async Task<IResult> GetResultAsync(
        Guid id, string? path, ConversionJobService service, ConversionOptions options, CancellationToken cancellationToken)
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
        return Results.File(filePath, ResultContentType(filePath));
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
}
