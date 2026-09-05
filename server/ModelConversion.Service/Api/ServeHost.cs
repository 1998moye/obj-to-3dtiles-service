using System.Text.Json.Serialization;
using ModelConversion.Service.Application;

namespace ModelConversion.Service.Api;

public static class ServeHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        var app = Build(args);
        await app.RunAsync();
        return 0;
    }

    public static WebApplication Build(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        var options = builder.Services.AddConversionServices(builder.Configuration);

        Directory.CreateDirectory(options.InputRoot);
        Directory.CreateDirectory(options.OutputRoot);
        Directory.CreateDirectory(options.StateRoot);

        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            // 未知请求字段直接 400，避免静默忽略拼写错误的参数。
            json.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        });
        // 独立转换服务允许任意浏览器页面直接调用 API 和加载瓦片资源。
        builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        builder.Services.AddHostedService<ConversionWorker>();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseCors();
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", () =>
        {
            var ready = File.Exists(options.Obj2TilesExecutable)
                && Directory.Exists(options.InputRoot)
                && Directory.Exists(options.OutputRoot)
                && Directory.Exists(options.StateRoot);
            return ready
                ? Results.Ok(new { status = "ready" })
                : Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
        app.MapGet("/openapi/v1.json", () =>
            Results.File(Path.Combine(AppContext.BaseDirectory, "openapi.json"), "application/json"));
        app.MapConversionEndpoints();
        return app;
    }
}
