# Build context: this folder (medstock/). Built by the Supervisor on the Home Assistant host.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MedStock.Web/MedStock.Web.csproj MedStock.Web/
RUN dotnet restore MedStock.Web/MedStock.Web.csproj
COPY src/ .
RUN dotnet publish MedStock.Web/MedStock.Web.csproj -c Release -o /app --no-restore

# Ubuntu-based runtime: includes ICU (nl/en cultures) and tzdata (TZ from the Supervisor).
FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.title="MedStock"       org.opencontainers.image.description="Household medication stock tracker for Home Assistant"       org.opencontainers.image.source="https://github.com/joachimcarrein/MedStock"       org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8099 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
EXPOSE 8099
ENTRYPOINT ["dotnet", "MedStock.Web.dll"]
