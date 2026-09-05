# 模型转换服务（Model Conversion Service）

独立的 **OBJ → 3D Tiles** 转换服务。接收 NodeODM/ODM 摄影测量生成的地理参考 OBJ（`odm_textured_model_geo.obj`），输出可被 Mars3D/CesiumJS 稳定加载的 3D Tiles（空间优先 HLOD、meshoptimizer 绝对误差简化、边界锁定、REPLACE 精化、外部 tileset、纹理逐级降采样）。

本项目源码公开：非商业使用免费但必须注明项目来源；商业使用必须提前联系作者取得书面授权。仓库内第三方 Obj2Tiles 代码继续适用其 AGPL-3.0 许可证，详情见[许可与商业使用](#许可与商业使用)。

同一个可执行文件/同一个 Docker 镜像提供两种用法：

- **HTTP API 服务**：异步作业队列、状态持久化、取消/重试、重启恢复，供平台集成；
- **CLI 一次性转换**：同步执行、稳定退出码，适合脚本、流水线与 `docker run --rm`。

两种入口共用同一个转换核心（`ConversionRunner`），转换、校验、发布语义完全一致。

## 目录

- [依赖](#依赖)
- [快速开始（Docker）](#快速开始docker)
- [CLI 参考](#cli-参考)
- [HTTP API 参考](#http-api-参考)
- [API 快速测试（PowerShell）](#api-快速测试powershell)
- [配置](#配置)
- [前端查看器接入](#前端查看器接入)
- [验证](#验证)
- [离线构建](#离线构建)
- [常见问题（FAQ）](#常见问题faq)
- [已知限制](#已知限制)
- [许可与商业使用](#许可与商业使用)

## 依赖

| 用途 | 依赖 |
|---|---|
| Docker 运行（推荐） | Docker / Docker Compose。镜像内含 Obj2Tiles 与服务，无需其他依赖 |
| 本机运行/开发 | .NET 10 SDK；本机转换需自备 Obj2Tiles 可执行文件并配置 `Conversion:Obj2TilesExecutable` |

## 快速开始（Docker）

```powershell
docker compose up -d --build
```

服务监听 `http://127.0.0.1:8091`（容器内 8080）。compose 挂载约定：

| 宿主目录 | 容器目录 | 说明 |
|---|---|---|
| `./data/input` | `/data/input` | 输入（只读） |
| `./data/output` | `/data/output` | 已发布输出 |
| `./data/state` | `/data/state` | 作业状态/日志，重启后恢复 |

健康检查：`GET /health/live`（进程存活）、`GET /health/ready`（Obj2Tiles 可用）；compose 已内置 healthcheck。

```powershell
Invoke-RestMethod http://127.0.0.1:8091/health/ready
docker compose ps
```

API 不能直接读取宿主机上的任意路径，只能读取挂载到 `/data/input` 的文件。使用 compose 时，应先把 OBJ、MTL 和纹理完整放入 `./data/input`：

```powershell
Copy-Item -LiteralPath D:\models\obj1 -Destination .\data\input\obj1 -Recurse
```

请求中的 `inputPath` 必须是相对于 `./data/input` 的路径，例如 `obj1/odm_textured_model_geo.obj`。`outputPath` 同理是相对于 `./data/output` 的目录；例如 `test/obj6` 会发布到 `./data/output/test/obj6`。

同一个镜像执行一次性 CLI 转换（**容器内使用 Linux 路径**）：

```powershell
docker run --rm `
  -v D:\models:/data/input:ro `
  -v D:\tiles:/data/output `
  model-conversion-service:local `
  convert --input /data/input/obj1 --output /data/output/obj1 --profile industrial-jpeg --reference-lla /data/input/obj1/reference_lla.json
```

CLI 退出码原样透传宿主机，可直接用于流水线判断。

## CLI 参考

```text
ModelConversion.Service                  启动 HTTP 服务（无参数等同于 serve）
ModelConversion.Service serve            启动 HTTP 服务
ModelConversion.Service convert ...      同步执行一次转换
ModelConversion.Service --help           显示帮助
```

Docker 下对应 `docker run --rm model-conversion-service:local [serve|convert|--help] ...`。

### convert 参数

必填：

| 参数 | 说明 |
|---|---|
| `--input <路径>` | OBJ 目录（优先取其中 `odm_textured_model_geo.obj`，否则要求目录内唯一 `.obj`）或直接指定 `.obj` 文件 |
| `--output <路径>` | 最终输出目录；**已存在时拒绝覆盖**（退出码 3），不提供 `--force` |

坐标模式（互斥，三选一或由 profile 决定）：

| 参数 | 说明 |
|---|---|
| `--local` | 本地坐标，不做地理参考（恒等变换；查看器能看几何，但不落在地图真实位置） |
| `--reference-lla <文件>` | 从 ODM `reference_lla.json` 读取锚点经纬高，模型落在真实地理位置 |
| `--lat <v> --lon <v> --alt <v>` | 显式锚点经纬高，三个必须同时提供 |

规则：`--local` 与任何地理参考参数互斥；`--reference-lla` 与显式经纬高互斥；同时违反返回退出码 2。都不提供时沿用 profile 的 `Local` 设置；若 profile 要求地理参考（`Local=false`）而未给坐标，报参数错误。

配置：

| 参数 | 说明 |
|---|---|
| `--profile <名称>` | 配置档（默认 `industrial-jpeg`），所有覆盖参数以其为基础值 |

配置来源：可执行文件旁的 `appsettings.json` 与 `Conversion__*` 环境变量。

转换参数覆盖（可选，强类型，范围与 API 校验一致）：

| 参数 | 类型/范围 | 含义 |
|---|---|---|
| `--lods` | 整数 1-10 | LOD 层级数 |
| `--min-geometry-quality` | (0,1] | 最粗层几何保留比例 |
| `--hierarchical` | true/false | HLOD 管线开关 |
| `--hlod-error-divisor` | 正数 | HLOD 误差梯度除数 |
| `--hlod-target-ratio` | (0,1] | HLOD 目标误差比例 |
| `--external-tileset-depth` | 0-8 | 外部 tileset 外链深度 |
| `--octree` | true/false | 八叉树切分 |
| `--divisions` | 0-8 | 切分段数 |
| `--zsplit` | true/false | Z 向切分 |
| `--split-strategy` | AbsoluteCenter / VertexBaricenter / VertexMedian | 切分策略 |
| `--lod-texture-scale` | (0,1] | 纹理逐级缩放 |
| `--max-texture-size` | 128-8192 | 纹理边长上限 |
| `--texture-format` | Jpeg / Webp / Ktx2 | 纹理格式 |
| `--texture-quality` | 1-100 | JPEG/Webp 质量 |
| `--ktx2-quality` | 1-255 | KTX2/BasisU 质量 |

### 退出码

| 码 | 含义 |
|---|---|
| 0 | 成功 |
| 2 | 参数/输入错误（缺参、未知参数、互斥、非法值、输入不存在、未知 profile） |
| 3 | 输出冲突（输出目录已存在） |
| 4 | 转换失败或超时 |
| 5 | tileset 校验失败 |
| 130 | 用户取消（Ctrl+C；首次请求取消，二次强杀） |

### 输出语义

- stdout 打印单调递增的阶段进度、Obj2Tiles 详细过程、最终 `tileset.json` 绝对路径、耗时与校验摘要；详细输出也会保留在 `<输出目录>.log`。HTTP 服务模式下，同样可通过 `docker compose logs -f model-conversion-service` 查看实时转换日志。
- 暂存目录位于输出旁的 `<输出>.staging-<id>`；转换+校验全部通过后**原子发布**为最终目录（跨挂载点时自动退化为复制后删除）。失败/取消时暂存被清理、最终目录不可见，日志保留。
- 校验内容：tileset 树结构（含外部子树递归）、geometricError 单调性、REPLACE 精化、b3dm/glb 二进制头与长度、路径越界防护。

### 示例

本机：

```powershell
dotnet ModelConversion.Service.dll convert --input D:\models\obj1 --output D:\tiles\obj1 --profile industrial-jpeg --reference-lla D:\models\obj1\reference_lla.json
dotnet ModelConversion.Service.dll convert --input ./obj1 --output ./tiles/obj1 --local --lods 7 --max-texture-size 1024
```

Docker（真实坐标定位）：

```powershell
docker run --rm -v D:\models:/data/input:ro -v D:\tiles:/data/output model-conversion-service:local convert --input /data/input/obj1 --output /data/output/obj1 --lat 33.62678591 --lon 117.00336389 --alt 0
```

## HTTP API 参考

基础路径 `/api/v1/conversions`，OpenAPI 3.0 契约：`GET /openapi/v1.json`。

### 创建作业

```http
POST /api/v1/conversions
Content-Type: application/json

{
  "inputPath": "obj1/odm_textured_model_geo.obj",
  "outputPath": "county/demo-001",
  "profile": "industrial-jpeg",
  "referenceLlaPath": "obj1/reference_lla.json",
  "geoReference": null,
  "overrides": {
    "lods": 5,
    "minGeometryQuality": 0.5,
    "maxTextureSize": 2048,
    "textureFormat": "Jpeg",
    "textureQuality": 85
  }
}
```

字段规则：

- `inputPath`（必填）：`InputRoot` 内相对路径，必须以 `.obj` 结尾且文件存在；
- `outputPath`（可选）：`OutputRoot` 内相对目录，省略时为 `jobs/{jobId}`；拒绝绝对路径/盘符/UNC、`..` 逃逸、保留前缀 `jobs/`；目标已存在或被进行中任务占用时返回 `409`；
- `profile`（可选，默认 `industrial-jpeg`）；
- `referenceLlaPath` 与 `geoReference` 互斥；`overrides.local=true`（或 profile 为本地模式）时提供地理参考返回 `400`；
- `overrides`（可选）：字段与 CLI 覆盖参数同名（camelCase），全部可空，缺省沿用 profile；未知字段、非法范围返回 `400`；**不接受原始 Obj2Tiles 命令行字符串**；
- 请求体出现任何未定义字段返回 `400`（`UnmappedMemberHandling.Disallow`）。

成功响应 `202 Accepted`，body 为作业对象。

### 作业对象

```json
{
  "id": "…",
  "state": "Queued",
  "profileName": "industrial-jpeg",
  "settingsSnapshot": { "lods": 5, "textureFormat": "Jpeg" },
  "inputRelativePath": "obj1/odm_textured_model_geo.obj",
  "referenceLlaRelativePath": "obj1/reference_lla.json",
  "outputRelativePath": "county/demo-001",
  "geoReference": { "latitude": 34.1, "longitude": 113.2, "altitude": 92.3 },
  "attempt": 1,
  "previousJobId": null,
  "createdAt": "…", "updatedAt": "…", "startedAt": null, "completedAt": null,
  "exitCode": null,
  "diagnostic": null,
  "validation": null,
  "tilesetUrl": null,
  "progress": {
    "percent": 0,
    "stage": "Queued",
    "message": "等待执行",
    "updatedAt": "…"
  },
  "canCancel": true
}
```

- `settingsSnapshot`：创建时 profile+overrides 合并后的**不可变参数快照**；重启恢复与重试都使用快照，不随服务端 profile 后续修改漂移；
- `state` 状态机：`Queued → Running → Validating → Succeeded / Failed / Canceled`；
- `progress.percent` 为 `0-100` 的单调进度；`stage` 可能为 `Queued / Preparing / Converting / Validating / Publishing / Canceling / Completed / Failed / Canceled`；
- `canCancel` 表示当前是否可以调用取消接口；
- 成功后 `validation` 带校验摘要（tileset/瓦片/内容数量、总字节、根 geometricError），`tilesetUrl` = `/api/v1/conversions/{id}/result/tileset.json`；
- 失败时 `diagnostic` 带原因（含 Obj2Tiles 日志尾部）。

### 查询 / 取消 / 重试

```text
GET  /api/v1/conversions              列表（创建时间倒序）
GET  /api/v1/conversions/{id}         详情，404 不存在
GET  /api/v1/conversions/{id}/status  精简状态、是否可取消及当前进度
GET  /api/v1/conversions/{id}/progress 实时进度百分比、阶段与消息
POST /api/v1/conversions/{id}/cancel  取消；排队中立即取消，运行中请求取消；409 状态切换竞争
POST /api/v1/conversions/{id}/retry   仅失败/已取消可重试（409 其他状态）；新作业复用原快照与输出位置，旧记录保留审计
```

`status` 和 `progress` 是查询接口，必须使用 **GET** 且不需要请求体。误用 POST 会返回 `405 Method Not Allowed`。

### 读取结果（Cesium/Mars3D）

```text
GET /api/v1/conversions/{id}/result/{path}
```

- 仅 `Succeeded` 作业可访问；以该作业已发布输出目录为根解析相对路径，支持外部 tileset 子树、`.b3dm`、纹理等相对引用；
- MIME：`.json→application/json`、`.b3dm→application/octet-stream`、`.glb→model/gltf-binary`、`.jpg/.jpeg→image/jpeg`、`.png→image/png`、`.webp→image/webp`、`.ktx2→image/ktx2`；
- 越界（含 URL 编码的 `..` 与绝对路径）、目录浏览、跨作业读取、未完成作业一律 `404`；
- 暂存目录、日志目录与 `StateRoot` 永不暴露。

### curl 示例（PowerShell 用 curl.exe，JSON 用单引号）

```powershell
curl.exe -s -X POST http://127.0.0.1:8091/api/v1/conversions -H "Content-Type: application/json" -d '{"inputPath":"obj1/odm_textured_model_geo.obj","outputPath":"county/demo-001","profile":"industrial-jpeg","referenceLlaPath":"obj1/reference_lla.json"}'

curl.exe -s http://127.0.0.1:8091/api/v1/conversions/<id>
curl.exe -s http://127.0.0.1:8091/api/v1/conversions/<id>/status
curl.exe -s http://127.0.0.1:8091/api/v1/conversions/<id>/progress
curl.exe -s -X POST http://127.0.0.1:8091/api/v1/conversions/<id>/cancel
curl.exe -s -X POST http://127.0.0.1:8091/api/v1/conversions/<id>/retry
curl.exe -sO http://127.0.0.1:8091/api/v1/conversions/<id>/result/tileset.json
```

## API 快速测试（PowerShell）

下面使用 `local-test` 和两级 LOD 做一次快速冒烟，避免首次验证生成过多瓦片。每次测试应使用新的 `outputPath`，服务不会覆盖已有成果。

```powershell
$base = "http://127.0.0.1:8091"
$outputName = "api-test-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

$body = @{
    inputPath  = "obj1/odm_textured_model_geo.obj"
    outputPath = $outputName
    profile    = "local-test"
    overrides  = @{
        lods           = 2
        maxTextureSize = 1024
    }
} | ConvertTo-Json -Depth 5

$job = Invoke-RestMethod `
    -Method Post `
    -Uri "$base/api/v1/conversions" `
    -ContentType "application/json" `
    -Body $body

$id = $job.id
$job | ConvertTo-Json -Depth 8
```

查询进度和状态（均为 GET）：

```powershell
Invoke-RestMethod "$base/api/v1/conversions/$id/progress" | ConvertTo-Json -Depth 5
Invoke-RestMethod "$base/api/v1/conversions/$id/status"   | ConvertTo-Json -Depth 5
```

持续观察直到结束：

```powershell
while ($true) {
    $status = Invoke-RestMethod "$base/api/v1/conversions/$id/status"
    Write-Host "$($status.progress.percent)%  $($status.progress.stage)  $($status.progress.message)"
    if ($status.state -in @("Succeeded", "Failed", "Canceled")) { break }
    Start-Sleep -Seconds 2
}
```

取消运行中的任务：

```powershell
Invoke-RestMethod -Method Post "$base/api/v1/conversions/$id/cancel"
```

成功后取得可直接交给 Cesium/Mars3D 的地址：

```powershell
$detail = Invoke-RestMethod "$base/api/v1/conversions/$id"
$tilesetUrl = "$base$($detail.tilesetUrl)"
$tilesetUrl
```

完整契约可在浏览器查看：`http://127.0.0.1:8091/openapi/v1.json`。

### 错误状态码汇总

| 场景 | 状态码 |
|---|---|
| 未知字段 / 非法范围 / 互斥参数 / 输入不存在 / 非法 outputPath | 400 |
| 输出目录已存在或被进行中任务占用 / 对运行中作业 retry / 取消竞争 | 409 |
| 作业不存在 / 结果路径越界 / 非成功作业读结果 | 404 |

## 配置

`server/ModelConversion.Service/appsettings.json` 的 `Conversion` 节，全部可用 `Conversion__*` 环境变量覆盖（如 `Conversion__MaxConcurrentJobs=2`）：

| 键 | 默认 | 说明 |
|---|---|---|
| `InputRoot` / `OutputRoot` / `StateRoot` | `/data/input` `/data/output` `/data/state` | API 输入/输出/状态根目录（CLI 任意路径不受其约束） |
| `Obj2TilesExecutable` | `/app/obj2tiles/Obj2Tiles` | Obj2Tiles 可执行文件路径 |
| `MaxConcurrentJobs` | 1 | 并发转换数（1-8）。摄影测量模型为重内存任务，默认单并发 |
| `JobTimeoutMinutes` | 720 | 单作业超时，超时终止完整进程树 |
| `MaxTilesetJsonBytes` | 16777216 | 单个 tileset JSON 大小上限 |
| `Profiles` | 见下 | 配置档字典 |

内置配置档：

| profile | Lods | 纹理上限 | 纹理格式 | Local | 用途 |
|---|---|---|---|---|---|
| `industrial-jpeg`（默认） | 5 | 2048 | JPEG | false | 生产基线，兼容性优先，要求地理参考 |
| `industrial-ktx2` | 5 | 2048 | KTX2 | false | KTX2 纹理降低显存，验证浏览器/显卡支持后再切 |
| `local-test` | 5 | 1024 | JPEG | true | 本地坐标结构测试 |

自定义档：在 `Profiles` 下加一节新名称即可（或 `Conversion__Profiles__myprofile__Lods=7` 环境变量），无需改代码。

## 前端查看器接入

- Mars3D/Cesium 直接使用作业详情中的 `tilesetUrl` 加载；
- 预览建议参数：`maximumScreenSpaceError=2`、动态屏幕空间误差、`cullWithChildrenBounds=false`；
- 服务默认允许任意 Origin、Header 和 Method，前端在其他域名或端口时可以直接调用 API 并加载瓦片。生产环境若需要访问控制，应由反向代理或网关限制来源并增加认证；
- ODM 的 OBJ 为 Z-up 坐标而 b3dm 按 Y-up→Z-up 轴校正加载：地理参考模式（`--reference-lla`/`--lat,--lon,--alt` 或 API 的 `referenceLlaPath`/`geoReference`）输出的根变换为 ENU 旋转，模型在 Cesium 中会"竖立"。需要在查看侧叠加 `Rx(-90°)` 修正（右乘根 transform、平移保持原值），或在做平台接入时统一处理；`--local` 输出为恒等变换，同理。

## 验证

公开仓库不包含测试模型、测试工程和测试产物。可通过镜像构建、CLI 帮助和健康检查验证运行环境：

```powershell
docker compose build
docker run --rm model-conversion-service:local --help
docker compose up -d
Invoke-RestMethod http://127.0.0.1:8091/health/ready
```

随后按 [API 快速测试（PowerShell）](#api-快速测试powershell) 使用自己的 OBJ 模型做端到端验证。

## 离线构建

Dockerfile 依赖两个基础镜像：`mcr.microsoft.com/dotnet/sdk:10.0`（构建）与 `mcr.microsoft.com/dotnet/aspnet:10.0`（运行时）。构建环境无法访问 mcr 时，在能联网的机器上预取并归档：

```powershell
docker pull mcr.microsoft.com/dotnet/sdk:10.0
docker pull mcr.microsoft.com/dotnet/aspnet:10.0
docker save -o dotnet-sdk_10.0.tar mcr.microsoft.com/dotnet/sdk:10.0
docker save -o dotnet-aspnet_10.0.tar mcr.microsoft.com/dotnet/aspnet:10.0
# 目标机器：
docker load -i dotnet-sdk_10.0.tar
docker load -i dotnet-aspnet_10.0.tar
```

镜像内 NuGet 还原走 nuget.org（国内通常直连可用），无需额外配置。

## 常见问题（FAQ）

**`docker run` 报 `invalid reference format`（cmd）**
你在 cmd 里用了 PowerShell 的反引号续行符。cmd 多行用 `^`，或整条命令写一行；PowerShell 才用 `` ` ``。查看退出码：cmd 用 `echo %ERRORLEVEL%`，PowerShell 用 `$LASTEXITCODE`。

**路径用 `\` 还是 `/`？**
宿主（Windows）侧两者皆可（Git Bash 里用 `/d/...`）；容器内（Linux）参数必须 `/`，如 `--input /data/input/obj1`。挂卷语法 `-v 宿主路径:容器路径[:ro]`，冒号左侧随意、右侧必须 `/`。

**CLI 跑起来没有进度输出，是不是卡死？**
当前版本会在 stdout/容器控制台显示阶段进度和 Obj2Tiles 详细过程，同时写入 `<输出>.log`。另开窗口执行 `Get-Content <输出>.log -Wait -Tail 20`（PowerShell），或使用 `docker compose logs -f model-conversion-service` 跟随日志。

**创建 API 作业返回“输入文件不存在”？**
`inputPath` 不是宿主机绝对路径，而是容器 `/data/input` 下的相对路径。compose 默认把 `./data/input` 挂到该目录；确认 OBJ、MTL、纹理都已复制进去，并用 `Test-Path .\data\input\obj1\odm_textured_model_geo.obj` 检查。

**查询 `/progress` 返回 405？**
进度和状态查询只支持 GET，不要发送请求体：`GET /api/v1/conversions/{id}/progress`、`GET /api/v1/conversions/{id}/status`。创建、取消和重试才使用 POST。

**坐标模式怎么选？**
有 `reference_lla.json` 用 `--reference-lla`；知道坐标用 `--lat/--lon/--alt`；只是测试几何时用 `--local`。三者互斥。生产档 `industrial-jpeg` 必须给坐标，否则退出码 2。

**输出目录已存在怎么办？**
设计上拒绝覆盖（退出码 3 / API 409）。换目录名，或确认后人工删除旧目录。不提供 `--force` 是有意的：防止误删已发布成果。

**为什么 HLOD 每增加一级，瓦片数量大约变成四倍？**
当前空间优先 HLOD 使用 XY 四叉树，每深入一级会把每个区域拆成四块。五级输出包含 `LOD-0` 到 `LOD-4`；大型模型若更关注转换和请求数量，可以在单次 `overrides.lods` 中降低层数。已经生成的 tileset 不受后续配置变化影响。

**转换好的模型在前端测试页里扭曲/不动态精化？**
Vite 开发服务器对 `public/` 下点开头目录（如 `.tmp`）运行期间新增的文件索引不可靠（子树 JSON 会返回 index.html）。请把产物放在非点目录（如 `public/models-regression/...`）或拷贝后重启 dev 服务器；并注意浏览器缓存（F12 勾"禁用缓存"）。

**玩具级小模型（如 cube）用 HLOD 档转换失败？**
已知上游边界：meshoptimizer 对极小网格（个位三角形）可能返回非法索引数。测试小模型时用 `overrides`/`--hierarchical false` 关闭 HLOD；真实摄影测量模型不受影响。

## 已知限制

- CORS 默认完全开放，但不支持认证；不要把服务端口直接暴露到不可信公网，应由平台网关承担认证、限流和来源限制；
- 不支持上传接口、对象存储/CDN（按设计边界，由平台侧承担）；
- 非 root 运行沿用 aspnet 基础镜像的 `$APP_UID` 模型；
- 源模型自身缺陷（如房檐异常、破洞、黑色拉伸面）会原样保留在输出中，不属于转换回归。

## 许可与商业使用

- 仓库原创服务代码、配置和文档适用根目录 [LICENSE](./LICENSE)：非商业使用免费，必须保留版权声明并注明项目名称、作者和仓库地址；商业使用必须通过 GitHub 账号或仓库 Issue 联系作者并取得单独书面授权。
- 这是带非商业限制的**源码公开许可证**，不是 OSI 定义下的开源许可证。
- `third-party/Obj2Tiles` 及本仓库对它的定制修改继续适用 AGPL-3.0，详见 [third-party/Obj2Tiles/LICENSE.md](./third-party/Obj2Tiles/LICENSE.md)，不受根目录商业限制重新许可。
- 第三方声明汇总见 [NOTICE](./NOTICE)；meshoptimizer 为 MIT，通过 Obj2Tiles 依赖的官方 NuGet 原生包引入。
