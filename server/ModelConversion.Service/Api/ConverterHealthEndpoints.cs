using ModelConversion.Service.Application;

namespace ModelConversion.Service.Api;

// [2026-09-09 Issue01 健康容量] PRD §12 固定契约的薄端点；聚合逻辑全部在 ConverterHealthService。
public static class ConverterHealthEndpoints
{
    public static IEndpointRouteBuilder MapConverterHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/health", GetAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        ConverterHealthService service,
        CancellationToken cancellationToken)
    {
        var snapshot = await service.GetAsync(cancellationToken);
        return Results.Ok(new
        {
            status = snapshot.Status,
            queueDepth = snapshot.QueueDepth,
            runningJobs = snapshot.RunningJobs,
            maxConcurrentJobs = snapshot.MaxConcurrentJobs,
            roots = new
            {
                inputReady = snapshot.InputReady,
                outputReady = snapshot.OutputReady,
                stateReady = snapshot.StateReady
            },
            storage = new
            {
                freeBytes = snapshot.StorageFreeBytes,
                reserveBytes = snapshot.StorageReserveBytes
            },
            resourcePressure = snapshot.ResourcePressure,
            updatedAt = snapshot.UpdatedAt
        });
    }
}
