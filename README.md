# OBJ 转 3D Tiles 服务（obj-to-3dtiles-service）

独立的 **OBJ → 3D Tiles** 转换服务。接收 NodeODM/ODM 摄影测量生成的地理参考 OBJ（`odm_textured_model_geo.obj`），输出可被 Mars3D/CesiumJS 稳定加载的 3D Tiles（空间优先 HLOD、meshoptimizer 绝对误差简化、边界锁定、REPLACE 精化、外部 tileset、纹理逐级降采样）。

本项目源码公开：非商业使用免费但必须注明项目来源；商业使用必须提前联系作者取得书面授权。仓库内第三方 Obj2Tiles 代码继续适用其 AGPL-3.0 许可证，详情见[许可与商业使用](#许可与商业使用)。

同一个可执行文件/同一个 Docker 镜像提供两种用法：

- **HTTP API 服务**：异步作业队列、状态持久化、取消/重试、重启恢复，供平台集成；
- **CLI 一次性转换**：同步执行、稳定退出码，适合脚本、流水线与 `docker run --rm`。

两种入口共用同一个转换核心（`ConversionRunner`），转换、校验、发布语义完全一致。

## 目录

- [依赖](#依赖)
- [快速开始（Docker）](#快速开始docker)
- [Windows 远程完整使用教程](#windows-远程完整使用教程)
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

直接使用已发布的 `v3.1` 镜像：

```powershell
docker pull dz98/obj-to-3dtiles-service:v3.1

docker run --rm `
  -v "C:/Users/qkp/Desktop:/data/input:ro" `
  -v "D:/tiles:/data/output" `
  dz98/obj-to-3dtiles-service:v3.1 `
  convert --input /data/input/obj2 --output /data/output/obj2 `
  --lat 33.62678591 --lon 117.00336389 --alt 0 --lods 4
```

`v3.1` 在 `v3` 的有界内存纹理处理、深层封底剥离、HLOD 三角形包络安全检查和图集边缘延展之上，
新增健康容量接口、可恢复分片上传会话、结果清单 SHA-256 与 Range 下载、终态 TTL 清理。
上述转换侧处理用于防止 OOM、地下封底灰块、简化产生的跨洞大三角形及 mipmap 黑边；它们不会重建源 OBJ 中本来就缺失或已经错误封闭的表面。
如业务模型确实需要保留深层结构，追加 `--strip-deep-bottom false`。

Linux 上直接使用 `docker run` 时，宿主机输出目录必须预先存在并对容器 UID 1654 可写；常驻 API 服务建议使用下面的 Compose 自动初始化目录与权限。

使用 Compose 启动发布镜像：

```powershell
docker compose pull
docker compose up -d
```

Compose 使用 `pull_policy: always`，即使服务器本地已经存在同名 `v3`，启动时也会检查并拉取
远端最新摘要，避免持续发布标签被旧缓存静默截留。

Compose 默认使用宿主机绝对目录，不依赖 compose 文件所在目录，也不要求 Windows 存在 D 盘：

- Windows：`%USERPROFILE%/obj-to-3dtiles-service-data`；
- Linux：`/var/lib/obj-to-3dtiles-service-data`；
- 显式设置 `OBJ_TO_3DTILES_DATA_ROOT` 时优先采用该绝对路径。

一次性的 `volume-init` 容器创建目录内的
`input/output/state` 并交给镜像内 UID 1654 后退出，正式服务继续以非 root 用户运行；因此
`docker compose ps -a` 中 `volume-init` 显示 `Exited (0)` 是正常状态。

Compose 不再固定 24G 内存上限，服务会根据部署机器实际可见内存自动选择资源档位。共享宿主机如需硬限制，
可通过额外的 `compose.override.yml` 增加 `resources.limits.memory`。

从源码构建后仍使用同一份 Compose：

```powershell
docker build -t obj-to-3dtiles-service:local .
$env:OBJ_TO_3DTILES_IMAGE = "obj-to-3dtiles-service:local"
docker compose up -d
```

服务监听 `http://127.0.0.1:8091`（容器内 8080）。宿主机目录映射约定：

| 宿主机数据根下目录 | 容器目录 | 说明 |
|---|---|---|
| `input` | `/data/obj2tiles/input` | API 上传后原子发布的输入模型 |
| `output` | `/data/obj2tiles/output` | 已发布输出 |
| `state` | `/data/obj2tiles/state` | 作业状态、日志和上传暂存，重启后恢复 |

例如 Windows 用户 `qkp` 的结果默认可直接在
`C:\Users\qkp\obj-to-3dtiles-service-data\output` 查看；Linux 默认在
`/var/lib/obj-to-3dtiles-service-data/output`。需要更换磁盘时才设置覆盖变量：

```powershell
$env:OBJ_TO_3DTILES_DATA_ROOT = "E:/model-data"
docker compose up -d
```

健康检查：`GET /health/live`（进程存活）、`GET /health/ready`（Obj2Tiles 可用）；compose 已内置 healthcheck。

```powershell
Invoke-RestMethod http://127.0.0.1:8091/health/ready
docker compose ps
```

模型通过上传 API 进入宿主机输入根目录下的唯一目录，调用方不需要直接操作服务器目录。先把 OBJ、MTL 和纹理文件夹压缩为 ZIP，再上传：

```powershell
$zip = "$env:TEMP\obj1.zip"
Compress-Archive -Path "D:\models\obj1\*" -DestinationPath $zip
$upload = curl.exe -s -X POST http://127.0.0.1:8091/api/v1/uploads `
  -H "Content-Type: application/zip" --data-binary "@$zip" | ConvertFrom-Json
$upload
```

响应的 `inputPath` 可直接传给创建转换接口；每次上传都会使用新的 UUID 目录，不覆盖旧模型。
如果包内恰好有一个 `reference_lla.json`，响应还会返回可直接使用的 `referenceLlaPath`。

同一个镜像执行一次性 CLI 转换（**容器内使用 Linux 路径**）：

```powershell
docker run --rm `
  -v D:\models:/data/input:ro `
  -v D:\tiles:/data/output `
  obj-to-3dtiles-service:local `
  convert --input /data/input/obj1 --output /data/output/obj1 --profile industrial-jpeg --reference-lla /data/input/obj1/reference_lla.json
```

CLI 退出码原样透传宿主机，可直接用于流水线判断。

## Windows 远程完整使用教程

本节覆盖一次完整操作：Windows 将 OBJ 模型包上传到 Linux 服务器、创建转换、按 ID 查询进度，
最后读取文件清单或下载完整 3D Tiles。示例使用 SSH 隧道访问 API，不需要将无认证的 8091 端口暴露到公网。

### 第一步：建立 SSH 隧道

在 Windows 打开第一个 PowerShell 窗口并保持运行：

```powershell
ssh -N -L 18091:127.0.0.1:8091 root@<服务器IP>
```

输入服务器密码后，本机 `18091` 会转发到服务器 `8091`。再打开第二个 PowerShell 窗口：

```powershell
$base = "http://127.0.0.1:18091"
Invoke-RestMethod "$base/health/ready"
```

预期返回 `{"status":"ready"}`。如果通过受认证的网关直接访问，可把 `$base` 换成网关地址，
后续命令不变。

### 第二步：打包并上传模型

ZIP 内应包含一个 OBJ 及其同目录 MTL、纹理；可以有外层目录，但只能有一个 `.obj` 文件。

```powershell
$modelDir = "C:\Users\<用户名>\Desktop\obj3"
$zip = Join-Path $env:TEMP "obj3-$(Get-Date -Format 'yyyyMMdd-HHmmss').zip"

Compress-Archive `
  -Path "$modelDir\*" `
  -DestinationPath $zip `
  -CompressionLevel Fastest

$uploadJson = curl.exe --fail-with-body `
  -X POST "$base/api/v1/uploads" `
  -H "Content-Type: application/zip" `
  --data-binary "@$zip"

if ($LASTEXITCODE -ne 0) { throw "模型上传失败" }
$upload = $uploadJson | ConvertFrom-Json
$upload | ConvertTo-Json -Depth 5
```

大模型纹理本身通常已经压缩，`Fastest` 可减少客户端 CPU 时间。若单个文件超过 PowerShell
`Compress-Archive` 的适用范围，应改用 7-Zip 生成标准 ZIP，上传命令无需改变。

上传成功示例：

```json
{
  "uploadId": "1cc959a5-e9c8-4218-a021-fd8b7f11bd43",
  "inputPath": "uploads/1cc959a5e9c84218a021fd8b7f11bd43/model.obj",
  "referenceLlaPath": null,
  "archiveBytes": 202,
  "extractedBytes": 179,
  "fileCount": 1
}
```

保存 `inputPath`；创建转换时使用它，不需要知道服务器的宿主机目录。

### 第三步：选择坐标模式和 LOD

三种坐标模式只能选择一种：

| 场景 | profile/字段 | 结果 |
|---|---|---|
| 只验证模型结构，暂时没有坐标 | `profile = local-test` | 本地坐标结果，不能直接定位到地球表面 |
| 已知经纬高 | `profile = industrial-jpeg` + `geoReference` | 按指定经纬高生成地理参考结果 |
| ZIP 含唯一 `reference_lla.json` | `profile = industrial-jpeg` + `referenceLlaPath` | 使用上传响应返回的参考文件路径 |

所有内置 profile 默认均为 **5 层 LOD**，即生成 `LOD-0` 至 `LOD-4`。只有请求显式覆盖
`overrides.lods = 2` 时才只生成 `LOD-0`、`LOD-1`：

| `lods` | 生成目录 | 建议用途 |
|---:|---|---|
| 2 | `LOD-0`～`LOD-1` | 快速接口冒烟 |
| 4 | `LOD-0`～`LOD-3` | 较快的模型效果验证 |
| 5（默认） | `LOD-0`～`LOD-4` | 正式转换 |

没有坐标时的快速验证请求：

```powershell
$request = @{
    inputPath = $upload.inputPath
    profile   = "local-test"
    overrides = @{
        lods           = 2
        maxTextureSize = 2048
    }
}
```

已知真实坐标时的正式请求：

```powershell
$request = @{
    inputPath = $upload.inputPath
    profile   = "industrial-jpeg"
    geoReference = @{
        latitude  = 33.62678591
        longitude = 117.00336389
        altitude  = 0
    }
    overrides = @{
        lods = 5
    }
}
```

如果上传响应带有 `referenceLlaPath`，也可以使用：

```powershell
$request = @{
    inputPath       = $upload.inputPath
    referenceLlaPath = $upload.referenceLlaPath
    profile         = "industrial-jpeg"
}
```

### 第四步：创建转换并保存 ID

执行上面选定的一种 `$request` 后：

```powershell
$job = Invoke-RestMethod `
    -Method Post `
    -Uri "$base/api/v1/conversions" `
    -ContentType "application/json" `
    -Body ($request | ConvertTo-Json -Depth 8)

$id = $job.id
Write-Host "转换 ID: $id"
```

不传 `outputPath` 时，服务自动使用 `jobs/{转换ID}`，不会与其他任务覆盖冲突。

### 第五步：查询进度直到结束

```powershell
do {
    Start-Sleep -Seconds 3
    $status = Invoke-RestMethod "$base/api/v1/conversions/$id/status"
    Write-Host "$($status.progress.percent)%  $($status.state)  $($status.progress.message)"
}
until ($status.state -in @("Succeeded", "Failed", "Canceled"))

if ($status.state -ne "Succeeded") {
    $status | ConvertTo-Json -Depth 8
    throw "转换失败：$($status.diagnostic)"
}
```

关闭客户端或 SSH 隧道不会取消已提交的转换。重新建立连接后，仍可使用相同 `$id` 查询。

### 第六步：查询和下载结果

查询完整文件清单：

```powershell
$result = Invoke-RestMethod "$base/api/v1/conversions/$id/result"
$result | ConvertTo-Json -Depth 8
```

直接供 Cesium/Mars3D 加载的入口：

```powershell
$tilesetUrl = "$base/api/v1/conversions/$id/result/tileset.json"
$tilesetUrl
```

下载并解压完整结果目录：

```powershell
$downloadDir = Join-Path $env:USERPROFILE "Downloads\obj-to-3dtiles-$id"
New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null

$resultZip = Join-Path $downloadDir "result.zip"
Invoke-WebRequest `
    "$base/api/v1/conversions/$id/result.zip" `
    -OutFile $resultZip

Expand-Archive `
    -Path $resultZip `
    -DestinationPath (Join-Path $downloadDir "tiles")
```

单独下载一个已知文件：

```powershell
Invoke-WebRequest `
    "$base/api/v1/conversions/$id/result/tileset.json" `
    -OutFile (Join-Path $downloadDir "tileset.json")
```

服务器上的实际持久化位置为：

```text
Windows 默认：%USERPROFILE%\obj-to-3dtiles-service-data
Linux 默认：  /var/lib/obj-to-3dtiles-service-data

input/uploads/<uploadId>/              上传并解压后的源模型
output/jobs/<conversionId>/            转换结果
state/jobs/<conversionId>.json         作业状态
state/logs/<conversionId>.log          转换日志
```

重建或删除容器不会删除这些 bind mount 数据。

## CLI 参考

```text
ModelConversion.Service                  启动 HTTP 服务（无参数等同于 serve）
ModelConversion.Service serve            启动 HTTP 服务
ModelConversion.Service convert ...      同步执行一次转换
ModelConversion.Service --help           显示帮助
```

Docker 下对应 `docker run --rm obj-to-3dtiles-service:local [serve|convert|--help] ...`。

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
| `--max-texture-size` | 128-16384 | 纹理边长上限 |
| `--texture-format` | Jpeg / Webp / Ktx2 | 纹理格式 |
| `--texture-quality` | 1-100 | JPEG/Webp 质量 |
| `--ktx2-quality` | 1-255 | KTX2/BasisU 质量 |
| `--strip-deep-bottom` | true/false（默认 true） | 剥离深层封底几何：摄影测量封洞产生的地下几何会渲染成灰色块并带偏高度回正，转换前剔除 |
| `--deep-bottom-min-drop` | 10-10000（默认 100 米） | 判定存在封底的最小落差；小于该值视为无封底直通（保护屋顶主导的城市模型） |
| `--deep-bottom-margin` | 0-1000（默认 2 米） | 剥离阈值向下保留一个直方图桶，避免数值误差且不残留浅层裙边 |

深层封底剥离的判定方法：顶点 Z 直方图（2m 桶）从峰值桶向下游走，桶密度跌破峰值 15% 处即地表带底；
带底距模型最低点超过 `--deep-bottom-min-drop` 才认定存在封底。剥离阈值 = 带底 − `--deep-bottom-margin`，
任一顶点低于阈值的面片被剔除，并压缩未引用顶点、重映射面索引，使 bounds 与 HLOD 只使用有效几何（源输入只读，剥离发生在任务专属副本上）。
护栏：剥离 0 张或超过 90% 面片时直接直通，宁可不剥也不毁坏模型。

### 退出码

| 码 | 含义 |
|---|---|
| 0 | 成功 |
| 2 | 参数/输入错误（缺参、未知参数、互斥、非法值、输入不存在、未知 profile） |
| 3 | 输出冲突（输出目录已存在） |
| 4 | 转换失败或超时 |
| 5 | tileset 校验失败 |
| 6 | 资源准入拒绝（内存档位不足、极低内存准入失败、纹理总量超硬上限，或运行中触发主动内存保护） |
| 7 | 临时磁盘预算不足（预计临时需求超过可用空间的安全预算） |
| 130 | 用户取消（Ctrl+C；首次请求取消，二次强杀） |

资源类退出码（6/7）表示任务在执行前或执行中被资源策略安全拦下，不会留下半成品输出；解决资源问题后可直接重试同一命令。

### 输出语义

- stdout 打印单调递增的阶段进度、Obj2Tiles 详细过程、最终 `tileset.json` 绝对路径、耗时与校验摘要；详细输出也会保留在 `<输出目录>.log`。HTTP 服务模式下，同样可通过 `docker compose logs -f obj-to-3dtiles-service` 查看实时转换日志。
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
docker run --rm -v D:\models:/data/input:ro -v D:\tiles:/data/output obj-to-3dtiles-service:local convert --input /data/input/obj1 --output /data/output/obj1 --lat 33.62678591 --lon 117.00336389 --alt 0
```

## HTTP API 参考

上传路径 `/api/v1/uploads`，转换路径 `/api/v1/conversions`，OpenAPI 3.0 契约：`GET /openapi/v1.json`。

### 上传模型包

```http
POST /api/v1/uploads
Content-Type: application/zip

<ZIP 二进制正文>
```

ZIP 必须且只能包含一个 `.obj`，可以同时包含 `.mtl`、纹理和 `reference_lla.json`。服务采用流式落盘，
校验压缩包大小、解压总量、文件数、压缩比、重复路径、目录穿越和符号链接，通过后原子发布到唯一目录。

```json
{
  "uploadId": "7b0e965b-188e-45e8-9f50-91b1e05d3b99",
  "inputPath": "uploads/7b0e965b188e45e89f5091b1e05d3b99/odm_textured_model_geo.obj",
  "referenceLlaPath": null,
  "archiveBytes": 123456789,
  "extractedBytes": 456789012,
  "fileCount": 22
}
```

### 可恢复上传会话（平台集成）

大模型包（可达 10GiB+）经单请求上传容易因网络中断整体重传。上传会话把同一 ZIP 按固定 **64MiB** 分片传输，
服务重启后凭持久化会话记录继续，平台侧只需保存 `uploadId` 与已完成分片列表：

```text
POST /api/v1/upload-sessions                          创建会话（幂等）
GET  /api/v1/upload-sessions/{uploadId}               查询会话（恢复与完成轮询）
PUT  /api/v1/upload-sessions/{uploadId}/parts/{n}     上传分片（二进制正文）
POST /api/v1/upload-sessions/{uploadId}/complete      完成上传（幂等转入 finalizing）
```

创建请求固定为：

```json
{
  "idempotencyKey": "obj-import-20260909-001",
  "fileName": "model.zip",
  "sizeBytes": 123456789,
  "sha256": "<整包 SHA-256 小写十六进制>",
  "partSizeBytes": 67108864
}
```

- 同一 `idempotencyKey + sha256` 重复创建返回原会话；同键不同摘要返回 `409 UPLOAD_SESSION_CONFLICT`；
- 分片编号从 1 开始，除末片外大小必须为 64MiB；`PUT` 要求 `Content-Length` 和 `X-Part-Sha256` 头，
  相同摘要重复上传幂等返回 `200`，不同摘要返回 `409`；
- `complete` 无请求体、幂等转入 `finalizing` 返回 `202`；后台有界流式合并、校验总大小/整包 SHA-256、
  与单请求上传共用同一套 ZIP 安全校验后原子发布；**只有轮询到 `completed` 才能读取 `inputPath/referenceLlaPath`**；
- 会话状态机：`uploading → finalizing → completed / failed`；超过 TTL（默认 24 小时）的 uploading 会话
  转为 `expired`，GET 统一返回 `404`，目录由后台清理回收；
- 会话接口错误体固定为 `{ "errorCode": "...", "message": "..." }`，`errorCode` 取值：
  `UPLOAD_SESSION_CONFLICT`（409）、`UPLOAD_PART_INVALID`、`UPLOAD_HASH_MISMATCH`、`UPLOAD_FINALIZE_FAILED`（后三者 400 或失败终态）。

会话响应（`GET` 与 `complete` 同结构）：

```json
{
  "uploadId": "…",
  "state": "completed",
  "partSizeBytes": 67108864,
  "partCount": 2,
  "completedParts": [1, 2],
  "expiresAt": "2026-09-10T08:00:00+00:00",
  "inputPath": "uploads/<id>/odm_textured_model_geo.obj",
  "referenceLlaPath": "uploads/<id>/reference_lla.json",
  "errorCode": null
}
```

### 服务健康与容量

```text
GET /api/v1/health
```

平台调度的容量事实来源（只读，不含宿主机绝对路径）：

```json
{
  "status": "ready",
  "queueDepth": 2,
  "runningJobs": 1,
  "maxConcurrentJobs": 1,
  "roots": { "inputReady": true, "outputReady": true, "stateReady": true },
  "storage": { "freeBytes": 53687091200, "reserveBytes": 10737418240 },
  "resourcePressure": "normal",
  "updatedAt": "2026-09-09T08:00:00+00:00"
}
```

- `status`：`not_ready`（Obj2Tiles 执行器或数据目录缺失）→ `degraded`（资源压力超软水位，或状态卷
  可用空间低于 `reserveBytes`）→ `ready`；
- `resourcePressure` 与转换 Worker 的内存水位门控同一口径：软水位 `throttled`、硬水位 `blocked`；
- `queueDepth/runningJobs` 直接来自作业仓储计数，与作业列表一致。

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
GET /api/v1/conversions/{id}/result         获取完整文件清单、单文件 URL 和整包 URL
GET /api/v1/conversions/{id}/result/{path}  读取单个 tiles 文件
GET /api/v1/conversions/{id}/result.zip     流式下载完整结果目录
```

- 仅 `Succeeded` 作业可访问；以该作业已发布输出目录为根解析相对路径，支持外部 tileset 子树、`.b3dm`、纹理等相对引用；
- 转换宣布成功前，服务一次性流式计算并原子持久化结果清单（`StateRoot/manifests/{id}.json`），`GET .../result`
  的 `files[]` 固定携带 `{path, bytes, sha256, url}`，查询不再重复扫描目录与哈希；本功能上线前完成的旧作业
  首次查询时惰性补建清单，补建失败回退为无摘要（`sha256=null`）的目录枚举，不影响可预览性；
- `GET .../result/{path}` 支持 HTTP Range：合法区间返回 `206` 及标准 `Content-Range`/`Accept-Ranges: bytes`，
  越界返回 `416`，供平台按清单断点续传；整包 `result.zip` 继续供人工下载与兼容调用；
- 所有结果响应在读期间持有进程内读租约，终态 TTL 清理不会删除正在下载的作业产物；
- 整包下载边读取文件边写 ZIP 响应，不把完整结果目录载入内存；
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
curl.exe -s http://127.0.0.1:8091/api/v1/conversions/<id>/result
curl.exe -sO http://127.0.0.1:8091/api/v1/conversions/<id>/result/tileset.json
curl.exe -o result.zip http://127.0.0.1:8091/api/v1/conversions/<id>/result.zip
```

## API 快速测试（PowerShell）

下面假设模型已经位于 `InputRoot`，使用 `local-test` 和两级 LOD 做一次快速冒烟。
完整的上传到下载流程请使用前面的 [Windows 远程完整使用教程](#windows-远程完整使用教程)。

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
| 结果文件 Range 越界或不可满足 | 416 |
| 上传会话字段非法（`UPLOAD_PART_INVALID`）/ 分片摘要不符（`UPLOAD_HASH_MISMATCH`） | 400 |
| 上传会话幂等键或状态冲突（`UPLOAD_SESSION_CONFLICT`） | 409 |
| 上传会话不存在或已过期 | 404 |

## 配置

`server/ModelConversion.Service/appsettings.json` 的 `Conversion` 节，全部可用 `Conversion__*` 环境变量覆盖（如 `Conversion__MaxConcurrentJobs=2`）：

| 键 | 默认 | 说明 |
|---|---|---|
| `InputRoot` / `OutputRoot` / `StateRoot` | `/data/obj2tiles/input` `/data/obj2tiles/output` `/data/obj2tiles/state` | API 输入/输出/状态根目录（CLI 任意路径不受其约束） |
| `Obj2TilesExecutable` | `/app/obj2tiles/Obj2Tiles` | Obj2Tiles 可执行文件路径 |
| `MaxConcurrentJobs` | 1 | 并发转换数（1-8）。摄影测量模型为重内存任务，默认单并发 |
| `JobTimeoutMinutes` | 720 | 单作业超时，超时终止完整进程树 |
| `MaxTilesetJsonBytes` | 16777216 | 单个 tileset JSON 大小上限 |
| `TerminalRetentionHours` | 24 | 终态作业（成功/失败/取消）的输入、输出与状态保留时长，超时由内置清理工作器删除；`0` 表示立即清理（仅测试/演示） |
| `CleanupIntervalSeconds` | 300 | 终态作业与过期上传会话的清理扫描周期 |
| `StorageReserveBytes` | 10737418240 | `/api/v1/health` 的 `storage.reserveBytes`：状态卷可用空间低于该值时报 `degraded`（预警口径，不是硬门控） |
| `Profiles` | 见下 | 配置档字典 |

内置配置档：

| profile | Lods | 纹理上限 | 纹理格式 | Local | 用途 |
|---|---|---|---|---|---|
| `industrial-jpeg`（默认） | 5 | 2048 | JPEG | false | 生产基线，兼容性优先，要求地理参考 |
| `industrial-ktx2` | 5 | 2048 | KTX2 | false | KTX2 纹理降低显存，验证浏览器/显卡支持后再切 |
| `local-test` | 5 | 1024 | JPEG | true | 本地坐标结构测试 |

自定义档：在 `Profiles` 下加一节新名称即可（或 `Conversion__Profiles__myprofile__Lods=7` 环境变量），无需改代码。

上传限制位于 `Uploads` 配置节，可用 `Uploads__*` 环境变量覆盖：默认压缩包 20GiB、解压后
100GiB、最多 20000 个文件、相对路径最长 1024 字符、单文件最大压缩比 1000；`SessionTtlHours`（默认 24）
为可恢复上传会话的过期时长，过期会话 GET 返回 404 并由清理工作器回收目录。

### 临时数据生命周期

服务只持有临时数据，平台归档成果后由内置清理工作器兜底回收：

- **终态作业**：成功/失败/取消超过 `TerminalRetentionHours` 后，按 输出目录 → 服务管理输入（仅 `uploads/`
  命名空间，且无未过期作业引用时）→ 暂存/日志/结果清单 → 作业状态文件 的顺序删除；运行中、未到 TTL
  或结果正被下载（读租约）的作业绝不清理；用户手工放入 `InputRoot` 的目录（非 `uploads/` 前缀）永不删除；
- **上传会话**：`finalizing` 交给完成器收尾，其余状态超过 TTL 即删除会话目录（分片、合并包、状态），
  已发布输入由作业生命周期管理；

### 资源策略（Resources）

服务在每次转换前做资源预检：读取容器内存上限（cgroup v2/v1，非容器回退为进程可见内存）、CPU、临时磁盘空间，扫描输入 OBJ/MTL 与纹理头部，然后按内存档位计算执行计划。临时磁盘估算不足、内存低于极低档位下限、纹理解码总量超硬上限时**执行前安全拒绝**（CLI 退出码 6/7，API 作业 Failed 且 diagnostic 带 `[Kind]` 前缀）。

| 键（`Conversion__Resources__*`） | 默认 | 说明 |
|---|---|---|
| `SoftWatermarkRatio` / `ReduceWatermarkRatio` / `HardWatermarkRatio` | 0.65 / 0.75 / 0.85 | 内存水位：软水位暂停启动新任务；硬水位主动终止运行中转换（仅容器限制口径下强制，非容器只报告） |
| `StandardModeMinMemoryBytes` / `LowMemoryModeMinMemoryBytes` / `VeryLowMemoryModeMinMemoryBytes` | 8GiB / 4GiB / 2GiB | 标准 / 低内存（正式支持）/ 极低内存（受限准入）档位下限；低于极低档位直接拒绝 |
| `StandardModeMaxSourceTextureEdge` / `LowMemoryModeMaxSourceTextureEdge` / `VeryLowMemoryModeMaxSourceTextureEdge` | 4096 / 4096 / 2048 | 各档位源纹理边长上限（规范化副本，非输出图集规格） |
| `GcHeapRatio` | 0.5 | Obj2Tiles 受管堆预算占容器内存比例（`DOTNET_GCHeapHardLimit`） |
| `TextureCacheBudgetRatio` | 0.2 | 任务级纹理缓存预算占容器内存比例（按解码后字节计） |
| `StandardModeMaxStageConcurrency` / `LowMemoryModeMaxStageConcurrency` / `VeryLowMemoryModeMaxStageConcurrency` | 2 / 1 / 1 | 各档位重型阶段并发 |
| `TemporaryDiskEstimateMultiplier` / `TemporaryDiskBudgetRatio` | 3.0 / 0.5 | 临时磁盘需求估算倍率与可用空间预算比例 |
| `HardMaxTotalDecodedTextureBytes` | 48GiB | 纹理完整解码总量硬性上限，超过一律拒绝 |
| `MemoryWatchdogIntervalSeconds` | 5 | 运行中内存看门狗采样间隔（连续 2 次超硬水位才终止） |
| `SoftWatermarkRetrySeconds` | 30 | 软水位门控触发后重新排队前的等待秒数 |

预检报告（快照、输入摘要、档位与预算或拒绝原因）会写入任务日志与服务控制台，解释任务为什么被执行或拒绝。

## 前端查看器接入

- Mars3D/Cesium 直接使用作业详情中的 `tilesetUrl` 加载；
- 预览建议参数：`maximumScreenSpaceError=2`、动态屏幕空间误差、`cullWithChildrenBounds=false`；
- 服务默认允许任意 Origin、Header 和 Method，前端在其他域名或端口时可以直接调用 API 并加载瓦片。生产环境若需要访问控制，应由反向代理或网关限制来源并增加认证；
- ODM 的 OBJ 为 Z-up 坐标而 b3dm 按 Y-up→Z-up 轴校正加载：地理参考模式（`--reference-lla`/`--lat,--lon,--alt` 或 API 的 `referenceLlaPath`/`geoReference`）输出的根变换为 ENU 旋转，模型在 Cesium 中会"竖立"。需要在查看侧叠加 `Rx(-90°)` 修正（右乘根 transform、平移保持原值），或在做平台接入时统一处理；`--local` 输出为恒等变换，同理。

## 验证

公开仓库不包含测试模型、测试工程和测试产物。可通过镜像构建、CLI 帮助和健康检查验证运行环境：

```powershell
docker compose build
docker run --rm obj-to-3dtiles-service:local --help
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
当前版本会在 stdout/容器控制台显示阶段进度和 Obj2Tiles 详细过程，同时写入 `<输出>.log`。另开窗口执行 `Get-Content <输出>.log -Wait -Tail 20`（PowerShell），或使用 `docker compose logs -f obj-to-3dtiles-service` 跟随日志。

**创建 API 作业返回“输入文件不存在”？**
`inputPath` 不是宿主机绝对路径，而是服务 `InputRoot`（默认 `/data/obj2tiles/input`）下的相对路径。
不要自行拼接路径，优先使用 `POST /api/v1/uploads` 响应中的 `inputPath`。若人工排查，Linux 宿主机默认文件位于
`/var/lib/obj-to-3dtiles-service-data/input`，Windows 默认位于
`%USERPROFILE%\obj-to-3dtiles-service-data\input`。

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
- 不含对象存储/CDN；多实例部署时应把单机 bind mount 替换为共享存储，并由平台网关做会话路由或统一结果地址；
- 服务只保存临时数据：终态作业产物与上传会话按 TTL（默认 24 小时）自动清理，平台必须在清理前完成成果归档，不要把转换服务 URL 当作长期成果地址；
- 非 root 运行沿用 aspnet 基础镜像的 `$APP_UID` 模型；
- 源模型自身缺陷（如房檐异常、破洞、黑色拉伸面）会原样保留在输出中，不属于转换回归。

## 许可与商业使用

- 仓库原创服务代码、配置和文档适用根目录 [LICENSE](./LICENSE)：非商业使用免费，必须保留版权声明并注明项目名称、作者和仓库地址；商业使用必须通过 GitHub 账号或仓库 Issue 联系作者并取得单独书面授权。
- 这是带非商业限制的**源码公开许可证**，不是 OSI 定义下的开源许可证。
- `third-party/Obj2Tiles` 及本仓库对它的定制修改继续适用 AGPL-3.0，详见 [third-party/Obj2Tiles/LICENSE.md](./third-party/Obj2Tiles/LICENSE.md)，不受根目录商业限制重新许可。
- 第三方声明汇总见 [NOTICE](./NOTICE)。meshoptimizer 为 MIT，通过固定版本的 `Meshoptimizer.NET 1.0.7` NuGet 包在构建时引入，因此仓库不重复提交其源码和二进制；来源及完整许可证见 [third-party/meshoptimizer](./third-party/meshoptimizer/README.md)。相关许可证也会复制到 Docker 镜像的 `/app/licenses`。
