$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$dataDirectories = @(
    (Join-Path $projectRoot 'data\input'),
    (Join-Path $projectRoot 'data\output'),
    (Join-Path $projectRoot 'data\state')
)
foreach ($directory in $dataDirectories) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

docker compose --file (Join-Path $projectRoot 'compose.yml') up --build -d
Write-Host '模型转换服务已启动：http://127.0.0.1:8091' -ForegroundColor Green
