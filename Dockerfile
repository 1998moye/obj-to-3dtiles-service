FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY third-party/Obj2Tiles/ /source/obj2tiles/
COPY NuGet.Config /source/NuGet.Config
RUN dotnet restore /source/obj2tiles/Obj2Tiles/Obj2Tiles.csproj -r linux-x64 --configfile /source/NuGet.Config
RUN dotnet publish /source/obj2tiles/Obj2Tiles/Obj2Tiles.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=true \
    -p:TrimMode=partial \
    --no-restore \
    --output /artifacts/obj2tiles

COPY server/ModelConversion.Service/ /source/server/ModelConversion.Service/
RUN dotnet restore /source/server/ModelConversion.Service/ModelConversion.Service.csproj --configfile /source/NuGet.Config
RUN dotnet publish /source/server/ModelConversion.Service/ModelConversion.Service.csproj \
    --configuration Release \
    --no-restore \
    --output /artifacts/service

COPY tools/HealthProbe/ /source/health/
RUN dotnet publish /source/health/HealthProbe.csproj \
    --configuration Release \
    --output /artifacts/health

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app/service
COPY --from=build /artifacts/obj2tiles/ /app/obj2tiles/
COPY --from=build /artifacts/service/ /app/service/
COPY --from=build /artifacts/health/ /app/health/
COPY LICENSE NOTICE /app/licenses/
COPY third-party/Obj2Tiles/LICENSE.md /app/licenses/Obj2Tiles-AGPL-3.0.md
COPY third-party/meshoptimizer/LICENSE-Meshoptimizer.NET.txt /app/licenses/
COPY third-party/meshoptimizer/LICENSE-meshoptimizer.txt /app/licenses/
RUN mkdir -p /data/input /data/output /data/state && chown -R "$APP_UID:$APP_UID" /data
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD ["dotnet", "/app/health/HealthProbe.dll"]
ENTRYPOINT ["dotnet", "/app/service/ModelConversion.Service.dll"]
# 默认启动 HTTP 服务；docker run <image> convert ... / --help 时覆盖为 CLI 模式。
CMD ["serve"]
