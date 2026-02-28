FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY Directory.Build.props Directory.Packages.props ./
COPY src/agent/OpenAgent3.sln src/agent/Directory.Build.props src/agent/Directory.Packages.props src/agent/
COPY src/agent/OpenAgent3.Api/OpenAgent3.Api.csproj src/agent/OpenAgent3.Api/
COPY tests/agent/OpenAgent3.Api.Tests/OpenAgent3.Api.Tests.csproj tests/agent/OpenAgent3.Api.Tests/
RUN dotnet restore src/agent/OpenAgent3.sln

# Copy everything and build
COPY . .
RUN dotnet build src/agent/OpenAgent3.sln -c Release --no-restore

# Run tests inside the build
RUN dotnet test src/agent/OpenAgent3.sln -c Release --no-build --no-restore

# Publish
FROM build AS publish
RUN dotnet publish src/agent/OpenAgent3.Api -c Release --no-build -o /app/publish

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=publish /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "OpenAgent3.Api.dll"]
