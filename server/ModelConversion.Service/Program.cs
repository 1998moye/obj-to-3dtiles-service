using ModelConversion.Service.Api;
using ModelConversion.Service.Cli;

// 无参数或仅带 ASP.NET 配置参数时保持原有 HTTP 服务行为；convert 为同步一次性转换。
var firstArgument = args.FirstOrDefault();
if (firstArgument == "convert") return await ConvertCommand.RunAsync(args[1..]);
if (firstArgument == "serve") return await ServeHost.RunAsync(args[1..]);
if (firstArgument is "--help" or "-h" or "help") return CliUsage.PrintRoot(Console.Out);
if (firstArgument != null && !firstArgument.StartsWith('-')) return CliUsage.PrintUnknownCommand(firstArgument, Console.Error);
return await ServeHost.RunAsync(args);

public partial class Program;
