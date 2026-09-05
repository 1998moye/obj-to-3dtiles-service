namespace ModelConversion.Service.Cli;

public static class CliUsage
{
    public static int PrintRoot(TextWriter output)
    {
        output.WriteLine("""
            模型转换服务 - OBJ → 3D Tiles

            用法:
              ModelConversion.Service                 启动 HTTP 服务（无参数等同于 serve）
              ModelConversion.Service serve           启动 HTTP 服务
              ModelConversion.Service convert ...     同步执行一次 OBJ → 3D Tiles 转换
              ModelConversion.Service --help          显示本帮助

            运行 convert --help 查看转换参数与退出码。
            """);
        return CliExitCodes.Success;
    }

    public static int PrintUnknownCommand(string command, TextWriter output)
    {
        output.WriteLine($"未知命令: {command}");
        output.WriteLine("可用命令: serve | convert | --help");
        return CliExitCodes.UsageError;
    }

    public static void PrintConvert(TextWriter output)
    {
        output.WriteLine("""
            用法: convert --input <OBJ目录或OBJ文件> --output <输出目录> [选项]

            必填:
              --input <路径>                 OBJ 模型目录（目录内优先选择 odm_textured_model_geo.obj，
                                           否则要求仅有一个 .obj）或直接指定 .obj 文件
              --output <路径>                最终输出目录；已存在时拒绝覆盖（退出码 3）

            坐标模式（互斥）:
              --local                        本地坐标，不做地理参考
              --reference-lla <文件>         从 ODM reference_lla.json 读取经纬高
              --lat <值> --lon <值> --alt <值>   显式经纬高，三个必须同时提供

            配置:
              --profile <名称>               转换配置档（默认 industrial-jpeg）；覆盖参数以 profile 为基础值
              配置来源: 可执行文件旁的 appsettings.json 与 Conversion__* 环境变量

            转换参数覆盖（可选，全部强类型）:
              --lods <1-10>
              --min-geometry-quality <(0,1]>
              --hierarchical <true|false>
              --hlod-error-divisor <正数>
              --hlod-target-ratio <(0,1]>
              --external-tileset-depth <0-8>
              --octree <true|false>
              --divisions <0-8>
              --zsplit <true|false>
              --split-strategy <AbsoluteCenter|VertexBaricenter|VertexMedian>
              --lod-texture-scale <(0,1]>
              --max-texture-size <128-8192>
              --texture-format <Jpeg|Webp|Ktx2>
              --texture-quality <1-100>
              --ktx2-quality <1-255>

            退出码:
              0 成功 | 2 参数/输入错误 | 3 输出冲突 | 4 转换失败或超时 | 5 tileset 校验失败 | 130 用户取消

            示例:
              convert --input D:\models\obj1 --output D:\tiles\obj1 --profile industrial-jpeg --reference-lla D:\models\obj1\reference_lla.json
              convert --input ./obj1 --output ./tiles/obj1 --local --lods 5 --max-texture-size 1024
            """);
    }
}
