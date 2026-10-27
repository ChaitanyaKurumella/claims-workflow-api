FROM node:22-bookworm-slim AS frontend
WORKDIR /build/web
COPY web/package*.json ./
RUN npm ci
COPY web/ ./
RUN npm run format:check && npm test && npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS backend
WORKDIR /build
COPY global.json ./
COPY src/Claims.Api/Claims.Api.csproj src/Claims.Api/
RUN dotnet restore src/Claims.Api/Claims.Api.csproj
COPY src/ src/
COPY --from=frontend /build/web/dist/dashboard/browser/ src/Claims.Api/wwwroot/
RUN dotnet publish src/Claims.Api/Claims.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
WORKDIR /app
COPY --from=backend /out/ ./
ENV ASPNETCORE_URLS=http://0.0.0.0:10000 \
    Claims__DataPath=/tmp/claimdesk/claims.db \
    Claims__DemoMode=false \
    Claims__SeedDemo=false
USER app
EXPOSE 10000
ENTRYPOINT ["dotnet", "Claims.Api.dll"]
