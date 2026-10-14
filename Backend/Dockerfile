# The accounts API image; the same image runs `migrate` (Compose service "migrate", profile tools).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ src/
COPY db/ db/
RUN dotnet publish src/Accounts.Api/Accounts.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# The base image sets ASPNETCORE_HTTP_PORTS=8080 and provides the non-root "app" user.
USER $APP_UID
ENTRYPOINT ["dotnet", "Accounts.Api.dll"]
CMD ["serve"]
