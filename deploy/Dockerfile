# syntax=docker/dockerfile:1
# Windows Bridge relay — single-instance production image.
# Build on a machine with Docker; this VM has none, so the image
# has NOT been built here (see deploy/README.md).
# The relay is a plain ASP.NET Core app: it listens on
# http://127.0.0.1:5000 inside the container, Caddy terminates TLS.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Bridge.sln ./
COPY src/Bridge.Relay/Bridge.Relay.csproj src/Bridge.Relay/
COPY src/Bridge.DeviceChannel/Bridge.DeviceChannel.csproj src/Bridge.DeviceChannel/
COPY src/Bridge.RelayService/Bridge.RelayService.csproj src/Bridge.RelayService/
RUN dotnet restore src/Bridge.RelayService/Bridge.RelayService.csproj -r linux-x64
COPY src/ src/
RUN dotnet publish src/Bridge.RelayService/Bridge.RelayService.csproj \
    -c Release -r linux-x64 --self-contained \
    -m:1 --disable-build-servers \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS final
# Non-root user; the data volume is owned by this uid.
RUN adduser --system --uid 10001 --group bridge
WORKDIR /app
COPY --from=build /app/publish ./
RUN chown -R bridge:bridge /app
USER bridge
ENV ASPNETCORE_ENVIRONMENT=Production
ENV RelayService__Urls=http://127.0.0.1:5000
ENV RelayService__DatabasePath=/data/relay.db
EXPOSE 5000
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget -qO- http://127.0.0.1:5000/healthz || exit 1
ENTRYPOINT ["./Bridge.RelayService"]
