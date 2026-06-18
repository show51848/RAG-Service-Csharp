FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 先複製 csproj 並 restore，利用 Docker layer cache
# 只要 .csproj 沒有變動，NuGet restore 這層就不需要重跑
COPY ["src/RagService.Api/RagService.Api.csproj", "src/RagService.Api/"]
RUN dotnet restore "src/RagService.Api/RagService.Api.csproj"

COPY . .
WORKDIR "/src/src/RagService.Api"
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# 建立資料目錄
RUN mkdir -p /app/uploads /app/data /app/models

COPY --from=build /app/publish .

# 在 Container 內監聽 8080（對應 Python 版的 uvicorn --port 8000）
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "RagService.Api.dll"]
