FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/OrasBackup.Cli -c Release -o /app --self-contained false

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine
RUN apk add --no-cache curl && \
    curl -sLO https://github.com/oras-project/oras/releases/download/v1.2.2/oras_1.2.2_linux_amd64.tar.gz && \
    tar -xzf oras_1.2.2_linux_amd64.tar.gz -C /usr/local/bin oras && \
    rm oras_1.2.2_linux_amd64.tar.gz

WORKDIR /app
COPY --from=build /app .
COPY docker-entrypoint.sh /app/
RUN chmod +x /app/docker-entrypoint.sh

VOLUME ["/data", "/config"]
EXPOSE 8080

ENTRYPOINT ["/app/docker-entrypoint.sh"]
