FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
WORKDIR /DSFiles-Server

# Copy everything
COPY . ./
# Restore as distinct layers
RUN dotnet restore
# Build and publish a release
RUN dotnet publish -c Release -o out

ENV DOTNET_EnableDiagnostics=0

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /DSFiles-Server
COPY --from=build-env /DSFiles-Server/out .
ENTRYPOINT ["dotnet", "DSFiles_Server.dll"]