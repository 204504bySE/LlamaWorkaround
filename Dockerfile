# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY LlamaWorkaround.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish LlamaWorkaround.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# Listen on all interfaces inside the container (overrides Hosting:Urls in appsettings.json).
ENV HOSTING__URLS__0=http://0.0.0.0:12435
EXPOSE 12435
USER app
ENTRYPOINT ["dotnet", "LlamaWorkaround.dll"]
