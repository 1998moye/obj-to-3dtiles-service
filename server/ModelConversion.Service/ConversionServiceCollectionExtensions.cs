using ModelConversion.Service.Application;
using ModelConversion.Service.Configuration;
using ModelConversion.Service.Infrastructure;

namespace ModelConversion.Service;

// serve 与 convert 共用同一套配置加载和依赖注入，避免两条入口行为漂移。
public static class ConversionServiceCollectionExtensions
{
    public static ConversionOptions AddConversionServices(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Conversion").Get<ConversionOptions>() ?? new ConversionOptions();
        options.EnsureValid();

        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConversionJobRepository, FileConversionJobRepository>();
        services.AddSingleton<ConversionJobQueue>();
        services.AddSingleton<RunningJobRegistry>();
        services.AddSingleton<ConversionProgressRegistry>();
        services.AddSingleton<ReferenceLlaReader>();
        services.AddSingleton<ConversionSettingsResolver>();
        services.AddSingleton<Obj2TilesCommandBuilder>();
        services.AddSingleton<ITilesetValidator, TilesetValidator>();
        services.AddSingleton<IModelConverter, Obj2TilesModelConverter>();
        services.AddSingleton<IConversionRunner, ConversionRunner>();
        services.AddSingleton<ConversionJobService>();
        return options;
    }
}
