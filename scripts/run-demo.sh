#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
command -v dotnet >/dev/null || { echo 'Install .NET 8 SDK first.'; exit 1; }
command -v npm >/dev/null || { echo 'Install Node.js 22.12+ first.'; exit 1; }
(cd web && npm ci && npm run build)
mkdir -p src/Claims.Api/wwwroot
cp -R web/dist/dashboard/browser/. src/Claims.Api/wwwroot/
export Claims__AdministratorKey="$(openssl rand -hex 32)"
export Claims__ReviewerKey="$(openssl rand -hex 32)"
export Claims__SupervisorKey="$(openssl rand -hex 32)"
export Claims__DemoMode=true Claims__SeedDemo=true
export Claims__DataPath="${Claims__DataPath:-data/claimdesk-demo.db}"
exec dotnet run --project src/Claims.Api --urls http://localhost:5081
