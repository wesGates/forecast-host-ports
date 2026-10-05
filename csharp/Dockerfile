# The port as a container: built on Microsoft's .NET SDK image, run on the smaller
# runtime image. Build context is this directory (csharp/).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ForecastHost/ForecastHost.csproj ForecastHost/
RUN dotnet restore ForecastHost/ForecastHost.csproj
COPY ForecastHost/ ForecastHost/
RUN dotnet publish ForecastHost/ForecastHost.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "ForecastHost.dll"]
