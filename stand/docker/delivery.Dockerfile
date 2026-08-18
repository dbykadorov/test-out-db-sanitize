FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Sanitize.Delivery/ src/Sanitize.Delivery/
RUN dotnet publish src/Sanitize.Delivery/Sanitize.Delivery.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Sanitize.Delivery.dll"]
