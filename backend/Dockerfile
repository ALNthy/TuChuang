# 后端构建阶段（context 为项目根，COPY 路径相对根）
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 复制 csproj 并 restore（利用层缓存）
COPY backend/TuchuangApi.csproj ./backend/
RUN dotnet restore backend/TuchuangApi.csproj

# 复制源码并发布
COPY backend/ ./backend/
RUN dotnet publish backend/TuchuangApi.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- 运行阶段 ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app

# Magick.NET 在 Linux 上需要 libgdiplus 解码图像（RAW/ARW 转 JPEG 预览）
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libgdiplus \
        libc6-dev \
        ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

# 数据目录：数据库 + 上传文件
RUN mkdir -p /app/data /app/uploads
ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5000

ENTRYPOINT ["dotnet", "TuchuangApi.dll"]
