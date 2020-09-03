### https://docs.docker.com/engine/examples/dotnetcore/

FROM mcr.microsoft.com/dotnet/core/sdk:3.1 AS build-env
WORKDIR /app

# Copy csproj and restore as distinct layers
COPY CachingProxy/*.csproj ./
RUN dotnet restore

# Copy everything else and build
COPY CachingProxy/ ./
RUN dotnet publish -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/core/aspnet:3.1
WORKDIR /app
COPY --from=build-env /app/out .
ENTRYPOINT ["dotnet", "CachingProxy.dll"]
