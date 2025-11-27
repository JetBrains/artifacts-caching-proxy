### https://docs.docker.com/engine/examples/dotnetcore/

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
WORKDIR /app

# Copy csproj and restore as distinct layers
COPY CachingProxy/*.csproj ./
RUN dotnet restore

# Copy everything else and build
COPY CachingProxy/ ./
RUN dotnet publish -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build-env /app/out .
ENTRYPOINT ["dotnet", "CachingProxy.dll"]
