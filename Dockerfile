# --- Web frontend build ---
FROM node:22-slim AS web-build
RUN apt-get update && apt-get install -y --no-install-recommends zip && rm -rf /var/lib/apt/lists/*
WORKDIR /web
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci
COPY src/web/ .
RUN npm run build && cd dist && zip -r ../wwwroot.zip .

# --- Baileys bridge dependencies ---
FROM node:22-slim AS baileys-build
WORKDIR /baileys
COPY src/agent/OpenAgent.Channel.WhatsApp/node/package.json src/agent/OpenAgent.Channel.WhatsApp/node/package-lock.json ./
RUN npm ci --omit=dev

# --- .NET build ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/agent/ .
# Stage the React zip into the OpenAgent project so it gets embedded as a resource
COPY --from=web-build /web/wwwroot.zip OpenAgent/wwwroot.zip
RUN dotnet restore
RUN dotnet build -c Release --no-restore -p:SkipNpmInstall=true
RUN dotnet test -c Release --no-build --no-restore

# Publish
FROM build AS publish
RUN dotnet publish OpenAgent -c Release --no-build -o /app/publish

# --- Runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=publish /app/publish .

# Install Node.js for Baileys bridge, plus Python tooling for shell_exec workflows
RUN apt-get update && apt-get install -y --no-install-recommends \
    nodejs python3 python3-pip jq curl git \
    && rm -rf /var/lib/apt/lists/* \
    && ln -sf /usr/bin/python3 /usr/bin/python \
    && pip install --break-system-packages --no-cache-dir requests garminconnect

# Copy Baileys bridge script and dependencies
COPY --from=baileys-build /baileys/node_modules /app/node/node_modules
COPY src/agent/OpenAgent.Channel.WhatsApp/node/baileys-bridge.js /app/node/
COPY src/agent/OpenAgent.Channel.WhatsApp/node/package.json /app/node/

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "OpenAgent.dll"]
