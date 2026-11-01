# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b AS base
USER root
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081


# Build the Tailwind CSS bundle from source so Docker/CI does not depend on
# a locally generated wwwroot/css/auth.css artifact.
FROM node:24-bookworm-slim@sha256:235600a8101ab264e117b1768e925532262668dc9b581ef1dd7d96ced463b8e7 AS assets
WORKDIR /src/app/Auth/Auth
COPY ["app/Auth/Auth/package.json", "./"]
COPY ["app/Auth/Auth/package-lock.json", "./"]
RUN npm ci
COPY ["app/Auth/Auth/Styles/", "./Styles/"]
COPY ["app/Auth/Auth/Pages/", "./Pages/"]
COPY ["app/Auth/Auth/wwwroot/js/", "./wwwroot/js/"]
RUN npm run build:css && test -s ./wwwroot/css/auth.css


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Copy the complete repository graph before restore so new project references cannot drift from this image.
COPY . .
RUN dotnet restore "./app/Auth/Auth/Auth.csproj"
COPY --from=assets /src/app/Auth/Auth/wwwroot/css/auth.css /src/app/Auth/Auth/wwwroot/css/auth.css
WORKDIR "/src/app/Auth/Auth"
RUN dotnet build "./Auth.csproj" -c $BUILD_CONFIGURATION --no-restore

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Auth.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-build --no-restore

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Auth.dll"]
