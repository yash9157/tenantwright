# Dockerfile Project Closure

The service `.csproj` currently declares its Docker build context. Verify that value and the actual build command before editing; do not assume a context from another host.

## Compute the Closure

1. Read the host project.
2. Walk every `ProjectReference` recursively.
3. Read `global.json`, `Directory.Build.props`, `Directory.Packages.props`, and the nearest Dockerfile for framework and image tags.
4. Compare every pre-restore `COPY` entry with the recursive project closure.

```powershell
git grep -n "ProjectReference" -- services/<Service> shared orchestration
git grep -n -E "TargetFramework|DockerfileContext|mcr.microsoft.com/dotnet" -- global.json Directory.Build.props services/<Service>
git grep -n -E "docker build|buildx" -- scripts charts
```

## Multi-Stage Shape

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:<runtime-tag> AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:<sdk-tag> AS build
WORKDIR /src
COPY ["<project>.csproj", "<directory>/"]
# Repeat for every project in the verified recursive closure.
RUN dotnet restore "<host-project>.csproj"
COPY . .
RUN dotnet publish "<host-project>.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "<host-assembly>.dll"]
```

Use source-discovered values. Do not copy unrelated projects to hide reference drift.

## Verification

Build with the exact repository context, verify non-root and filesystem expectations, ports, health endpoint, configuration, and Helm image wiring. Container build success does not prove runtime health; a run or smoke check is separate and requires approval before starting processes.
