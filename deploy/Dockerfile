# NPC 서버 이미지 (A-09).
#
# 멀티스테이지다 — SDK 는 빌드에만 쓰고 런타임 이미지에는 aspnet 만 남긴다.
#
# 이미지에 넣는 것:   masterdata/ · planstore/pinned/ · planstore/manifest.json
# 볼륨으로 두는 것:   planstore/plans/(생성물·재현 가능) · state/(A-01 스냅샷) · logs/
# 넣지 않는 것:       dotllm(GPLv3 — 별도 배포. CLAUDE.md §2.7) · models/(GGUF)
#
# 빌드:  docker build -f deploy/Dockerfile -t npc-server:dev .
#        (컨텍스트는 저장소 루트다)

ARG DOTNET_VERSION=10.0

# ── 빌드 ────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG VERSION=1.0.0
ARG REVISION=unknown
WORKDIR /src

# 복원을 먼저 캐시한다. 소스가 바뀌어도 패키지 그래프가 같으면 이 레이어는 재사용된다.
COPY Directory.Build.props Directory.Packages.props NpcServer.sln ./
COPY src/ ./src/
COPY tools/Npc.Prebake/ ./tools/Npc.Prebake/
COPY tools/Npc.Narrate/ ./tools/Npc.Narrate/
COPY testbed/Npc.TestBed.Protocol/ ./testbed/Npc.TestBed.Protocol/
COPY testbed/Npc.TestGameServer/ ./testbed/Npc.TestGameServer/
COPY masterdata/ ./masterdata/

RUN dotnet restore src/Npc.Host/Npc.Host.csproj

RUN dotnet publish src/Npc.Host/Npc.Host.csproj \
        -c Release \
        -o /app \
        --no-restore \
        -p:PublishReadyToRun=true \
        -p:VersionPrefix=${VERSION} \
        -p:SourceRevisionId=${REVISION}

# ── 런타임 ──────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime

# 비루트. 컨테이너가 뚫려도 호스트 볼륨을 함부로 못 쓴다.
RUN useradd --system --uid 10001 --create-home npc

WORKDIR /opt/npc

COPY --from=build /app ./
COPY masterdata/ ./masterdata/
COPY planstore/pinned/ ./planstore/pinned/
COPY planstore/manifest.json ./planstore/manifest.json

# plans/ 는 생성물이라 이미지에 넣지 않는다 (CLAUDE.md §6). 볼륨으로 마운트한다.
RUN mkdir -p ./planstore/plans ./state ./logs \
    && chown -R npc:npc /opt/npc

USER npc

# 프로브 전용 포트를 따로 연다 — 대시보드·관리 API 는 토큰 뒤로 가고(A-06)
# 프로브만 무인증으로 남아야 한다.
EXPOSE 5080 5081

VOLUME ["/opt/npc/planstore/plans", "/opt/npc/state", "/opt/npc/logs"]

HEALTHCHECK --interval=10s --timeout=3s --start-period=30s --retries=3 \
    CMD ["/opt/npc/healthcheck.sh"]

COPY deploy/healthcheck.sh /opt/npc/healthcheck.sh

ENTRYPOINT ["dotnet", "/opt/npc/Npc.Host.dll"]

# 기본은 서비스 프로파일이다 — --days 1 기본값이 24분 뒤 exit 0 하는 함정을 컨테이너에
# 가져오지 않는다 (A-02).
CMD ["--profile", "service", "--link", "tcp", "--bind", "0.0.0.0", "--health-port", "5081"]
