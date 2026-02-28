FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY OpenAgent3.sln Directory.Build.props Directory.Packages.props ./
COPY OpenAgent3.Api/OpenAgent3.Api.csproj OpenAgent3.Api/
COPY OpenAgent3.Api.Tests/OpenAgent3.Api.Tests.csproj OpenAgent3.Api.Tests/
RUN dotnet restore

# Copy everything and build
COPY . .
RUN dotnet build -c Release --no-restore

# Run tests inside the build
RUN dotnet test -c Release --no-build --no-restore

# Publish
FROM build AS publish
RUN dotnet publish OpenAgent3.Api -c Release --no-build -o /app/publish

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=publish /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "OpenAgent3.Api.dll"]
