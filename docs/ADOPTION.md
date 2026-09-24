# LlmNpcServer 도입 가이드

게임 서버 옆에 NPC 행동 결정 서버를 두는 팀을 위한 시작 순서다. 구현 상태와 측정의 근거는 [성능·한계 레퍼런스](reference_metrics.html)에 있다.

## 30초 판단

| 맞는 경우 | 도입 전에 다시 생각할 경우 |
|---|---|
| 시간대·지역 상태·기후에 따라 다르게 행동하는 NPC가 많다 | 모든 NPC를 사람이 직접 정한 장면대로만 움직여야 한다 |
| 게임 서버가 NPC 명령의 결과를 이벤트로 돌려줄 수 있다 | 게임 서버와 별도 프로세스의 통신을 허용할 수 없다 |
| 콘텐츠 팀이 직업·장소·행동을 데이터로 관리한다 | 게임 서버가 NPC 경로와 충돌까지 이 서버에 맡기려 한다 |

이 프로젝트는 기술 검증 구현체다. 상용 투입 결정에는 [미측정·미구현 목록](reference_metrics.html)을 별도로 검토해야 한다.

## 역할과 준비물

| 담당 | 먼저 하는 일 | 읽을 문서 |
|---|---|---|
| 콘텐츠 | 직업, 장소, 아이템, 폴백 행동을 정의한다 | [마스터데이터](reference_masterdata.html), [Studio](npc_studio_manual.html) |
| 게임 서버 | 게임 이벤트를 보내고 명령별 결과를 회신한다 | [연동 규약](reference_link.html), [파이썬 예제](../samples/python_gs/README.md) |
| LLM | 사용할 엔진, 비용 상한, 플랜 검수 절차를 정한다 | [LLM 안내](llm/README.md), [측정치](reference_metrics.html) |
| 운영 | 상태 저장, 인증, 경보, 배포 경로를 정한다 | [문서 허브](index.html), [시크릿](security/secrets.md) |

## 1일차: 예제로 체험한다

저장소 루트에서 다음 순서로 실행한다. Windows, Linux, macOS에서 서버와 CLI는 .NET 10 SDK가 필요하다. GUI 뷰어는 Windows 전용이다.

```powershell
dotnet build -c Release
dotnet run -c Release --project src/Npc.Host -- doctor
dotnet run -c Release --project src/Npc.Host -- --loopback --npcs 100 --time-scale 600 --days 1 --no-llm
```

실행 중 `http://127.0.0.1:5080/dashboard`에서 지도와 NPC 상태를 본다. 콘텐츠를 편집하려면 `dotnet run --project tools/Npc.Studio -- --masterdata masterdata`를 실행한다. 시연 스크립트는 `testbed/run_demo.ps1`이다.

## 우리 세계로 옮긴다

`dotnet run --project tools/Npc.Cli -- init <새 폴더>`로 60 NPC 템플릿을 만든다. `npc validate --masterdata <새 폴더>`로 검사하고 Studio에서 직업·장소를 늘린다. 생성기가 고정된 저장소 마을을 쓰지 않도록 `tools/gen_npcs.cs`와 `tools/gen_poi_distances.cs`에 `--masterdata <새 폴더>`를 줄 수 있다.

| 우리 게임 개념 | 이 서버 개념 | 처음 정할 것 |
|---|---|---|
| 지역·인스턴스 | zone, shard | 인접 관계·수용량·게임 서버 담당 범위 |
| 집·일터·상점·사냥터 | POI | 역할·좌표·정원·열린 시간 |
| NPC 직업 | archetype | 허용 액션·인구 비중·폴백 계획·근무 시간 |
| 채집물·소지품 | item, recipe | 코드·생산 시간·입출력 |
| 한 행동 | action | 전제조건·효과·명령·응답 이벤트. 액션은 40개 이하다 |

