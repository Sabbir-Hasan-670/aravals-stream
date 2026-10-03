FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/AravalsStream.Relay/AravalsStream.Relay.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /out .
RUN mkdir -p /data && chown $APP_UID:$APP_UID /data
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV RELAY_STORAGE_PATH=/data/relay-state.json
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AravalsStream.Relay.dll"]
