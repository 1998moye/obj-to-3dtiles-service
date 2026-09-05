using ModelConversion.Service.Application;
using ModelConversion.Service.Domain;
using ModelConversion.Service.Infrastructure;

namespace ModelConversion.Service.Cli;

// CLI convert 子命令需要的转换核心依赖；测试用 fake 替换，不复制转换流程。
public sealed record ConversionCliServices(
    IConversionRunner Runner,
    ConversionSettingsResolver Resolver,
    ReferenceLlaReader Reader);

public static class ConvertCommand
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        ConversionCliServices? servicesOverride = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;

        var parsed = ConvertCommandLine.Parse(args);
        if (parsed.ShowHelp)
        {
            CliUsage.PrintConvert(stdout);
            return CliExitCodes.Success;
        }
        if (parsed.Command == null)
        {
            stderr.WriteLine($"参数错误: {parsed.Error}");
            stderr.WriteLine("运行 convert --help 查看用法。");
            return CliExitCodes.UsageError;
        }
        var command = parsed.Command;

        string input;
        try
        {
            input = ResolveInput(command.Input!);
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException or IOException)
        {
            stderr.WriteLine($"输入错误: {exception.Message}");
            return CliExitCodes.UsageError;
        }

        var output = Path.GetFullPath(command.Output!);
        using var host = servicesOverride == null ? BuildHost() : null;
        try
        {
            var services = servicesOverride ?? new ConversionCliServices(
                host!.Services.GetRequiredService<IConversionRunner>(),
                host.Services.GetRequiredService<ConversionSettingsResolver>(),
                host.Services.GetRequiredService<ReferenceLlaReader>());

            GeoReference? geoReference;
            try
            {
                geoReference = await ResolveGeoReferenceAsync(command, services.Reader, cancellationToken);
                geoReference?.EnsureValid();
            }
            catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException
                or FileNotFoundException or InvalidDataException or IOException or System.Text.Json.JsonException)
            {
                stderr.WriteLine($"参数错误: {exception.Message}");
                return CliExitCodes.UsageError;
            }

            ConversionSettings settings;
            try
            {
                settings = services.Resolver.Resolve(command.ProfileName, command.ToOverrides());
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
            {
                stderr.WriteLine($"参数错误: {exception.Message}");
                return CliExitCodes.UsageError;
            }

            if (!settings.Local && geoReference == null)
            {
                stderr.WriteLine("参数错误: 地理参考转换必须提供 --reference-lla 或 --lat/--lon/--alt，或使用 --local");
                return CliExitCodes.UsageError;
            }

            var runId = Guid.NewGuid();
            // 暂存与最终输出同盘同目录，保证 Directory.Move 原子发布成立。
            var staging = output + ".staging-" + runId.ToString("N");
            var logPath = output + ".log";

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ConsoleCancelEventHandler? cancelHandler = null;
            if (servicesOverride == null)
            {
                var cancelRequested = false;
                cancelHandler = (_, eventArgs) =>
                {
                    if (cancelRequested) return; // 第二次 Ctrl+C 直接终止进程
                    cancelRequested = true;
                    eventArgs.Cancel = true;
                    stderr.WriteLine("收到取消请求，正在停止转换...");
                    cancellation.Cancel();
                };
                Console.CancelKeyPress += cancelHandler;
            }

            try
            {
                stdout.WriteLine($"开始转换: {input}");
                stdout.WriteLine($"输出目录: {output}（profile: {command.ProfileName}）");
                var result = await services.Runner.RunAsync(
                    new ConversionRunRequest(runId, input, output, staging, logPath,
                        settings, geoReference, RevalidateExistingOutput: false,
                        Progress: update => stdout.WriteLine($"[{update.Percent,3}%] {update.Stage}: {update.Message}")),
                    cancellation.Token);
                return MapOutcome(result, output, logPath, stdout, stderr);
            }
            catch (Exception exception)
            {
                stderr.WriteLine($"转换失败: {exception.Message}");
                stderr.WriteLine($"详细日志: {logPath}");
                return CliExitCodes.ConversionFailed;
            }
            finally
            {
                if (cancelHandler != null) Console.CancelKeyPress -= cancelHandler;
            }
        }
        finally
        {
            host?.Dispose();
        }
    }

    private static int MapOutcome(
        ConversionRunResult result, string output, string logPath, TextWriter stdout, TextWriter stderr)
    {
        switch (result.Outcome)
        {
            case ConversionRunOutcome.Succeeded:
                stdout.WriteLine($"转换成功，耗时 {result.Duration.TotalSeconds:F1} 秒");
                if (result.Validation is { } validation)
                    stdout.WriteLine($"校验通过: {validation.TilesetCount} 个 tileset / {validation.TileCount} 个瓦片 / {validation.ContentCount} 个内容，共 {validation.TotalBytes} 字节");
                stdout.WriteLine($"tileset: {Path.Combine(output, "tileset.json")}");
                return CliExitCodes.Success;
            case ConversionRunOutcome.OutputConflict:
                stderr.WriteLine($"输出冲突: {result.Diagnostic}");
                return CliExitCodes.OutputConflict;
            case ConversionRunOutcome.ValidationFailed:
                stderr.WriteLine($"tileset 校验失败: {result.Diagnostic}");
                stderr.WriteLine($"详细日志: {logPath}");
                return CliExitCodes.ValidationFailed;
            case ConversionRunOutcome.TimedOut:
                stderr.WriteLine($"转换超时: {result.Diagnostic}");
                stderr.WriteLine($"详细日志: {logPath}");
                return CliExitCodes.ConversionFailed;
            case ConversionRunOutcome.ConversionFailed:
                stderr.WriteLine($"转换失败（Obj2Tiles 退出码 {result.ExitCode}）: {result.Diagnostic}");
                stderr.WriteLine($"详细日志: {logPath}");
                return CliExitCodes.ConversionFailed;
            case ConversionRunOutcome.Canceled:
                stderr.WriteLine("转换已取消。");
                return CliExitCodes.Canceled;
            default:
                stderr.WriteLine($"未预期的运行结果: {result.Outcome}");
                return CliExitCodes.ConversionFailed;
        }
    }

    private static IHost BuildHost()
    {
        // CLI 从可执行文件目录加载 appsettings.json，并同样接受 Conversion__* 环境变量。
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.AddFilter(level => level >= LogLevel.Information);
        builder.Services.AddConversionServices(builder.Configuration);
        return builder.Build();
    }

    private static string ResolveInput(string input)
    {
        var fullPath = Path.GetFullPath(input);
        if (File.Exists(fullPath))
        {
            if (!fullPath.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("输入文件不是 .obj");
            return fullPath;
        }
        if (Directory.Exists(fullPath))
        {
            var candidates = Directory.EnumerateFiles(fullPath, "*.obj", SearchOption.TopDirectoryOnly).ToArray();
            var preferred = candidates.FirstOrDefault(path =>
                string.Equals(Path.GetFileName(path), "odm_textured_model_geo.obj", StringComparison.OrdinalIgnoreCase));
            if (preferred != null) return preferred;
            if (candidates.Length == 1) return candidates[0];
            throw new ArgumentException(candidates.Length == 0
                ? "输入目录中没有 .obj 文件"
                : "输入目录包含多个 .obj 文件，请直接指定文件路径");
        }
        throw new FileNotFoundException("输入路径不存在", input);
    }

    private static async Task<GeoReference?> ResolveGeoReferenceAsync(
        ConvertCommandLine command, ReferenceLlaReader reader, CancellationToken cancellationToken)
    {
        if (command.Local) return null;
        if (command.ReferenceLlaPath != null)
        {
            var path = Path.GetFullPath(command.ReferenceLlaPath);
            if (!File.Exists(path)) throw new FileNotFoundException("reference_lla 文件不存在", path);
            return await reader.ReadAsync(path, cancellationToken);
        }
        if (command.Latitude.HasValue)
            return new GeoReference(command.Latitude.Value, command.Longitude!.Value, command.Altitude!.Value);
        return null;
    }
}
