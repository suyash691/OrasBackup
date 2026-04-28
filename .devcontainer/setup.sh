#!/bin/bash
set -e

dotnet restore

# Start registry with delete enabled (for cleanup after tests)
docker rm -f registry 2>/dev/null || true
docker run -d -p 5000:5000 --name registry \
  -e REGISTRY_STORAGE_DELETE_ENABLED=true \
  registry:2

echo ""
echo "=== OrasBackup dev environment ready ==="
echo "  Registry: localhost:5000"
echo "  Run tests: dotnet test"
echo "  Nuke registry: docker restart registry"
echo ""
