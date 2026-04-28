#!/bin/bash
set -e

dotnet restore

# Start registry with delete enabled (for cleanup after tests)
docker rm -f registry 2>/dev/null || true
docker run -d -p 5000:5000 --name registry \
  -e REGISTRY_STORAGE_DELETE_ENABLED=true \
  registry:2

# Install oras CLI
curl -sLO https://github.com/oras-project/oras/releases/download/v1.2.2/oras_1.2.2_linux_amd64.tar.gz
tar -xzf oras_1.2.2_linux_amd64.tar.gz
sudo mv oras /usr/local/bin/
rm oras_1.2.2_linux_amd64.tar.gz

echo ""
echo "=== OrasBackup dev environment ready ==="
echo "  Registry: localhost:5000"
echo "  Run tests: dotnet test"
echo "  Nuke registry: docker restart registry"
echo ""
