FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["WaveEngine.API/WaveEngine.API.csproj", "WaveEngine.API/"]
COPY ["WaveEngine.Application/WaveEngine.Application.csproj", "WaveEngine.Application/"]
COPY ["WaveEngine.Domain/WaveEngine.Domain.csproj", "WaveEngine.Domain/"]
COPY ["WaveEngine.Infrastructure/WaveEngine.Infrastructure.csproj", "WaveEngine.Infrastructure/"]
RUN dotnet restore "WaveEngine.API/WaveEngine.API.csproj"
COPY . .
RUN dotnet publish "WaveEngine.API/WaveEngine.API.csproj" -c Release -o /app/publish --no-restore

FROM base AS runtime
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "WaveEngine.API.dll"]
