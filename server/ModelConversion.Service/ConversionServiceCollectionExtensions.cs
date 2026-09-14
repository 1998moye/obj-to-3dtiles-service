using ModelConversion.Service.Application;
using ModelConversion.Service.Configuration;
using ModelConversion.Service.Infrastructure;
using ModelConversion.Service.Infrastructure.Resources;

namespace ModelConversion.Service;

// serve 与 convert 共用同一套配置加载和依赖注入，避免两条入口行为漂移。
public static class ConversionServiceCollectionExtensions
{
    public static ConversionOptions AddConversionServices(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Conversion").Get<ConversionOptions>() ?? new ConversionOptions();
        options.EnsureValid();
        var uploadOptions = configuration.GetSection("Uploads").Get<ModelUploadOptions>() ?? new ModelUploadOptions();
        uploadOptions.EnsureValid();

        services.AddSingleton(options);
        services.AddSingleton(uploadOptions);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConversionJobRepository, FileConversionJobRepository>();
        services.AddSingleton<ConversionJobQueue>();
        services.AddSingleton<RunningJobRegistry>();
        services.AddSingleton<ConversionProgressRegistry>();
        services.AddSingleton<ReferenceLlaReader>();
        services.AddSingleton<ConversionSettingsResolver>();
        // [2026-09-08 模型包上传] 深模块隐藏流式落盘、ZIP 安全校验、唯一目录和原子发布。
        services.AddSingleton<ModelUploadService>();
        // [2026-09-09 Issue01 可恢复上传会话] ZIP 安全解压共享实现（单请求上传与会话合并共用）。
        services.AddSingleton<SafeModelPackageExtractor>();
        // [2026-09-09 Issue01 可恢复上传会话] 会话深模块 + 完成队列；后台完成器在 ServeHost 注册为 HostedService。
        services.AddSingleton<UploadSessionService>();
        services.AddSingleton<UploadSessionFinalizeQueue>();
        // [2026-09-09 Issue01 健康容量] 平台调度唯一容量事实来源。
        services.AddSingleton<ConverterHealthService>();
        // [2026-09-09 Issue01 结果清单与租约] 清单持久化（成功前生成、查询零重复哈希）与进程内读租约。
        services.AddSingleton<ConversionResultManifestService>();
        services.AddSingleton<ResultReadLeaseRegistry>();
        // [2026-09-08 结果目录拉取] 清单、文件和流式 ZIP 共用成功作业边界。
        services.AddSingleton<ConversionResultService>();
        services.AddSingleton<Obj2TilesCommandBuilder>();
        services.AddSingleton<ITilesetValidator, TilesetValidator>();
        // 资源感知转换：探测 → 预检 → 规划 → 规范化共用同一套实现，CLI 与 HTTP Worker 行为一致。
        services.AddSingleton<IRuntimeResourceProbe>(_ => new RuntimeResourceProbe());
        services.AddSingleton<InputModelInspector>();
        services.AddSingleton<ConversionResourcePlanner>();
        services.AddSingleton<Infrastructure.Textures.TextureNormalizationPipeline>();
        // [2026-09-07 深层封底剥离] 纹理规范化之后、转换之前运行；与规范化共用输入直通/副本语义
        services.AddSingleton<Infrastructure.Geometry.DeepBottomGeometryFilter>();
        services.AddSingleton<IModelConverter, Obj2TilesModelConverter>();
        services.AddSingleton<IConversionRunner, ConversionRunner>();
        services.AddSingleton<ConversionJobService>();
        return options;
    }
}
