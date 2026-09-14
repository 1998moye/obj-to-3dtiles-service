using Microsoft.AspNetCore.Http.Features;
using ModelConversion.Service.Application;
using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Api;

// [2026-09-09 Issue01 可恢复上传会话] PRD §12 固定契约的薄端点：只做 HTTP 映射，
// 幂等去重、分片校验、状态机与合并全部在 UploadSessionService / UploadSessionFinalizer。
// 内部错误体固定为 {errorCode, message}，不携带宿主绝对路径。
public static class UploadSessionEndpoints
{
    public static IEndpointRouteBuilder MapUploadSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/upload-sessions");
        group.MapPost("/", CreateAsync);
        group.MapGet("/{uploadId:guid}", GetAsync);
        group.MapPut("/{uploadId:guid}/parts/{partNumber:int}", UploadPartAsync);
        group.MapPost("/{uploadId:guid}/complete", CompleteAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        CreateUploadSessionRequest request,
        UploadSessionService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var session = await service.CreateAsync(request, cancellationToken);
            return Results.Json(ToResponse(session), statusCode: StatusCodes.Status201Created);
        }
        catch (UploadSessionException exception)
        {
            return Error(exception);
        }
    }

    private static async Task<IResult> GetAsync(
        Guid uploadId,
        UploadSessionService service,
        CancellationToken cancellationToken)
    {
        var session = await service.GetAsync(uploadId, cancellationToken);
        // [2026-09-09 Issue01 会话查询] 不存在或过期统一 404，不区分原因（不泄露会话存在性）。
        return session == null || session.State == UploadSessionState.Expired
            ? Results.NotFound()
            : Results.Ok(ToResponse(session));
    }

    private static async Task<IResult> UploadPartAsync(
        Guid uploadId,
        int partNumber,
        HttpRequest request,
        UploadSessionService service,
        CancellationToken cancellationToken)
    {
        // [2026-09-09 Issue01 分片上传] 同步 Kestrel 正文上限到固定分片大小（64MiB + 余量），
        // 正文内部仍由服务二次计数与摘要校验。
        var bodySizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
            bodySizeFeature.MaxRequestBodySize = UploadSessionService.PartSizeBytes + 1024 * 1024;

        try
        {
            var result = await service.SavePartAsync(
                uploadId,
                partNumber,
                request.Body,
                request.ContentLength,
                request.Headers["X-Part-Sha256"].FirstOrDefault(),
                cancellationToken);
            return Results.Ok(new { partNumber = result.PartNumber, sizeBytes = result.SizeBytes, sha256 = result.Sha256 });
        }
        catch (UploadSessionNotFoundException)
        {
            return Results.NotFound();
        }
        catch (UploadSessionException exception)
        {
            return Error(exception);
        }
    }

    private static async Task<IResult> CompleteAsync(
        Guid uploadId,
        UploadSessionService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var session = await service.RequestCompleteAsync(uploadId, cancellationToken);
            return Results.Json(ToResponse(session), statusCode: StatusCodes.Status202Accepted);
        }
        catch (UploadSessionNotFoundException)
        {
            return Results.NotFound();
        }
        catch (UploadSessionException exception)
        {
            return Error(exception);
        }
    }

    private static IResult Error(UploadSessionException exception) =>
        Results.Json(
            new { errorCode = exception.ErrorCode, message = exception.Message },
            statusCode: exception.ErrorCode == UploadSessionErrorCodes.Conflict
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest);

    private static object ToResponse(UploadSession session) => new
    {
        uploadId = session.Id,
        state = session.State switch
        {
            UploadSessionState.Uploading => "uploading",
            UploadSessionState.Finalizing => "finalizing",
            UploadSessionState.Completed => "completed",
            UploadSessionState.Failed => "failed",
            _ => "expired"
        },
        partSizeBytes = session.PartSizeBytes,
        partCount = session.PartCount,
        completedParts = session.CompletedParts.OrderBy(number => number).ToArray(),
        expiresAt = session.ExpiresAt,
        inputPath = session.State == UploadSessionState.Completed ? session.InputPath : null,
        referenceLlaPath = session.State == UploadSessionState.Completed ? session.ReferenceLlaPath : null,
        errorCode = session.ErrorCode
    };
}
