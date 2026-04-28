FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/OrasBackup.Cli -c Release -o /app --self-contained false

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine

WORKDIR /app
COPY --from=build /app .
COPY docker-entrypoint.sh /app/
RUN chmod +x /app/docker-entrypoint.sh && \
    adduser -D -u 1000 orasbackup

VOLUME ["/data", "/config", "/scratch"]
ENV TMPDIR=/scratch
EXPOSE 8080
USER orasbackup

ENTRYPOINT ["/app/docker-entrypoint.sh"]
