FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY OpenAgent.sln ./
COPY OpenAgent.Models/OpenAgent.Models.csproj OpenAgent.Models/
COPY OpenAgent.Contracts/OpenAgent.Contracts.csproj OpenAgent.Contracts/
COPY OpenAgent.Store.InMemory/ConversationStore.InMemory.csproj OpenAgent.Store.InMemory/
COPY OpenAgent/OpenAgent.csproj OpenAgent/
COPY OpenAgent.Tests/OpenAgent.Tests.csproj OpenAgent.Tests/
RUN dotnet restore

# Copy everything and build
COPY . .
RUN dotnet build -c Release --no-restore

# Run tests inside the build
RUN dotnet test -c Release --no-build --no-restore

# Publish
FROM build AS publish
RUN dotnet publish OpenAgent -c Release --no-build -o /app/publish

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=publish /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "OpenAgent.dll"]
