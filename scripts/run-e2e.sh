#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
# Use the production static bundle and the same isolated demo mode as Render.
test -f web/dist/dashboard/browser/index.html || { echo 'Run npm run build in web first.'; exit 1; }
mkdir -p src/Claims.Api/wwwroot
cp -R web/dist/dashboard/browser/. src/Claims.Api/wwwroot/
export Claims__PublicDemo=true Claims__DemoMode=true
export ASPNETCORE_ENVIRONMENT=Production
exec dotnet run --no-launch-profile --project src/Claims.Api --configuration Release --urls http://127.0.0.1:5086
