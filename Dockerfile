# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS source
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props nuget.config ./
COPY JerryChart.Api/ JerryChart.Api/
COPY JerryChart.Data/ JerryChart.Data/
COPY JerryChart.Monitor/ JerryChart.Monitor/
COPY JerryChart.ServiceDefaults/ JerryChart.ServiceDefaults/

FROM source AS publish-api
RUN dotnet publish JerryChart.Api/JerryChart.Api.csproj \
    -c Release -warnaserror -o /out /p:UseAppHost=false

FROM source AS publish-monitor
RUN dotnet publish JerryChart.Monitor/JerryChart.Monitor.csproj \
    -c Release -warnaserror -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
USER $APP_UID

FROM runtime AS api
COPY --from=publish-api /out/ ./
ENTRYPOINT ["dotnet", "JerryChart.Api.dll"]

FROM runtime AS monitor
COPY --from=publish-monitor /out/ ./
ENTRYPOINT ["dotnet", "JerryChart.Monitor.dll"]
