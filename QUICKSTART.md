# 배포 zip 빠른 시작

이 파일이 들어 있는 폴더에서 명령을 실행한다. .NET SDK 설치 없이 실행할 수 있는 자체 포함 배포다. 플랫폼에 맞는 zip 하나만 푼다.

## Windows

```powershell
.\bin\host\npc-server.exe doctor
.\bin\host\npc-server.exe --loopback --npcs 100 --time-scale 600 --days 1 --no-llm
```

## Linux·macOS

```sh
./bin/host/npc-server doctor
./bin/host/npc-server --loopback --npcs 100 --time-scale 600 --days 1 --no-llm
```

실행 중 `http://127.0.0.1:5080/dashboard`에서 NPC 지도를 본다. 마지막에 `판정: 정상`이 나오면 게임 속 하루를 완주한 것이다. 전체 옵션은 `npc-server --help-all`에서 본다.

새 세계는 `bin/cli/npc init <새 폴더>`로 만든다. `masterdata/`는 예제 세계고 `planstore/pinned/`는 사람이 확정한 플랜이다. API 키는 [시크릿 안내](docs/security/secrets.md)처럼 환경변수로만 둔다. dotLLM 실행 파일과 모델은 별도로 준비한다.
