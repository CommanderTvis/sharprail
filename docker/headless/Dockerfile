# syntax=docker/dockerfile:1
# The headless SharpRail image: `sharprail-headless mcp` over stdio. The contract is in src/SharpRail.Host.Headless/SPEC.md.
#
# sharprail-headless is a NativeAOT binary (NativeAot.props). Linux links with the pinned Zig against glibc 2.31, so every
# publish runs under scripts/mise, from a Linux or a macOS host. CI publishes outside Docker, once per architecture, and
# builds the default target (prebuilt):
#   sh scripts/mise x -- dotnet publish src/SharpRail.Host.Headless -c Release -r linux-<arch> -o artifacts/headless/linux-<arch>
#   docker build -f docker/headless/Dockerfile --build-arg PUBLISH_DIR=artifacts/headless/linux-<arch> -t sharprail .
# For local use from a checkout, the "local" target publishes inside the SDK image instead, through the same scripts/mise:
#   docker build -f docker/headless/Dockerfile --target local -t sharprail .
#
# Base images are pinned by digest (the multi-architecture index, so amd64 and arm64 stay in step). To update one, read the
# new digest of its tag (`docker buildx imagetools inspect <image>:<tag>`) and replace the digest; the headless check fails
# on a base image without one.

FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS publish
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) echo "unsupported architecture $TARGETARCH" >&2; exit 1 ;; esac \
    && sh scripts/mise x -- dotnet publish src/SharpRail.Host.Headless -c Release -r "$rid" -o /out

# clangd for linux/arm64. clangd/clangd publishes no arm64 build; LLVM's release has one inside its 1.1 GB toolchain archive, too big to
# download at run time. This stage downloads it once at build time, checks the pinned SHA-256 before unpacking, and keeps only bin/clangd and
# the builtin headers it reads from lib/clang/<major>/include. The URL and hash are the linux-aarch64 pin in ClangdPlugin.cs (a check compares them).
# The host copies the result into /cache/managed on first use (ArtifactInstaller.Preinstalled). On amd64 the stage is empty: the host downloads
# the clangd/clangd zip itself.
FROM ubuntu:24.04@sha256:fe81560990fae86dc4743202b2cb4d3df98d0bcf949173cecef15ce1127ef852 AS clangd
ARG TARGETARCH
ARG CLANGD_ARM64_URL=https://github.com/llvm/llvm-project/releases/download/llvmorg-23.1.5/LLVM-23.1.5-Linux-ARM64.tar.zst
ARG CLANGD_ARM64_SHA256=830ceb70edf63849ce66e97bf85310a3bf2bdd04c860a9f2fa4ef1560e290cef
RUN mkdir -p /out \
    && if [ "$TARGETARCH" = arm64 ]; then \
         apt-get update && apt-get install -y --no-install-recommends ca-certificates curl zstd \
         && curl -fsSL --retry 3 -o /tmp/llvm.tar.zst "$CLANGD_ARM64_URL" \
         && echo "$CLANGD_ARM64_SHA256  /tmp/llvm.tar.zst" | sha256sum -c - \
         && zstd -dc --long=30 /tmp/llvm.tar.zst | tar -x -C /out --strip-components=1 --wildcards '*/bin/clangd' '*/lib/clang/*/include' \
         && test -x /out/bin/clangd && ls /out/lib/clang/*/include/stddef.h \
         && printf '{"sha256":"%s","url":"%s"}' "$CLANGD_ARM64_SHA256" "$CLANGD_ARM64_URL" > /out/.sharprail-artifact.json \
         && rm -f /tmp/llvm.tar.zst; \
       fi

# The native binary still loads libicu (globalization) and libssl (HTTPS for installs) at run time, and the .NET SDK it
# downloads for language servers needs those and libstdc++ and zlib too. runtime-deps carries exactly that set.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble@sha256:9cab4d7d886226d47b5489ef9abf0544e2f109fde11a8acc190217610af26c00 AS base
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && (userdel --remove ubuntu 2>/dev/null || true) \
    && useradd --uid 1000 --home-dir /cache/home --no-create-home --shell /usr/sbin/nologin sharprail \
    && mkdir -p /cache/home /cache/managed /cache/state /workspace \
    && chown -R sharprail:sharprail /cache /workspace \
    && ln -s /opt/sharprail/sharprail-headless /usr/local/bin/sharprail-headless
COPY --from=clangd --chmod=a+rX /out/ /opt/sharprail/preinstalled/clangd/
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
ARG TARGETARCH
ARG PUBLISH_DIR=artifacts/headless/linux-${TARGETARCH}
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
