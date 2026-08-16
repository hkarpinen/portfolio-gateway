FROM mcr.microsoft.com/dotnet/sdk:8.0 AS restore
WORKDIR /src

COPY Gateway.sln ./
COPY src/Gateway/Gateway.csproj src/Gateway/
RUN dotnet restore Gateway.sln

FROM restore AS build
COPY src/ src/
RUN dotnet build src/Gateway/Gateway.csproj -c Release

FROM build AS publish
RUN dotnet publish src/Gateway/Gateway.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=publish /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Gateway.dll"]
