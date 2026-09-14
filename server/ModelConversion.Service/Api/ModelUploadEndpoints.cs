using Microsoft.AspNetCore.Http.Features;
using ModelConversion.Service.Application;
using ModelConversion.Service.Configuration;

namespace ModelConversion.Service.Api;

public static class ModelUploadEndpoints
{
    public static IEndpointRouteBuilder MapModelUploadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/uploads", UploadAsync);
        return endpoints;
    }

    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        ModelUploadService service,
        ModelUploadOptions options,
        CancellationToken cancellationToken)
    {
        if (!IsZipContentType(request.ContentType))
            return Results.Json(
                new { error = "Content-Type 必须是 application/zip、application/x-zip-compressed 或 application/octet-stream" },
                statusCode: StatusCodes.Status415UnsupportedMediaType);

        // [2026-09-08 大模型上传] 在读取正文前同步 Kestrel 上限，正文内部仍由服务二次计数，兼容无 Content-Length 的分块上传。
        var bodySizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false }) bodySizeFeature.MaxRequestBodySize = options.MaxArchiveBytes;

        try
        {
            var result = await service.UploadAsync(request.Body, request.ContentLength, cancellationToken);
            return Results.Json(result, statusCode: StatusCodes.Status201Created);
        }
        catch (ModelUploadTooLargeException exception)
        {
            return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static bool IsZipContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        var mediaType = contentType.Split(';', 2)[0].Trim();
        return mediaType.Equals("application/zip", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/x-zip-compressed", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);
    }
}
