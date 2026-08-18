FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Sanitize.Service/ src/Sanitize.Service/
RUN dotnet publish src/Sanitize.Service/Sanitize.Service.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Sanitize.Service.dll"]
