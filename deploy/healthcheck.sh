#!/bin/sh
# HEALTHCHECK 진입점 (A-09). aspnet 이미지에 curl 이 없으므로 .NET 으로 친다.
# /healthz/live 가 200 이면 0, 아니면 1.
set -e
exec dotnet /opt/npc/Npc.Host.dll healthcheck --url "http://127.0.0.1:${NPC_HEALTH_PORT:-5081}/healthz/live"
