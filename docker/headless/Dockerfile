# syntax=docker/dockerfile:1
# The headless SharpRail image: `sharprail-headless mcp` over stdio. The contract is in src/SharpRail.Host.Headless/SPEC.md.
#
# CI publishes the app outside Docker, once per architecture on a native runner, and builds the default target (prebuilt):
#   dotnet publish src/SharpRail.Host.Headless -c Release -r linux-<arch> --self-contained -p:PublishSingleFile=false -o artifacts/headless/linux-<arch>
#   docker build -f docker/headless/Dockerfile --build-arg PUBLISH_DIR=artifacts/headless/linux-<arch> -t sharprail .
# For local use from a checkout, the "local" target publishes inside the SDK image instead:
#   docker build -f docker/headless/Dockerfile --target local -t sharprail .

FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS publish
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) echo "unsupported architecture $TARGETARCH" >&2; exit 1 ;; esac \
    && dotnet publish src/SharpRail.Host.Headless -c Release -r "$rid" --self-contained -p:PublishSingleFile=false -o /out

# runtime-deps carries what a self-contained app needs (libicu, libssl, libstdc++, zlib), and the downloaded .NET SDKs need the same.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble AS base
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && (userdel --remove ubuntu 2>/dev/null || true) \
    && useradd --uid 1000 --home-dir /cache/home --no-create-home --shell /usr/sbin/nologin sharprail \
    && mkdir -p /cache/home /cache/managed /cache/state /workspace \
    && chown -R sharprail:sharprail /cache /workspace \
    && ln -s /opt/sharprail/sharprail-headless /usr/local/bin/sharprail-headless
ENV HOME=/cache/home \
    SHARPRAIL_TOOLCHAINS=managed \
    SHARPRAIL_MANAGED_CACHE=/cache/managed \
    SHARPRAIL_STATE_DIR=/cache/state \
    SHARPRAIL_ROOT=/workspace \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    GIT_CONFIG_COUNT=1 \
    GIT_CONFIG_KEY_0=safe.directory \
    GIT_CONFIG_VALUE_0=*
USER sharprail
WORKDIR /workspace
VOLUME /cache
ENTRYPOINT ["sharprail-headless"]
CMD ["mcp"]

FROM base AS local
COPY --from=publish --chmod=a+rX /out/ /opt/sharprail/

FROM base AS prebuilt
ARG PUBLISH_DIR=artifacts/headless/publish
COPY --chmod=a+rX ${PUBLISH_DIR}/ /opt/sharprail/

# Labels sit on the last stage, which is the default target. The local target is for development and carries none.
FROM prebuilt
ARG REVISION=unknown
ARG CREATED=unknown
LABEL org.opencontainers.image.title="sharprail-headless" \
      org.opencontainers.image.description="SharpRail without a window: language, spec and Blueprint tools over MCP on stdio, for one workspace" \
      org.opencontainers.image.source="https://github.com/CommanderTvis/sharprail" \
      org.opencontainers.image.licenses="PolyForm-Shield-1.0.0" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.created="${CREATED}"
