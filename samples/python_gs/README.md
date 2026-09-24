# 파이썬 최소 게임서버

Python 3.10 이상과 표준 라이브러리만 사용한다. 저장소 루트에서 실행한다.

```powershell
dotnet run --project tools/Npc.Cli -- export link-bundle --npcs 300 --out samples/python_gs/bundle.json
python samples/python_gs/mini_gs.py --bundle samples/python_gs/bundle.json --port 7010 --time-scale 60
dotnet run -c Release --project src/Npc.Host -- --link tcp --gs-port 7010 --npcs 300 --time-scale 60 --days 0
```

세 번째 명령은 다른 터미널에서 실행한다. `--npcs`와 `--time-scale`은 양쪽이 같아야 한다.
파이썬 프로세스는 NPC 서버가 끊어져도 다음 연결을 받는다.

NPC 서버를 내린 뒤 같은 포트의 파이썬 서버에 적합성 키트를 붙인다.

```powershell
dotnet run --project tools/Npc.Conformance -- --host 127.0.0.1 --port 7010 --npcs 300 --time-scale 60 --seconds 60 --probe
```

`--probe`는 명령 3건을 실제로 보내 C6 응답도 판정한다. 게임 상태가 바뀔 수 있으므로 테스트 환경에서 실행한다. C4·C5는 이 예제가
근접과 전투를 생략하므로 미판정으로 보고된다.

이 예제는 로스터 스폰, 존 초기 상태, 틱, POI 이동, 상호작용과 기본 응답을 구현한다.
경로 탐색, 플레이어 근접, 전투 판정, 인증과 영속화는 구현하지 않았다.
월드의 실제 위치·재고·전투 규칙을 이 코드에 연결하면 게임서버 출발점이 된다.
인증을 요구하는 NPC 서버에는 이 예제 그대로 연결할 수 없다.

와이어 구조와 응답 종류의 기준은 [파이썬 참조 코덱](../../docs/wire/reference/npc_wire.py)과
[명령 응답 규약](../../docs/reference_link.html)이다.
