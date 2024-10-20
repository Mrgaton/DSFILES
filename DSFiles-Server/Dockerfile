FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env

WORKDIR /DSFiles-Server

# Copy only necessary files
COPY . ./

# Restore as distinct layers
RUN dotnet restore

# Build and publish a release, skipping restore
RUN dotnet publish -c Release -o out --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0


WORKDIR /DSFiles-Server

# Copy only published output
COPY --from=build-env /DSFiles-Server/out .

# Remove unnecessary runtimes (e.g., Windows, macOS)
RUN rm -rf ./runtimes/win* ./runtimes/osx* ./runtimes/unix*

# Set environment variables to reduce image size
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

# Expose the port
EXPOSE 8080

ENTRYPOINT ["dotnet", "DSFiles_Server.dll"]