버킷 축은 시간대 6 × 지역 상태 4 × 기후 3으로 고정돼 있다. 계절을 기후 축에, 던전 위험도를 지역 상태에 매핑할 수 있지만 기존 enum의 뜻을 문서로 명시해야 한다. POI 역할 10종도 고정돼 있다. 예를 들어 선술집은 휴식·사교 장소로 매핑한다. `npc explain core`가 엔진이 이름으로 참조하는 플래그·POI 역할·액션을 보여 준다. `world_flags.json`은 서버 빌드에 쓰인 것과 일치해야 한다.

## 게임 서버와 잇는다

```powershell
dotnet run --project tools/Npc.Cli -- export link-bundle --npcs 300 --out bundle.json
python samples/python_gs/mini_gs.py --bundle bundle.json --port 7010 --time-scale 60
dotnet run -c Release --project src/Npc.Host -- --link tcp --gs-port 7010 --npcs 300 --time-scale 60 --days 0
```

첫 단계에서는 [명령별 응답 표](reference_link.html)와 번들의 해시·로스터 순서를 그대로 따른다. 별도 터미널에서 `dotnet run --project tools/Npc.Conformance -- --host 127.0.0.1 --port 7010 --npcs 300 --time-scale 60 --seconds 60 --probe`를 실행해 C1~C7 판정을 본다. 예제에서 근접과 전투 판정은 미구현이므로 C4·C5는 미판정이다. 운영 전에는 인증·TLS, 재접속, 샤드 소유권을 적용한다.

## 플랜을 굽고 검수한다

버킷 수는 `아키타입 수 × 72`다. 기존 마을 2,880건 전체 굽기의 실측 비용 $5.12를 단순 비례하면 버킷당 약 $0.0018이다. 예를 들어 20 직업이면 1,440 버킷, 전체 굽기 약 $2.6이다. 기존 마을의 도달 집합은 전체의 9.2%였으므로 같은 비율을 외삽하면 약 $0.24다. **이 비율은 세계마다 달라지며 비용 보장이 아니다.** 최신 단가와 재시도율을 다시 측정한다.

LLM 엔진은 [연결 안내](llm/README.md)에 따라 설정한다. `Npc.Prebake`로 후보를 만들고 `npc review`와 Studio로 검수한 뒤 `npc pin`으로 확정한다. 마스터데이터·프롬프트 변경 시 `npc diff`와 `npc regen --check`로 무효화 범위를 본다. 프롬프트 접두 해시가 바뀌면 이전 플랜 회차를 그대로 쓰지 않는다. 콘텐츠 작업 공수는 미측정이다. 실습서의 게임 서버 연결 경로 약 8시간은 실습 기준 하한이다.

## 운영 준비

Docker/Kubernetes 예제는 `deploy/`에 있다. `--profile service`, 스냅샷, 관리 API, `/status`·`/metrics`, 알림 웹훅, 킬스위치를 운영 환경에서 점검한다. API 키는 환경변수나 시크릿 저장소에 둔다. [시크릿 운영 안내](security/secrets.md)에 키 이름과 노출 방지 방법이 있다. 서버 옵션 전체는 [생성된 옵션 표](host_options.md)에 있다.

자체 포함 배포 zip은 .NET 10 SDK와 Python 3.9+가 있는 빌드 머신에서 `tools/publish.ps1 -Rid win-x64` 또는 `-Rid linux-x64`로 만든다. 라이선스를 확정해 `LICENSE`를 둔 뒤 실행해야 한다. 압축을 푼 사용자는 SDK 없이 [빠른 시작](../QUICKSTART.md)을 따른다.

## 상용 투입 전 남은 판단

[성능·한계 레퍼런스 §14](reference_metrics.html)에 미측정 항목이 있다. 법무 검토, 샤딩 2단계, 블라인드 품질 평가도 끝나지 않았다. 팀이 자신의 콘텐츠·게임 서버로 같은 측정을 재현한 후에 채택 여부를 결정한다.
