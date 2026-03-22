# --- Web frontend build ---
FROM node:22-slim AS web-build
WORKDIR /web
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci
COPY src/web/ .
RUN npm run build

# --- .NET build ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/agent/ .
RUN dotnet restore
RUN dotnet build -c Release --no-restore
RUN dotnet test -c Release --no-build --no-restore

# Publish
FROM build AS publish
RUN dotnet publish OpenAgent -c Release --no-build -o /app/publish

# Copy web build output into wwwroot
COPY --from=web-build /web/dist /app/publish/wwwroot

# --- Runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=publish /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "OpenAgent.dll"]
