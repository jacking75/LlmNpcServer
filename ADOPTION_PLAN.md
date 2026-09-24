# 도입성 보강 계획 — "남이 가져다 쓰는 제품"으로 만들기

> 작성 2026-09-24 01:33 KST · 기준 커밋 `5f589a4` · .NET SDK 10.0.400 · Windows 11
>
> 근거는 추측이 아니라 이번 조사에서 **직접 돌린 것**이다 — 깨끗한 클론에서 README 를 그대로 따라 한
> 회차, `--link record` 트레이스 분석, 코드·문서 대조. 원자료는 [부록 C](#appendix-c).
>
> **작업 규칙.** 태스크 하나를 끝낼 때마다 **즉시** 체크박스를 `- [x]` 로 바꾸고 완료 일시(KST,
> 시스템 명령으로 확인)를 덧붙인다. 여러 개를 몰아서 체크하지 않는다. 각 태스크는 CLAUDE.md 의
> "변경 하나 = 커밋 하나"·"문서와 어긋나는 변경이면 같은 커밋에서 레퍼런스 HTML 도 고친다"를 따른다.
> 작업 로그 파일은 사용하지 않는다. 완료 근거와 시각은 각 태스크 항목에 남긴다.

## 태스크 목록

**P0 — 이것이 없으면 남이 쓸 수 없다**

- [ ] [T1. 라이선스를 정하고 명시한다](#t1) — 사람 결정 대기 (LICENSE 없음)
- [x] [T2. 스텝 마감을 예상 소요 시간에서 도출한다 — "500m 벽" 제거](#t2) — 완료 2026-09-24 09:16 KST
- [x] [T3. 명령별 응답 규약을 한 곳에 정의한다](#t3) — 완료 2026-09-24 09:16 KST
- [x] [T4. 지속형 액션의 시간을 NPC 서버가 센다 — "1틱 수면" 제거](#t4) — 완료 2026-09-24 09:16 KST

**P1 — 게임서버 연동을 끝까지 잇는다**

- [x] [T5. 게임서버 연동 번들을 내보낸다 — `npc export link-bundle`](#t5) — 완료 2026-09-24 08:49 KST
- [x] [T6. 파이썬 최소 게임서버 예제 — 적합성 키트 통과까지](#t6) — 완료 2026-09-24 08:49 KST

**P1 — 처음 온 사람이 5분 안에 가치를 본다**

- [x] [T7. 첫 실행 로그 · 종료 판정 · 실행 기준 폴더를 초심자용으로](#t7) — 완료 2026-09-24 09:16 KST
- [x] [T8. `Npc.Host doctor` — 한 번에 진단하고 다음 할 일을 말한다](#t8) — 완료 2026-09-24 09:16 KST
- [x] [T9. 대시보드 라이브 지도](#t9) — 완료 2026-09-24 08:49 KST
- [ ] [T10. 데모 플랜 팩](#t10) — ⚠ 사람 결정 + 비용 승인

**P1 — 문서를 줄이고 사실에 맞춘다**

- [x] [T11. 사용자 대면 문자열에서 내부 작업 ID 제거 · `--help` 그룹화 · 옵션 문서 생성](#t11) — 완료 2026-09-24 08:49 KST
- [ ] [T12. README 재구성 (≤ 250줄)](#t12)
- [x] [T13. 낡은·모순 문서 정리 + README 드리프트 테스트](#t13) — 완료 2026-09-24 09:33 KST
- [ ] [T14. HTML 문서를 GitHub 에서 읽히게 한다 (GitHub Pages)](#t14) — ⚠ 사람 결정

**P2 — 예제 마을이 아니라 "우리 게임"으로 옮겨 가는 길**

- [x] [T15. 엔진 코어 어휘 등록부 + 친절한 오류](#t15) — 완료 2026-09-24 09:03 KST
- [x] [T16. 최소 월드 템플릿 — `npc init`](#t16) — 완료 2026-09-24 09:03 KST
- [x] [T17. 도입 가이드 `docs/ADOPTION.md`](#t17) — 완료 2026-09-24 09:15 KST

**P2 — 설치와 LLM 연결**

- [x] [T18. LLM 엔진 연결을 쉽게 — OpenAI 키 · Ollama · LM Studio](#t18) — 완료 2026-09-24 09:14 KST (설정·병합 검증 완료, 실제 엔진 호출 미실시)
- [ ] [T19. 배포 산출물(자체 포함 zip) + 플랫폼 표기 정정](#t19) — ⚠ 릴리스 게시는 사람 결정

### 순서와 의존


| 태스크 | 먼저 끝나야 하는 것               | 이유                                                         |
| --- | ------------------------- | ---------------------------------------------------------- |
| T4  | T2 · T3                   | T2 가 만드는 `StepDeadlineTick` 을 쓰고, "즉시형 명령" 목록의 원천이 T3 의 표다 |
| T5  | T3                        | 번들에 응답 규약표를 싣는다                                            |
| T6  | T5                        | 파이썬 게임서버가 번들만 읽는다                                          |
| T12 | T8 · T9 · T11             | README 5분 체험이 `doctor`·지도 스크린샷·옵션 문서를 가리킨다                 |
| T13 | T12 · (T2 · T4)           | README 드리프트 테스트는 새 README 에 건다. 실습서 수치는 T2·T4 뒤에 다시 돈다     |
| T16 | T15                       | 템플릿이 코어 어휘와 빌드 일치를 지켜야 한다                                  |
| T17 | T5 · T6 · T15 · T16 · T18 | 가이드가 이것들을 가리킨다                                             |


**세션 묶음 제안** — A: T2 → T3 → T4 (런타임, 검증이 무겁다) · B: T5 → T6 · C: T7 → T8 → T9 ·
D: T11 → T12 → T13 · E: T15 → T16 → T18 → T17 · 결정이 나는 대로: T1 · T10 · T14 · T19

### 세션 시작 전에 사용자에게 받을 결정


| #       | 결정                | 선택지                                                                | 권장                                                             |
| ------- | ----------------- | ------------------------------------------------------------------ | -------------------------------------------------------------- |
| T1      | 저장소 라이선스          | Apache-2.0 · MIT · 비공개 전환                                          | **Apache-2.0** — 수정·상용 허용 + 특허 허여 명시. 사내 정책·법무 확인이 필요하면 그것이 선행 |
| T2 · T4 | 계약 **의미** 변경 승인   | ① `timeout_s` 를 "하한"으로, 마감은 예상 소요에서 도출 ② 즉시형 명령의 지속 시간은 NPC 서버가 센다 | 둘 다 승인 — 와이어·패킷은 안 바뀌고 레퍼런스 문서의 문장만 바뀐다                        |
| T10     | 데모 플랜 팩           | A 커밋 · B 릴리스 첨부 · C 안 함                                            | **A** (프리베이크 약 $0.37 — 비용 승인 필요)                               |
| T14     | GitHub Pages 켜기   | 켠다 · 안 켠다                                                          | **켠다** (`main` · `/docs`) — 공개 행위라 승인 필요                       |
| T19     | GitHub Release 게시 | 게시 · zip 만 만든다                                                     | zip 을 만든 뒤 결정                                                  |


---

## 1. 진단 — 질문에 대한 답

### 1.1 한 줄 결론

**기술 검증 구현체로서는 완성도가 높다. 그러나 남이 자기 게임에 가져다 쓰는 제품으로는 아직 아니다.**
막는 것은 코드 품질이 아니라 넷이다 —
① 라이선스가 없다 ② 참조 구현에서 이 서버의 첫 번째 약속("시간대에 맞는 하루 일과")이 깨져 있다
③ C# 이 아닌 게임서버가 붙는 길이 중간에 끊겨 있다 ④ 첫 실행에서 가치가 안 보이고, 문서가 너무 많고 일부가 사실과 다르다.

### 1.2 잘 된 것 — 보강하면서 깨지 않을 것

- 깨끗한 클론 → `dotnet build -c Release` **17초, 경고 0** (NuGet 캐시가 있는 기계 기준)
- README 빠른 시작이 **그대로 돈다** — NPC 500 · 게임 1일 · 틱 p99 **0.323ms** · 틱당 할당 **0B** · 링크 드롭 0
- 계약 N1\~N8 · 와이어 v2 · C++/파이썬 참조 코덱 · 골든 바이트 · 적합성 키트 — 연동의 **바닥 공사**는 이미 되어 있다
- Studio(JSON 없이 편집) · `npc` CLI(`--json` · `fix_hint`) · MCP — **콘텐츠 작성은 쉽다**
- 실습서 21장 + 실제로 돌아가는 예제 스크립트
- 미측정을 미측정이라 적는 문화 — 이 계획서도 그 규칙을 따른다

### 1.3 실용적인가 — 막는 것


| #   | 문제                            | 근거 (이번 조사)                                                                                                                                                                                                                                                                                  | 영향                                                                                                    | 태스크             |
| --- | ----------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------- | --------------- |
| 1   | **라이선스 없음**                   | `gh repo view` → `PUBLIC` · `licenseInfo: null`. README 끝 "본 저장소 자체의 라이선스는 사내 정책에 따른다"                                                                                                                                                                                                      | 읽을 수는 있어도 **복제·수정·배포할 권리가 없다**                                                                        | T1              |
| 2   | **지속형 액션이 1틱에 끝난다**           | 트레이스: NPC #1 이 06:24 에 `SetVisualState(Sleeping)` → **06:25 에 다시 일터로**. 같은 7스텝 루프가 24분 주기로 06:00\~08:40 사이 7회. `SimWorld.cs` 가 `SetVisualState`·`PlayAnimation`·`Speak`·`FaceTo` 에 즉시 완료를 돌려주고, 런타임에는 duration 을 보는 코드가 없다(`src/Npc.Runtime` 에서 `Duration` 0건). `Sleep` 명령에는 시간 정보가 실리지 않는다 | "밤엔 귀가해 아침까지 잔다"가 **성립하지 않는다**. NPC 한 마리가 게임 하루에 명령 약 600건. Studio "하루 재생"(소요 시간 반영)과 실제 서버가 다르게 움직인다 | T3 · T4         |
| 3   | **MoveTo 500m 벽**             | MoveTo `timeout_s 300` × `per_meter_s 0.6` = 500m. 월드는 x −1,939~~1,895 · z −1,592~~1,756m, **POI 쌍의 87 %가 500m 초과**. 트레이스: 500m 이상 이동은 거의 전부 도착 전에 타임아웃 ([T2 표](#t2))                                                                                                                       | 오류 주입 없는 회차에서도 **스텝의 13.5 %가 합성 타임아웃**(40,209 / 298,092). 먼 곳에 끝내 도착하지 못한다                            | T2              |
| 4   | **비-.NET 게임서버가 핸드셰이크를 못 맞춘다** | Hello 는 구조·내용·로스터 해시 셋을 요구하는데 **값을 얻는 명령도 알고리즘 문서도 없다**. `npc_wire.h`·`npc_wire.py` 는 해시 필드를 인코딩만 한다. 로스터는 `--npcs` 균등 간격 선택 공식(`NpcRoster.Select`)에 달렸다. 실습서 13장은 C# 라이브러리를 직접 불러 해시를 얻는다                                                                                                  | C++ 팀은 우리 C# 을 돌려 보기 전에는 **첫 연결도 못 한다**                                                               | T5 · T6         |
| 5   | **명령별 응답 규약이 표로 없다**          | `reference_link.html` §05 는 필드·의미만. "무엇을 언제 돌려주는가"는 `SimWorld`·`MovementSim`·`InteractionSim` 코드가 사실상의 사양                                                                                                                                                                                   | 게임서버 팀이 우리 C# 코드를 읽어야 한다                                                                              | T3              |
| 6   | **첫 실행에서 가치가 안 보인다**          | 깨끗한 클론: 버킷 19/2880(전부 pinned) · 캐시 히트율 **0 %** · `warn: 플랜 스토어가 낡았다 … Prebake 로 재생성` · 재계획 큐 395 적체                                                                                                                                                                                         | README 의 98.67 % 를 보려면 API 키와 비용이 든다. 첫 화면이 경고다                                                       | T7 · T10        |
| 7   | **자기 세계로 옮기는 길이 없다**          | 실습서는 예제 마을에 더하는 방식뿐. `world_flags.json` 은 **저장소 경로에서 빌드 시 enum 으로 컴파일**된다(`Npc.Core.csproj` 의 `WorldFlagsJson`). 런타임이 이름으로 아는 플래그 약 40종(`EventApplier` 28곳), POI 심볼 10개 고정(`PoiSymbol.TryParse`), 버킷 축 고정(enum 6·4·3), 생성기가 저장소 `masterdata/` 에 고정                                          | 도입자가 무엇은 바꿔도 되고 무엇은 못 바꾸는지 모른다. 어기면 알아볼 수 없는 빌드·로드 오류                                                 | T15 · T16 · T17 |


### 1.4 사용하기 쉬운가

**콘텐츠 작성은 쉽다. 처음 접하는 사람에게는 어렵다.**


| 문제                         | 근거                                                                                                                                                                                                                                                                                                       | 태스크       |
| -------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------- |
| 문서가 너무 많고 진입점이 여럿이다        | README 820줄 · `docs/index.html` 200KB · 책 13장 · 실습서 21장 · 레퍼런스 3 · LLM 팩 · FAQ · 기동 흐름 · 테스트 베드 안내 · Studio 매뉴얼. "읽는 순서"가 README 안에서만 세 번 나온다                                                                                                                                                            | T12 · T17 |
| 문서가 사실과 다르다                | README "MCP 서버 없음"(`tools/Npc.Mcp`·`.mcp.json` 있음) · "`repair`·`review` 아직 없다"(`npc --help` 에 있음) · "현재 상태: 구현 진행 중"(같은 파일 753행 "구현이 끝난 제품") · `docs/index.html` "TCP 골격 · P4 미착수 · 16개 중 9 통과 · 기준일 2026-07-27" · 실습서 목차 "3부부터 목차만"(21장 전부 본문 있음) · CLAUDE.md "TestClient 미착수" · GPU 요구가 12GB/8GB 로 갈린다 | T12 · T13 |
| GitHub 에서 문서가 안 읽힌다        | 공개 저장소의 핵심 문서가 전부 `.html` — GitHub 은 **소스를 보여 준다**                                                                                                                                                                                                                                                       | T14       |
| 내부 작업 ID 가 화면에 나온다         | `Npc.Host --help` 11곳(A-07·A-08·B-05·B-06·B-08·C-03·D-03·P6·T4-16·지운 문서 `docs/14 §2`) · `npc --help` 4곳(B-08·C-05·T22·T29) · 기동 로그 "평면 배치다 (C-03 이전)"                                                                                                                                                    | T11       |
| 옵션이 평면으로 64개               | `Npc.Host --help` 73줄, 그룹 없음                                                                                                                                                                                                                                                                             | T11       |
| 종료 요약이 판정을 안 한다            | `timeouts 40209 · interrupts 500 · replan-q 500 · llm 0` — 정상인가? 대답이 없다                                                                                                                                                                                                                                  | T7        |
| `dotnet run` 의 작업 폴더가 엉뚱하다 | `launchSettings.json` 때문에 스냅샷이 `src/Npc.Host/state` 에 쌓이고 환경이 `Development`                                                                                                                                                                                                                              | T7        |
| 받아서 바로 못 쓴다                | 릴리스·태그 0, 이미지 미게시, 소스 빌드만. OS 표기는 "Windows" — 실제로는 WinForms 뷰어만 Windows 전용                                                                                                                                                                                                                               | T19       |
| LLM 을 붙이는 흔한 길이 없다         | 엔진 15개가 dotLLM·llama.cpp·OpenRouter·Poe·Gemini 뿐 — OpenAI 키·Ollama 예가 없다. 설정 주석이 지운 문서(`TASKS.md`·`docs/10`)와 "사용자 지시"를 말한다                                                                                                                                                                              | T18       |
| "왜 안 되지"에 답하는 곳이 흩어져 있다    | `npc validate` · `npc regen --check` · `/status` · `samples/ch16_tier/check-engines.ps1`                                                                                                                                                                                                                 | T8        |


### 1.5 필요한 것이라고 알 수 있는가 (가치 전달)

- 인포그래픽 4장(`docs/infographics/`)이 있는데 **어디에서도 쓰이지 않는다**
- 실제 동작 화면(스크린샷·GIF)이 저장소에 **하나도 없다**
- "우리 게임에 맞나"를 가르는 판단표가 FAQ Q2·Q3 에 묻혀 있다
- 비용·공수를 가늠할 산식이 없다 — 재료(요청 단가, 2,880건 $5.12, 도달 집합 9.2 %)는 `reference_metrics.html` 에 있다
- README 첫 화면의 상태가 모순이고, 로드맵 표에서 ❌ 가 먼저 보인다

→ T9(보여 준다) · T10(LLM 결과를 보여 준다) · T12(첫 화면) · T17(판단 재료)

---

## 2. 태스크 상세

<a id="t1">

</a>

### T1. 라이선스를 정하고 명시한다 — ⚠ 사람 결정

**목적** 공개 저장소를 남이 법적으로 쓸 수 있게 한다. LICENSE 가 없으면 기본값은 "모든 권리 보유"다.

**근거** `gh repo view jacking75/LlmNpcServer --json visibility,licenseInfo` → `PUBLIC`, `null`.

**결정할 것** (세션 시작 시 사용자에게 묻는다)


| 선택              | 의미                           | 비고                 |
| --------------- | ---------------------------- | ------------------ |
| Apache-2.0 (권장) | 수정·상용 허용, 특허 허여 명시, 변경 고지 의무 | 회사가 쓴 코드를 공개할 때 무난 |
| MIT             | 가장 단순                        | 특허 조항 없음           |
| 비공개 전환          | 저장소를 private 으로              | "다른 사람"이 사내 인원뿐이라면 |


저작권 줄의 주체(개인·회사)도 사용자에게 받는다. 법무 확인이 필요하면 `docs/legal/` 의 "법무 확인은 미실시" 표기를 유지한다.

**변경할 파일**


| 파일                       | 무엇                                                                     |
| ------------------------ | ---------------------------------------------------------------------- |
| `LICENSE` (신규)           | SPDX 공식 원문 그대로. 손으로 요약하지 않는다                                           |
| `README.md` 라이선스 절       | 저장소 라이선스 + "dotLLM 은 GPLv3 라 번들하지 않는다" + 모델 약관 링크                      |
| `Directory.Build.props`  | `<PackageLicenseExpression>` — `Npc.Cli` 가 `dotnet pack` 으로 도구 패키지가 된다 |
| (Apache-2.0 이면) `NOTICE` | 제3자 고지. `tools/sbom.ps1` 산출물로 NuGet 의존 라이선스 목록                         |


**검증** `dotnet build -c Release` 경고 0 · `dotnet pack -c Release tools/Npc.Cli` 의 nuspec 에 license 표기 · 푸시 후 `gh repo view --json licenseInfo` 가 선택한 라이선스를 보고.

**완료 기준** 루트에 LICENSE 가 있고, README 와 패키지 메타데이터가 같은 라이선스를 말한다.

**커밋** `legal: 저장소 라이선스를 <이름> 으로 명시했다`

---

<a id="t2">

</a>

### T2. 스텝 마감을 예상 소요 시간에서 도출한다 — "500m 벽" 제거

**목적** 게임서버가 정상적으로 일하고 있는데 NPC 서버가 먼저 포기하는 일을 없앤다. `timeout_s` 는
**명령 유실 방어**인데, 지금은 정상 이동까지 잘라 먹는다.

**근거 (재현)**

```powershell
dotnet run -c Release --project src/Npc.Host -- `
    --link record --trace trace.jsonl --npcs 100 --time-scale 600 --days 1 --no-llm --no-dashboard
python move_outcomes.py trace.jsonl      # 부록 A-1
```

`MoveTo(poi)` 를 "명령 시점의 NPC 위치 ↔ 목표 POI" 거리로 묶은 결과 (게임 약 15시간, 100마리):


| 거리            | 도착    | 무응답 → 합성 타임아웃 |
| ------------- | ----- | ------------- |
| 0 \~ 250m     | 5,991 | 0             |
| 250 \~ 500m   | 2,931 | 472           |
| 500 \~ 750m   | 55    | 908           |
| 750 \~ 1,000m | 20    | 867           |
| 1,000m 이상     | 0     | 1,291         |


- 원인: `actions.json` MoveTo `per_meter_s 0.6` · `default_timeout_s 300` → 500m 를 넘으면 도착 전에
`PlanExecutor.SynthesizeTimeout`(388\~410행)이 `ActionFailed(Timeout)` 을 합성한다. 폴백 플랜의 MoveTo 108개가 전부 `timeout_s: 300`
- 같은 병이 Interact 에도 있다: Sim 은 레시피 `duration_s × count` 로 완료하는데(`InteractionSim.WorkSecondsFor`, 레시피 120\~1,800s),
Craft 는 `timeout_s 1800` 에 `count` 상한 5 → 최대 9,000s
- **소요 시간의 원천이 둘이다.** `ActionDuration.Seconds`(동작 예측 · Studio 하루 재생 · DryRun)는 Work 600 · Craft 900 고정값,
Sim 은 레시피 값. `ActionDuration.cs` 주석 "여기가 유일한 구현이다"가 Interact 에서는 사실이 아니다

**설계 (권장) — `timeout_s` 를 하한으로 바꾼다**

```
마감(게임 초) = max(timeout_s, ceil(expected_s × 1.5) + 30)
expected_s    = ActionDuration.Seconds(...)      // 게임서버가 시간을 소유하는 액션만 (T3 표의 time_owner = game_server)
```

- `expected_s` 는 `ActionDuration` 하나에서 나온다. **Interact 계열은 레시피가 있으면 `duration_s × max(count, 1)`** 을
쓰도록 `ActionDuration` 을 고치고, Sim 의 `WorkSecondsFor` 가 이 함수를 부르게 바꿔 원천을 하나로 만든다
- 배수 1.5 와 가산 30s 는 이름 있는 상수로 두고 이유(Sim 지터 · 경로 우회)를 주석에 적는다
- **명령 유실 방어는 그대로다** — 응답이 끝내 안 오면 여전히 마감에 합성한다. 바뀌는 것은 마감 시각뿐
- 대안(비권장): 콘텐츠의 `timeout_s` 를 올린다 → 폴백 108곳 + LLM 이 굽는 모든 플랜 + 프롬프트 규칙을 고쳐야 하고,
프리픽스·ContentHash 가 바뀌어 **플랜 스토어 전량 무효**. 월드가 커질 때마다 다시 터진다

**변경할 파일**


| 파일                                                                                 | 무엇                                                                                                          |
| ---------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------- |
| `src/Npc.MasterData/ActionDuration.cs`                                             | Interact 계열에 레시피 반영 (기존 분기에 추가)                                                                             |
| `src/Npc.Sim/InteractionSim.cs`                                                    | `WorkSecondsFor` → `ActionDuration` 사용. 기본값(`Gather.base_s`) 경로 유지                                          |
| `src/Npc.Runtime/NpcStore.cs`                                                      | `long[] StepDeadlineTick` 추가 — **"지금 상태가 끝나는 틱"**. Waiting 에서는 타임아웃 마감, T4 에서는 Holding 종료                   |
| `src/Npc.Runtime/PlanExecutor.cs`                                                  | 발행 시점(`StepIssuedTick` 을 쓰는 187·354행 근처)에서 마감을 계산해 저장. `SynthesizeTimeout` 은 `tick >= StepDeadlineTick` 비교로 |
| `src/Npc.Runtime/NpcStoreSnapshot.cs` · `src/Npc.Host/Persistence/SnapshotFile.cs` | 새 배열 직렬화. `FormatVersion` **5 → 6** (옛 스냅샷은 기존 정책대로 시드 기동)                                                  |
| `docs/reference_link.html` §07 상관 규약과 타임아웃                                         | 마감 공식으로 정정                                                                                                  |
| `docs/reference_masterdata.html` actions 스키마                                       | `timeout_s`·`default_timeout_s` = **하한**                                                                    |
| `CLAUDE.md` §2.2                                                                   | "모든 플랜 스텝에 `timeout_s`" 옆에 하한의 뜻 한 줄                                                                        |


**틱 루프 제약 (CLAUDE.md §2.1)**

- `ActionDuration.Seconds` 는 `action.Param(...)`·`Enum.TryParse`·`EnumValues.Contains("run")` 을 부른다 — **틱 안에서 그대로 부르지 않는다.**
`PlanExecutor` 생성 시 액션 code·아이템 code 로 인덱싱하는 표를 미리 만든다:
`durationKind[]` · `baseSeconds[]` · `perMeterSeconds[]` · `runArgOrdinal[]`(뛰기 열거값의 ordinal, 없으면 −1) · `recipeSeconds[]`(0 = 레시피 아님)
- 이동 거리: `data.Pois.Distance(NpcStore.CurrentPoi[npc], 목표)` — 배열 조회. 목표는 `PoiBinder` 가 이미 바인딩한 값
- `CompiledStep`(16B, `CompiledStep_SizeIsBounded`)은 건드리지 않는다
- 결정론: 틱·마스터데이터 값만 쓴다

**테스트 (CLAUDE.md §5.1 — 실제 사고를 고정하는 것만)**

- 회귀 1건 (`TimeoutSynthesisTests`): "1,500m 떨어진 POI 로 가는 MoveTo 는 예상 도착 전에 타임아웃되지 않는다" — 주석에 이 계획서의 트레이스 표를 한 줄로
- 유실 방어가 살아 있는지는 기존 `TimeoutSynthesisTests` 가 이미 본다 — 깨지지 않으면 새로 쓰지 않는다
- `ActionDuration` 과 Sim 의 일치는 **Sim 이 그 함수를 부르게 만든 것**으로 보장한다. 둘을 비교하는 테스트는 쓰지 않는다(구현 복사본)
- Work·Craft 소요가 바뀌어 `NarrativeTests`·예측 테스트가 깨지면 **계약이 바뀐 것**이다 — 기대값을 고친다

**검증**

1. `dotnet build -c Release` 경고 0 · `dotnet test "--filter Category!=Golden&Category!=Gate&Category!=Load"` · `dotnet format --verify-no-changes`
2. 위 기록 명령 + 부록 A-1 → 500m 이상 구간의 "무응답"이 0 에 가깝다
3. `/status` 의 `timeoutsSynthesized / stepsAdvanced` 가 **13.5 % → 1 % 미만** (`--drop-rate 0`)
4. `--drop-rate 0.3` 에서 NPC 가 멈추지 않는다 — 합성이 여전히 돈다
5. 틱 루프를 건드렸다 → `dotnet test --filter Category=Load` 로 p99 ≤ 20ms · **`bytesPerTick` = 0**. `docs/measurements/W10_load.csv` 가
 덮어써지므로 `git diff` 로 확인하고 `dotnet run --project tools/Npc.Cli -- perf --check`
6. `dotnet test --filter Category=Determinism` 통과

**완료 기준** 오류 주입 없는 루프백 회차에서 합성 타임아웃이 스텝의 1 % 미만이고, 먼 POI 에 실제로 도착한다. 레퍼런스 문서가 같은 커밋에서 새 뜻을 말한다.

**커밋** ① `masterdata: 소요 시간의 원천을 ActionDuration 하나로 모았다 — 레시피 반영` ② `runtime: 스텝 마감을 예상 소요 시간에서 도출한다` (문서·스냅샷 형식 포함)

**파급** 실습서 1·2장 출력(`commands 11,825` · `timeouts 1,381`)과 `reference_metrics.html` 의 스텝·명령 수치가 바뀐다 → T13 에서 다시 돌려 갱신.

---

<a id="t3">

</a>

### T3. 명령별 응답 규약을 한 곳에 정의한다

**목적** 게임서버 팀이 우리 C# 을 읽지 않고도 "이 명령을 받으면 무엇을, 언제 돌려주는가"를 안다. 그리고 T4·T5·적합성 키트가 **같은 표**를 쓴다.

**근거** `reference_link.html` §05 명령 표는 필수 필드와 의미만 있다. 응답 이벤트·시점·시간 소유자는 `SimWorld.Handle`(354\~366행)·`MovementSim`·`InteractionSim` 에 흩어져 있다. 적합성 키트 C6 는 "응답이 오는가"만 본다.

**설계**

- 단일 원천: `src/Npc.Contracts/CommandResponses.cs` — `NpcCommandKind` 별 `(응답 이벤트 종류들, 시간 소유자 GameServer|NpcServer|Instant, 실패 사유 후보)` 정적 표.
외부 의존 0 이라 Contracts 규칙에 맞다. **먼저 `Contracts_*` 리플렉션 테스트가 패킷이 아닌 타입을 거르는지 확인**하고, 걸리면 `Npc.Core` 에 둔다
- 쓰는 곳: T4 즉시형 판정 · T5 번들 · 적합성 키트 C6(응답 종류가 표와 맞는가) · 레퍼런스 문서
- 초안 — Sim 동작에서 옮겼다. **구현 세션에서 `MovementSim` 의 추종(Follow)·배회(Wander) 처리를 확인해 "(확인)" 칸을 채운다**:


| 명령                                              | 게임서버가 할 일 | 돌려줄 이벤트                                                                                      | 언제                         | 시간 소유           |
| ----------------------------------------------- | --------- | -------------------------------------------------------------------------------------------- | -------------------------- | --------------- |
| Spawn                                           | NPC 생성    | `NpcSpawned`(corr)                                                                           | 즉시                         | —               |
| Despawn                                         | 제거        | `NpcDespawned`(corr)                                                                         | 즉시                         | —               |
| MoveTo(TargetPoi/Pos)                           | 경로 이동     | `NpcTransform`×N → `NpcArrived`(corr, Poi) · 실패 `NpcActionFailed(Unreachable)`               | 도착 시                       | 게임서버            |
| MoveTo(TargetNpc) 추종                            | 따라가기      | `NpcActionCompleted`(corr)                                                                   | (확인)                       | (확인)            |
| MoveTo(Zone+Amount) 배회                          | 반경 배회     | `NpcActionCompleted`(corr)                                                                   | (확인)                       | (확인)            |
| Interact                                        | 작업        | `NpcInventoryChanged`(corr) × 입력·산출 → `NpcActionCompleted`(corr) · 실패 `InsufficientResource` | 레시피 `duration_s × count` 뒤 | 게임서버            |
| InventoryChange                                 | 증감        | `NpcInventoryChanged`(corr) · 실패 `InsufficientResource`                                      | 즉시                         | —               |
| CombatAction                                    | 전투 판정     | `NpcActionCompleted` · `NpcActionFailed(Interrupted …)`                                      | 판정 뒤                       | 게임서버            |
| SetVisualState · PlayAnimation · Speak · FaceTo | 표시        | `NpcActionCompleted`(corr)                                                                   | **즉시**                     | **NPC 서버** (T4) |
| Stop · SetAggro                                 | 적용        | `NpcActionCompleted`(corr)                                                                   | 즉시                         | —               |


  명령과 무관한 주기 이벤트도 같은 절에 적는다: `TickSync` 매 틱 · `GameTimeChanged` 시간대 경계 · `ZoneStateChanged`/`WeatherChanged`(재동기화 1회 + 변경 시) · `PlayerProximity`(발행 규약 §11) · `NpcVitalsChanged`

**변경할 파일** `src/Npc.Contracts/CommandResponses.cs`(신규) · `docs/reference_link.html`(새 절 "명령별 응답 규약") · `tools/Npc.Conformance/Checks/`(C6 확장) · `docs/llm/RECIPES/connect-game-server.md`(링크)

**테스트**

- 드리프트: 레퍼런스 문서의 표 행 == 코드 표 (`LayoutDoc_MatchesTheCode` 처럼 문서에서 표를 읽어 대조)
- 검사기 자체 시험: C6 확장에 대조군 1건 — "틀린 이벤트 종류로 답하는 가짜 게임서버는 불합격" (`ConformanceCheckTests`)
- 대역이 표를 지키는가는 기존 `ConformanceBedTests`(대역 합격)가 본다 — 새 테스트 불필요

**검증** 빌드·CI 필터 테스트·format. `Npc.TestGameServer` 를 띄우고 `dotnet run --project tools/Npc.Conformance -- --host 127.0.0.1 --port 7010 --seconds 60` → 합격 유지. `docs/measurements/conformance_testbed.md` 변경분 확인.

**완료 기준** 게임서버 구현자가 `reference_link.html` 의 한 절로 모든 명령의 응답을 구현할 수 있고, 그 표가 코드·테스트로 묶여 있다.

**커밋** `contracts: 명령별 응답 규약을 표 하나로 정의했다` (+ 문서) · `conformance: C6 이 응답 종류까지 판정한다`

---

<a id="t4">

</a>

### T4. 지속형 액션의 시간을 NPC 서버가 센다 — "1틱 수면" 제거

**목적** "밤엔 집에 가서 아침까지 잔다", "근무 시간 동안 경비를 선다"가 실제로 그 시간만큼 지속되게 한다. **이 서버의 첫 번째 약속이다.**

**근거 (재현)** 부록 A-2 로 NPC #1 의 명령 열을 뽑았다 (`--time-scale 600`, 시작 06:00):

```
t=  21 (06:21) MoveTo    poi=29       ← 집
t=  24 (06:24) SetVisual Sleeping     ← "아침까지 잔다"
t=  25 (06:25) MoveTo    poi=55       ← 1틱 뒤 다시 일터로
t=  28 (06:28) Interact  item=20
…            같은 7스텝이 24분 주기로 반복 (06:00~08:40 사이 7회)
```

- `src/Npc.Sim/SimWorld.cs` 354\~366행이 `SetVisualState`·`PlayAnimation`·`Speak`·`FaceTo`·`Stop`·`SetAggro` 에 **즉시** `NpcActionCompleted` 를 낸다
- 이 명령들은 시간 정보를 싣지 않는다 — `Sleep` 의 `emits.map` 은 `{"Visual":"Sleeping"}` 뿐이라 게임서버는 언제 깨울지 알 수 없다
- 런타임에 duration 을 보는 코드가 없다 (`src/Npc.Runtime/*.cs` 에서 `Duration` 0건)
- 영향 액션 13종: Sleep(`until_time`) · Rest · Pray · Guard · Wait · Observe · Perform(`param`) · Bathe 300s · Gossip 180s · Talk 60s · CallForHelp 20s · Greet 15s · Emote 10s
- 결과: 500마리 게임 1일에 명령 **299,096건**(마리당 약 600건). Studio 하루 재생(`DayForecast`, 소요 시간 반영)과 서버의 실제 움직임이 다르다

**설계 (권장) — 시간은 NPC 서버가 소유한다. 게임서버 계약은 바꾸지 않는다**

- 규칙: 스텝이 내는 명령이 T3 표에서 **즉시형**(시간 소유 = NPC 서버)이면, 완료 이벤트를 받은 뒤 `ActionDuration` 만큼
새 상태 **`Holding`** 으로 머문다. 머무는 시간이 끝나면 스텝 경계로 넘어간다. 액션 이름을 코드에 박지 않는다(§2.4)
- `until_time`: 목표 시간대의 시작 시각까지 — `ActionDuration.UntilTime` 과 같은 공식. 현재 시각은 `GameClock.GameSeconds`
- **Holding 에는 진행 중인 명령이 없다 → 플랜 교체가 안전하다.** `PlanSwapper` 는 Holding 도 스텝 경계로 취급한다
(수면 중에도 버킷 전환·재계획 결과가 들어간다). 인터럽트는 지금처럼 즉시
- 대안 1(비권장): 명령에 지속 시간을 실어 게임서버가 센다 → 와이어 v2 확장 슬롯·계약 부 버전 변경 + 모든 게임서버 구현 부담.
NPC 서버 쪽 해결이 `reference_link.html` §01 "판단은 NPC 서버가 한다"와도 맞는다
- 대안 2: `actions.json` 에 `duration_owner` 필드 추가 — 액션 카탈로그가 프롬프트 프리픽스에 실리므로 **프리픽스 해시 변경 → 플랜 스토어 전량 무효**. 권장안은 데이터를 바꾸지 않는다

**변경할 파일**


| 파일                                                  | 무엇                                                                                             |
| --------------------------------------------------- | ---------------------------------------------------------------------------------------------- |
| `src/Npc.Runtime/NpcStore.cs`                       | `StepStatus.Holding = 6` — **뒤에만 추가**(값 재배치 금지). T2 의 `StepDeadlineTick` 을 Holding 종료 틱으로 쓴다   |
| `src/Npc.Runtime/PlanExecutor.cs`                   | Completed 처리에서 즉시형이면 Holding 전이 + 종료 틱 계산. `Step()` 에 `case Holding:` — 종료 틱이 되면 `AdvanceStep` |
| `src/Npc.Runtime/PlanSwapper.cs`                    | Holding 을 경계로 인정                                                                               |
| `src/Npc.Runtime/CognitionScheduler.cs`             | 이탈 판정이 Holding 을 "진행 중"으로 다루는지 확인 (수면 중 `IsSleeping` 등)                                        |
| `src/Npc.Host/Metrics/*` · `wwwroot/dashboard.html` | `byStepStatus` 의 Holding 칸(배열 8칸이라 여유 있음)과 표시 이름                                               |
| `src/Npc.Host/Persistence/SnapshotFile.cs`          | 새 상태 값이 복원되는지 (T2 에서 올린 형식 6 안에서)                                                              |
| `docs/reference_link.html` §05·§07                  | "즉시형 명령의 지속 시간은 NPC 서버가 센다"                                                                    |
| `docs/reference_masterdata.html` actions `duration` | kind 별로 누가 시간을 세는지                                                                             |


**틱 루프 제약** 종료 틱 계산은 T2 에서 만든 액션 code 인덱스 표를 쓴다. `until_time` 목표 시각(시간대 6개의 시작 시)은 생성 시 `int[6]` 로. 할당 0.

**테스트**

- 회귀 (사고 기록 = 이 계획서의 NPC #1 트레이스): "Sleep(until Morning) 은 Morning 이 될 때까지 다음 스텝을 내지 않는다"
- 경계: "Holding 중 들어온 버킷 전환은 그 틱에 반영된다"
- 인터럽트가 Holding 에서도 즉시 스왑되는지는 **기존 인터럽트 테스트를 Holding 상태에서 한 번 돌려 보고**, 이미 덮이면 새로 쓰지 않는다

**검증**

1. 빌드·CI 필터 테스트·format
2. 부록 A-2 로 NPC #1 추적 → 수면이 다음 시간대 시작까지 이어진다. 500마리 1일 명령 수 299,096 → **실측값을 적는다** (참고 목표: 1/10 이하)
3. `npc forecast archetype <그 NPC 의 아키타입>` 의 스텝 시각과 트레이스가 지터 범위에서 맞는다
4. Load(p99 · `bytesPerTick` = 0) · Determinism — T2 와 같음
5. `./testbed/run_demo.ps1 -Scenario day` 로 밤에 NPC 가 집에 머무는 것을 **눈으로** 본다 — 못 하면 "미실시"로 적는다

**완료 기준** 즉시형 액션이 정의된 시간만큼 지속되고, 게임 하루 동안 NPC 가 "일 → 귀가 → 아침까지 수면"을 한 번 산다.

**커밋** `runtime: 즉시형 액션의 지속 시간을 NPC 서버가 센다 (Holding)` — 문서 같은 커밋

**파급** `reference_metrics.html` 의 부하·스텝 수치, 실습서 1·2장 출력(T13), 골든 테스트(속성 단언이라 대개 무관 — 돌려서 확인). 부하 기준선 갱신이 필요하면 사람이 `npc perf --write-baseline --apply`.

---

<a id="t5">

</a>

### T5. 게임서버 연동 번들을 내보낸다 — `npc export link-bundle`

**목적** 어떤 언어로 짠 게임서버든 **파일 하나**만 읽으면 핸드셰이크를 맞추고 NPC 를 스폰할 수 있게 한다.

**근거**

- Hello 가 요구하는 값: 구조 해시 · 내용 해시 · 로스터 해시 (`TcpGameServerLink` 가 검증), 그리고 로스터 순서대로의 `NpcSpawned` 재발행
- 로스터 = `NpcRoster.Select(all, --npcs, --zone)` 의 **균등 간격 선택 공식** + SHA-256(순서 포함). 구조 해시는 "파일 바이트가 아니라 로드된 표에서" 계산(`StructuralHash.Compute`) — 다른 언어로 재구현하면 반드시 어긋난다
- `npc validate --json` 이 구조·내용 해시는 내지만 로스터 해시와 로스터 목록은 없다

**설계** `npc export link-bundle --npcs <N> [--zone a,b | --shard N] [--dynamic-roster] [--out <path>]` → JSON (snake\_case · UTF-8 · 한글 비이스케이프 — `validate --json` 과 같은 규칙):

```json
{
  "bundle_version": 1,
  "contract": { "major": 1, "minor": 3 },
  "tick_rate_hz": 10,
  "note": "Hello.timeScale 은 NPC 서버의 --time-scale 과 같아야 한다 — 실행 인자라 여기에 싣지 않는다",
  "hashes": { "structural": "<hex64>", "content": "<hex64>", "roster": "<hex64>" },
  "roster": {
    "npc_count": 500, "zone_filter": [], "dynamic": false,
    "npcs": [ { "slot": 0, "npc_id": 1, "archetype": 3, "zone": 2, "home_poi": 29, "work_poi": 55,
                "spawn": { "x": 32.1, "y": 0, "z": -600 }, "faction": 0, "instance": 0 } ]
  },
  "zones":      [ { "code": 1, "id": "town_center" } ],
  "pois":       [ { "code": 1, "id": "house_001_01", "zone": 1, "type": "home", "pos": { "x": 34.7, "y": 0, "z": 0 }, "capacity": 56 } ],
  "archetypes": [ { "code": 0, "id": "blacksmith" } ],
  "items":      [ { "code": 1, "id": "iron_ore" } ],
  "recipes":    [ { "item": 20, "duration_s": 600, "inputs": [ { "item": 1, "count": 2 } ], "outputs": [ { "item": 20, "count": 1 } ] } ],
  "time":       { "start_game_hour": 6, "time_of_day_hours": { "Dawn": [5, 7], "Morning": [7, 11] } },
  "commands":   { "MoveTo": { "respond": ["NpcArrived"], "time_owner": "game_server" } }
}
```

- **모든 값은 `Npc.Host` 가 쓰는 것과 같은 함수로 만든다** — `MasterDataLoader` → `MasterDataSet.StructuralHash`/`ContentHash`, `NpcRoster.Select(...).Hash`, 샤드는 `ShardTable`. 재구현 금지
- 스폰 좌표는 `Npc.TestGameServer` 가 쓰는 규칙을 **같은 함수로** — `testbed/Npc.TestGameServer/World/` 에서 스폰 좌표를 정하는 코드를 찾아, 공유가 안 되면 `Npc.MasterData` 로 올려 둘이 같이 부른다
- `commands` 절은 T3 의 표를 그대로 직렬화한다
- 위치: 로직 `src/Npc.MasterData/Authoring/LinkBundle.cs`(라이브러리) · 껍질은 `tools/Npc.Cli`. **CLI 의 의존 간선(CLAUDE.md §3)은 늘지 않는다** — Contracts·MasterData·Sim 으로 충분하다. 와이어 버전 범위는 `Npc.Wire` 참조가 생기므로 싣지 않는다(참조 코덱이 이미 MIN/MAX 를 가진다)
- (선택) MCP 툴 `link_bundle` — `tools/Npc.Mcp/Tools/` 에 CLI 를 부르는 껍질

**변경할 파일** 위 + `docs/reference_link.html` "다른 언어로 구현할 때" 절(번들 절차) · `docs/llm/RECIPES/connect-game-server.md`

**테스트**

- **핵심 1건 — "번들만으로 붙는다"**: 파일에서 읽은 번들로 만든 Hello 로 `TcpGameServerLink` 에 붙으면 `Connected`. `TcpLink_HandshakeRejectsMismatch` 의 하네스를 재사용한다. `--npcs 16` 한 경우 + `--zone` 필터 한 경우(선택 기제가 다르다)
- 해시를 다시 계산해 번들과 비교하는 테스트는 쓰지 않는다 (구현 복사본)

**검증** `dotnet run --project tools/Npc.Cli -- export link-bundle --npcs 300 --out bundle.json` → 파일 확인 · `npc --help` 에 명령 표기 · 테스트 통과

**완료 기준** C# 을 한 줄도 부르지 않는 게임서버가 이 파일로 핸드셰이크와 스폰을 할 수 있다 (T6 가 실증한다).

**커밋** `masterdata: 게임서버 연동 번들을 만든다` · `cli: npc export link-bundle`

---

<a id="t6">

</a>

### T6. 파이썬 최소 게임서버 예제 — 적합성 키트 통과까지

**목적** "C# 이 아닌 게임서버도 붙는다"를 말이 아니라 실물로 보인다. 게임서버 팀이 복사해서 시작할 출발점.

**근거** 게임서버 대역은 C# 뿐이다(`testbed/Npc.TestGameServer`, 실습서 13장 `samples/ch13_mini_gs` — 둘 다 C# 라이브러리로 해시·로스터를 얻는다). 파이썬 쪽은 스니퍼(`samples/ch14_sniffer`)뿐이다.

**설계** `samples/python_gs/` — 표준 라이브러리만 (Python 3.10+), `docs/wire/reference/npc_wire.py` 를 import

`mini_gs.py` (목표 300줄 안팎) — `--bundle bundle.json --port 7010 --time-scale 60`:

1. listen → NPC 서버 접속 수락
2. Hello v2 전송 (번들의 해시 · npcCount · timeScale · startTick · 최소 features) → HelloAck 수신, 거절이면 사유 코드 출력
3. 재동기화: `NpcSpawned` × N(로스터 순서, 프레임당 ≤ 256) → `ZoneStateChanged` × Z → `WeatherChanged` × Z (적합성 C1 순서)
4. 100ms 루프: `TickSync` 매 틱, 시간대 경계에 `GameTimeChanged`
5. 명령 처리는 **T3 표 그대로**: MoveTo → 거리 ÷ 속도로 도착 틱을 잡고 `NpcArrived`(중간 Transform 은 생략 가능) · Interact → 레시피 시간 뒤 `NpcInventoryChanged` + `NpcActionCompleted` · InventoryChange → `NpcInventoryChanged` · 즉시형 → `NpcActionCompleted` · CombatAction → `NpcActionCompleted`
6. 이벤트 시퀀스는 단조 증가(N6), 재접속해도 되감지 않는다

`README.md` — 실행 3줄 · 생략한 것(경로 탐색 · 플레이어 근접 · 전투 판정 · 인증) · "여기서부터 당신의 게임서버"

**검증** (수동 — 파이썬은 이 저장소의 빌드 의존이 아니다)

```powershell
dotnet run --project tools/Npc.Cli -- export link-bundle --npcs 300 --out samples/python_gs/bundle.json
python samples/python_gs/mini_gs.py --bundle samples/python_gs/bundle.json --port 7010 --time-scale 60
dotnet run -c Release --project src/Npc.Host -- --link tcp --gs-port 7010 --npcs 300 --time-scale 60 --days 0
# NPC 서버를 내리고, 적합성 키트를 같은 자리에 붙인다
dotnet run --project tools/Npc.Conformance -- --host 127.0.0.1 --port 7010 --npcs 300 --time-scale 60 --seconds 60 --probe
```

- `http://localhost:5080/status` 의 `linkState` 가 `Connected` · `linkReject` 가 null · 대시보드에서 명령·이벤트가 늘어난다
- 적합성 보고서: C1 · C2 · C3 · C6 · C7 통과, C4 · C5 는 **미판정**(근접·전투 미구현이 사유로 적힌다 — 통과로 세지 않는다)
- (선택) `build.ps1` 에 `check_wire_reference.ps1` 처럼 "파이썬이 있으면 돌고 없으면 미실시" 스모크 — 핸드셰이크 수락까지 5초 이내만

**완료 기준** 위 순서대로 파이썬 게임서버에 NPC 서버가 붙어 돌고, 적합성 키트가 불합격 0 을 낸다.

**커밋** `samples: 파이썬 최소 게임서버 — 번들 하나로 붙는다`

---

<a id="t7">

</a>

### T7. 첫 실행 로그 · 종료 판정 · 실행 기준 폴더를 초심자용으로

**목적** 깨끗한 클론에서 README 대로 돌린 사람이 **화면만 보고** 정상인지 안다.

**근거** 깨끗한 클론 첫 실행 로그 (발췌):

```
planstore: 평면 배치다 (C-03 이전). 다음 프리베이크부터 75a41c7f/ 에 쌓인다.
warn: 미생성 버킷 2861건. 아키타입 폴백으로 해소된다.
warn: 플랜 스토어가 낡았다 — 무효화 Full (바뀐 것: prompt/). tools/Npc.Prebake 로 재생성한다.
restore: 스냅샷이 없다 (…\src\Npc.Host\state) — 시드로 기동
Hosting environment: Development
…
ticks 1440 · game day 1 · events 344030 · commands 299096 · steps 298092 · timeouts 40209 · scan/tick 48 · interrupts 500 · replan-q 500 · drops link 0 sim 0 · backlogs 0 · llm 0
```

- `warn` 두 줄이 고장처럼 읽히지만, LLM 을 안 쓰는 첫 실행에서는 **정상 상태**다
- 요약 줄에 판정이 없다. `replan-q 500` 은 `--tier none` 이라 소비자가 없어서인데 설명이 없다
- `dotnet run` 이 `src/Npc.Host/Properties/launchSettings.json` 을 따라 **작업 폴더를 `src/Npc.Host` 로** 잡는다 →
`./state` 가 `src/Npc.Host/state` 가 되고, 환경이 `Development`, 쓰지 않는 URL `5252` 가 적혀 있다

**구현**

1. 기동 로그 (`src/Npc.Host/Program.cs` 의 플랜 스토어 출력부 — `grep -rn "평면 배치" src`)
   - 생성 플랜이 0 이면(pinned 뿐) `info` 한 줄로: `플랜: LLM 생성 0 · 사람 고정 19 · 나머지 2,861 버킷은 직업별 기본 행동으로 돈다 (LLM 없이도 정상)` + 다음 할 일 링크(T17 의 플랜 절)
   - 생성 플랜이 있는데 프리픽스가 다르면 지금처럼 `warn` — 그것은 진짜 낡음이다
   - "(C-03 이전)" 같은 내부 표기는 T11 기준으로 걷어 낸다
2. 종료 요약에 **판정 줄**
   - `판정: 정상 — 틱 p99 0.32ms (예산 20ms) · 틱 할당 0B · 링크 드롭 0 · 이벤트 갭 0` / 넘은 것이 있으면 무엇이 기준을 넘었는지
   - 기준은 **기존 계기만**: p99 ≤ 20ms · `bytesPerTick` = 0 · `eventGaps` = 0 · overruns = 0 · (T2 이후, `--drop-rate` 0 일 때) 합성 타임아웃 비율 ≤ 5 %
   - `llm 0 (LLM 꺼짐 — --tier none)` · `replan-q 500 (LLM 꺼짐: 소비자 없음)`
3. 대시보드 재계획 패널에 같은 설명 한 줄 (`/status` 의 티어 정보를 읽어)
4. `launchSettings.json`: `workingDirectory` 를 저장소 루트로 하거나 파일을 지워 셸의 작업 폴더를 쓰게 한다. `ASPNETCORE_ENVIRONMENT` 지정 제거(또는 `Production`), 쓰지 않는 `applicationUrl` 제거.
 **지우기 전에** `grep -rn launchSettings` 로 스크립트·테스트가 이 프로필에 기대는지 확인

**테스트** 판정 로직을 순수 함수로 빼고 경계만: "p99 20.0ms 는 정상, 20.1ms 는 주의" · "bytesPerTick 1 이면 주의". 문구는 테스트하지 않는다.

**검증** 부록 B 절차(깨끗한 클론) → README 빠른 시작 → `warn` 0줄 · 판정 줄 "정상" · 스냅샷 폴더가 저장소 루트 `state/`(gitignore 확인)

**완료 기준** 처음 돌린 사람이 로그 끝의 판정 한 줄로 성공 여부를 안다.

**커밋** `host: 첫 실행 로그를 초심자가 읽게 고쳤다` · `host: 종료 요약에 판정 줄` · `host: dotnet run 의 작업 폴더를 저장소 루트로`

---

<a id="t8">

</a>

### T8. `Npc.Host doctor` — 한 번에 진단하고 다음 할 일을 말한다

**목적** "왜 안 되지?"에 명령 하나로 답한다.

**근거** 지금은 `npc validate` · `npc regen --check` · `/status` · `samples/ch16_tier/check-engines.ps1` 에 흩어져 있다.

**설계** `dotnet run --project src/Npc.Host -- doctor [--online] [--json]`

**Host 에 두는 이유**: 이미 `validate`·`healthcheck`·`hints`·`schema` 하위 명령이 있고, LLM 설정을 읽는 `Npc.Llm` 을 참조하는 것이 Host 뿐이다 — `npc` CLI 에 두면 새 의존 간선이 생긴다(CLAUDE.md §3).


| #   | 항목                 | 방법                                                       | 실패 시 다음 할 일                                      |
| --- | ------------------ | -------------------------------------------------------- | ------------------------------------------------ |
| 1   | .NET 런타임           | `Environment.Version` ≥ 10                               | 설치 링크                                            |
| 2   | 마스터데이터             | `ValidateCommand` 의 코어 재사용 (V0\~V13 + 로더)                | 첫 위반 + `fix_hint`                                |
| 3   | 파생물 신선도            | `DerivedArtifacts.Stale`                                 | 돌릴 생성기 명령                                        |
| 4   | world\_flags 빌드 일치 | 주어진 폴더의 `world_flags.json` ↔ 빌드된 enum (T15 의 검사 재사용)     | "저장소 `masterdata/world_flags.json` 을 갱신하고 다시 빌드" |
| 5   | 플랜 스토어             | 현재 프리픽스 SHA, 그 폴더의 생성·고정·폴백 수                            | Prebake 명령 + 예상 비용(버킷당 실측 단가 × 미생성 수)            |
| 6   | LLM 엔진             | `appsettings.Llm.json` 파싱, 체인의 엔진별 API 키 환경변수 **존재 여부만** | 넣을 환경변수 이름                                       |
| 7   | (`--online`) 엔진 도달 | `GET {endpoint}/models`, 3초 타임아웃 — 외부 호출이라 기본 꺼짐         | 엔드포인트·키 확인                                       |
| 8   | 포트                 | 5080 · 7010 · 25056 사용 여부                                | `--port` 로 바꾸는 법                                 |


종료 코드 0 = 실패 없음 · 1 = 실패 있음. `--json` 은 `validate --json` 과 같은 규칙.

**변경할 파일** `src/Npc.Host/Commands/DoctorCommand.cs`(신규) · `src/Npc.Host/Program.cs`(하위 명령 분기) · `HostOptions` 도움말 · `docs/security/secrets.md`(키 값을 출력하지 않는다는 문장)

**테스트**

- **키 값이 출력에 절대 나오지 않는다** — 환경변수에 표식 문자열을 넣고 출력 전체에서 그 문자열이 0회 (시크릿 유출은 실제로 나는 사고 유형이다)
- world\_flags 불일치 판정 1건
- 나머지는 기존 명령 재사용이라 새 테스트를 쓰지 않는다

**검증** 깨끗한 클론에서 `doctor` → 1\~4 OK · 5 "LLM 생성 0 — 폴백으로 정상" · 6 "키 없음 — LLM 없이 동작". `$env:OPENROUTER_API_KEY="x"` 후 6 OK. 실습서 4장 `samples/ch04_break/break.ps1` 의 패치 하나를 사본에 걸고 `--masterdata <사본>` → 2 실패.

**완료 기준** 막힌 사용자가 `doctor` 출력만 붙여 넣으면 원인이 보인다.

**커밋** `host: doctor — 환경·데이터·플랜·LLM 을 한 번에 진단한다`

---

<a id="t9">

</a>

### T9. 대시보드 라이브 지도

**목적** 설치 5분 뒤 브라우저에서 "NPC 수천 명이 마을에서 산다"를 **본다.** 이 서버가 필요한 이유를 설명하는 가장 빠른 방법이다.

**근거** 지금 눈으로 보는 방법은 Windows 전용 WinForms 뷰어 + 3프로세스(`testbed/run_demo.ps1`)뿐이다. 대시보드(`src/Npc.Host/wwwroot/dashboard.html`)는 수치 패널과 버킷 히트맵뿐.
벌크 조회 `/npcs` 가 이미 웹 스레드에서 SoA 를 읽는다(`QueryEndpoints.List` — "한 틱 어긋남 허용"이 이미 정한 정책).

**설계**

- `GET /world` — 정적: 존(code · id · 중심) · POI(code · type · zone · x · z). 기동 시 1회 만들어 캐시
- `GET /world/npcs` — 동적, 열 배열 JSON: `{ "tick": n, "x": [...], "z": [...], "archetype": [...], "status": [...], "zone_state": [...] }`.
문자열 없음(§2.5). **1초에 한 번 이상 새로 만들지 않고 캐시** — 웹 스레드 비용 상한. 빈 슬롯(디스폰)은 제외
- `dashboard.html` 에 캔버스 패널: POI 점 · NPC 점(아키타입 색 = TestClient 의 황금각 팔레트) · 존 테두리(Peace 회색 · Alert 주황 · War 빨강) ·
클릭하면 기존 "NPC 추적" 패널에 id 를 채운다 · 휠 줌 / 드래그 팬. **외부 스크립트·CDN 없음**
- 인증은 기존 대시보드와 같은 정책(`NPC_ADMIN_TOKEN` 이 있으면 토큰 필요 — `AdminAuth`)
- 틱 루프 무관 — 읽기만 한다. `bytesPerTick` 이 0 으로 남는지 확인

**변경할 파일** `src/Npc.Host/Api/WorldEndpoints.cs`(신규) · `src/Npc.Host/Program.cs` 라우트 · `wwwroot/dashboard.html` · `docs/openapi.json`(생성물 — `OpenApiTests` 가 요구하는 절차로 재생성)

**테스트** "디스폰한 슬롯이 지도 응답에 없다"(B-05 의 슬롯 재사용 사고와 같은 종류) · "토큰이 설정되면 401". 캔버스는 테스트하지 않는다.

**검증** README 빠른 시작 → `http://localhost:5080/dashboard` 에서 점이 움직인다 · `--scenario ./scenarios/siege.jsonl` 에서 존 테두리가 빨개진다 ·
스크린샷을 떠 T12 에서 쓴다 — 예: `msedge --headless --screenshot=docs/img/dashboard_map.png --window-size=1400,900 http://localhost:5080/dashboard`

**완료 기준** Windows 가 아니어도, 프로세스 하나로, 브라우저에서 NPC 가 사는 모습을 본다.

**커밋** `host: 대시보드 라이브 지도 (/world · /world/npcs)`

---

<a id="t10">

</a>

### T10. 데모 플랜 팩 — ⚠ 사람 결정 + 비용 승인

**목적** LLM 키 없이도 첫 실행에서 "LLM 이 만든 플랜"과 헤드라인 수치(캐시 히트율)를 본다.

**근거** 깨끗한 클론에는 생성 플랜이 0 이다(`planstore/plans/`·`planstore/*/plans/` 가 gitignore). 캐시 히트율 0 %, 버킷 19/2880. 프리베이크는 API 키와 비용이 들고, CLAUDE.md 는 **사람 승인 사항**으로 둔다.

**결정할 것**


| 안          | 내용                                                                             | 비용                                    | 유지 부담                                                            |
| ---------- | ------------------------------------------------------------------------------ | ------------------------------------- | ---------------------------------------------------------------- |
| **A (권장)** | 현재 프리픽스의 **도달 집합 264 버킷**을 굽고 `planstore/<sha8>/plans/` 를 커밋 (gitignore 예외 1줄) | 약 $0.37 (`reference_metrics.html` 실측) | 프롬프트를 고칠 때마다 다시 구워야 한다 — `doctor`(T8)·기동 로그가 "데모 팩이 낡았다"고 말하게 한다 |
| B          | 같은 것을 GitHub Release 첨부 zip 으로                                                 | 같음                                    | 저장소는 가볍다. 받는 단계가 하나 는다                                           |
| C          | 하지 않는다. T7 문구로만 안내                                                             | 0                                     | 없음                                                               |


**구현 (A)**

1. 사용자 승인 + 사용할 엔진·키 확인
2. 도달 집합만 굽는 방법을 `dotnet run --project tools/Npc.Prebake -- --help` 로 확인 — 없으면 버킷 목록을 받는 옵션을 **별도 커밋**으로 먼저 추가
3. `dotnet run -c Release --project tools/Npc.Prebake -- --masterdata ./masterdata --out ./planstore --tier T2 --model <승인된 엔진> --concurrency 8 --budget-usd 1.00 <도달 집합 옵션>`
4. `rejected/` 는 커밋하지 않는다(실패 원자료는 로컬 보존 — CLAUDE.md §7)
5. `.gitignore` 에 `!planstore/<sha8>/plans/` 예외 · CLAUDE.md §6 "반드시 커밋하는 것"에 한 줄 + 이유 · `manifest.json` 갱신분 커밋

**검증** 깨끗한 클론 빠른 시작 → 버킷 283/2880 이상, 캐시 히트율 &gt; 0 — **실측값을** README·`reference_metrics.html` 에 적는다

**완료 기준** 키 없는 첫 실행에서 LLM 생성 플랜이 돌고 히트율이 0 이 아니다.

**커밋** `planstore: 데모 팩 — 도달 집합 264 버킷` (+ CLAUDE.md §6)

---

<a id="t11">

</a>

### T11. 사용자 대면 문자열에서 내부 작업 ID 제거 · `--help` 그룹화 · 옵션 문서 생성

**목적** 처음 보는 사람이 모르는 기호를 없애고, 64개 옵션 중 지금 필요한 것만 보이게 한다.

**근거** (이번 조사에서 실제 출력으로 센 값)

- `Npc.Host --help` 73줄 · 옵션 64개 · 내부 ID 11곳: `P6` `A-07` `A-08` `B-05`×2 `B-06` `B-08` `C-03` `D-03` `T4-16` + 지운 문서 `docs/14 §2`
- `npc --help` 내부 ID 4곳: `T22` `T29` `C-05` `B-08`
- 기동 로그 "평면 배치다 (C-03 이전)"
- **주의**: `T1`·`T2` 는 티어 이름(제품 용어)이라 지우지 않는다 — 스캐너 패턴이 이것을 잡으면 안 된다

**범위** 사용자에게 **보이는** 문자열만 — `--help` · 기동/종료 로그 · 오류 메시지 · 대시보드 · `appsettings.Llm.json` 주석.
Studio 는 `StudioTextTests` 가 이미 개발자 낱말을 막는다(같은 방식으로 맞춘다). **코드 주석의 이력 ID 는 그대로 둔다** (CLAUDE.md §0)

**설계**

- 도움말 그룹 7개: ① 기본 실행 ② 게임서버 연결 ③ LLM ④ 저장·복원 ⑤ 관측·경보 ⑥ 보안 ⑦ 측정·개발용.
`--help` = ①\~③ + "전체는 `--help-all`" (≤ 40줄) · `--help-all` = 전부
- 옵션 표를 한 곳에서: 도움말 문자열을 `(그룹, 옵션, 인자, 설명)` 표로 바꾸고 `--help` · `--help-all` · `--help-all --markdown`(→ `docs/host_options.md`, 생성물)이 같은 표를 쓴다. README 의 옵션 표는 이 문서로 대체(T12)
- 스캐너 테스트: 도움말 출력 · `docs/host_options.md` · 기동 로그 문구에 `\b[A-G]-\d{2}\b` · `\bT\d{2}\b` · `\bT\d-\d+\b` · `\bP\d\b` · `docs/\d{2}` 가 없다.
**대조군** 1건: 패턴이 든 문자열을 스캐너가 잡는다 · `T1`·`T2` 는 잡지 않는다 (CLAUDE.md §5.1 검사기 자체 시험)
- 드리프트: `docs/host_options.md` == 생성 결과 (`LayoutDocTests` 방식 — 어긋나면 새로 써 두고 실패)

**변경할 파일** `src/Npc.Host/HostOptions.cs` · `tools/Npc.Cli/Program.cs` · 기동 로그 출력부 · `appsettings.Llm.json` 주석(`_comment` 가 설정 로더에서 무시되는지 확인) · `docs/host_options.md`(신규 생성물) · `tests/Npc.Tests/Host/HostOptionsTests.cs`

**주의** `docs/llm/` 팩의 `Pack_ReferencedOptionsExist` 가 옵션 **이름**을 본다 — 이름은 바꾸지 않는다. 설명만 바꾼다.

**검증** `Npc.Host --help` ≤ 40줄 · `--help-all` 에 64개 전부 · 스캐너·드리프트 테스트 통과

**완료 기준** 사용자 대면 문자열에 내부 작업 ID 가 0 이고, 옵션 문서가 코드에서 생성된다.

**커밋** `host: 도움말을 그룹으로 나누고 옵션 문서를 생성한다` · `host: 사용자 대면 문구에서 내부 작업 ID 를 뺐다` · `cli: 도움말에서 내부 작업 ID 를 뺐다`

---

<a id="t12">

</a>

### T12. README 재구성 (≤ 250줄)

**목적** README 첫 화면에서 넷에 답한다 — ① 무엇인가 ② 나에게 필요한가 ③ 5분 체험 ④ 다음에 어디로.

**근거** 820줄. 사용자 가이드 · 개발자 가이드 · 에이전트 지시문 · 로드맵(W1\~W12, ❌ 3개) · 성능 목표 · 범위 밖이 한 파일에 있다.
상태가 모순이고(99행 "구현 진행 중" ↔ 753행 "구현이 끝난 제품"), 사실과 다른 문장이 있다(1.4 표). 인포그래픽 4장은 안 쓰이고, 화면 캡처는 0장.

**목차**

1. 한 줄 정의 + `docs/infographics/01-what-is-llmnpcserver.png`
2. **이런 게임에 맞다 / 맞지 않다** — FAQ Q2·Q3 요약 표 6줄
3. 실측 한눈에 — 넷만, 출처 링크: NPC 5,000 틱 p99 0.801ms · 틱 할당 0 · LLM 전면 차단에도 완주 · 2,880 상황 플랜 $5.12(도달 집합$0.37)
4. **5분 체험** — `dotnet build` → `doctor`(T8) → 실행 → 대시보드 지도 스크린샷(T9) → Studio 한 줄
5. 도입 경로 넷 — 콘텐츠(Studio · 실습서 2부) · 게임서버(번들 T5 · 파이썬 예제 T6 · 적합성 키트) · LLM(T18) · 운영(Docker) — 각 1\~2줄, 전체는 `docs/ADOPTION.md`(T17)
6. 현재 상태와 한계 — "기술 검증 구현체"를 한 문장, 미측정·미구현 5줄(`reference_metrics.html` §14 링크). **로드맵 표는 지운다**
7. 문서 지도 — 사람 유형별 6줄 (처음 보는 사람 · 콘텐츠 · 게임서버 · LLM · 운영 · 코드 기여)
8. 라이선스 (T1)

**옮길 곳**


| 지금 README 의 내용         | 옮길 곳                                                                                      |
| ---------------------- | ----------------------------------------------------------------------------------------- |
| 실행 옵션 표 · 환경변수 · 설정 파일 | `docs/host_options.md` (T11 생성물) + 시크릿은 `docs/security/secrets.md`                        |
| "MMO 개발에 LLM 을 붙일 때"   | `docs/llm/README.md`(신규) — `SKILL.md` 와 겹치는 것은 지우고 링크                                     |
| 프로젝트 구조 · 핵심 아이디어 그림   | `CODEMAP.md` · `docs/book/` 링크                                                            |
| 빌드 · 테스트 · 스타일         | `CONTRIBUTING.md`(신규) — **CLAUDE.md §1 로 보내는 짧은 포인터**. 규칙을 두 벌로 쓰지 않는다(AGENTS.md 와 같은 원칙) |
| 로드맵 W1\~W12 · 성능 목표    | 삭제 — `reference_metrics.html` 이 판정을 가진다                                                   |


**구현** 옛 README 에서 **사라지는 사실이 없게** — 무엇을 어디로 옮겼는지를 커밋 메시지에 표로 남긴다.

**검증** VS Code 마크다운 미리보기로 렌더링 확인(이미지 상대 경로가 GitHub 에서도 동작하는 형태) · T13 의 README 드리프트 테스트 통과 · `(Get-Content README.md).Count` ≤ 250 · 5분 체험을 깨끗한 클론에서 그대로 친다(부록 B)

**완료 기준** README 만 읽고 5분 체험을 끝낼 수 있고, 첫 화면 안에 "무엇 · 누구에게 · 어떻게 시작"이 있다.

**커밋** `docs: README 를 도입자 기준으로 다시 짰다` (+ `CONTRIBUTING.md` · `docs/llm/README.md`)

---

<a id="t13">

</a>

### T13. 낡은·모순 문서 정리 + README 드리프트 테스트

**목적** 문서가 코드와 어긋나는 곳을 없애고, 다시 어긋나면 테스트가 잡게 한다.

**이번 조사에서 확인한 어긋남**


| 파일                               | 어긋남                                                                                                                                                                 | 고칠 방향                                                                                                         |
| -------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| `docs/index.html` (허브, 200KB)    | 머리 칩 "태스크 132/164 완료 · 문서 기준일 2026-07-27" · §1 "P4 미착수 · 16개 중 9 통과 · 테스트 795건" · "실제 네트워크 전송은 안 만든다" · §3.4 · §5 · §15 "TCP 골격" · §14 틱 p99 0.37ms(README 0.801ms) | 상태·수치를 걷어 내고 `reference_metrics.html` 로 링크. **상태 수치는 `reference_metrics.html` 한 곳에만 둔다** — 허브는 "무엇을 어디서 읽는가"만 |
| `docs/startup_flow.html`         | 기준일 2026-07-27 — 이후 생긴 스냅샷 복원 · TCP 핸드셰이크 · 리로드 · `doctor` 가 흐름에 있는가                                                                                                | 코드와 대조해 갱신하거나 기준일과 "이후 변경" 목록을 명시                                                                             |
| `docs/tutorial/index.html`       | "3부부터는 아직 목차만" — 21장 전부 본문이 있다                                                                                                                                      | 문구 삭제                                                                                                         |
| `docs/FAQ.html` Q3               | "203개 태스크 중 196개" — 밖의 사람에게 뜻 없는 내부 계수                                                                                                                              | 미측정·미구현 목록으로 교체                                                                                               |
| `CLAUDE.md` §3                   | `Npc.TestClient … 미착수` — 실제로 있다                                                                                                                                     | 고친다                                                                                                           |
| `CODEMAP.md` 머리 · `CLAUDE.md` §4 | "`src` 96 파일 20,914줄" · "96파일 21,000줄" — 실제 `git ls-files 'src/*.cs'` 는 **179 파일 48,768줄**                                                                          | 숫자를 지우거나(다시 낡는다) 세는 명령을 같이 적는다                                                                                |
| `appsettings.Llm.json` 주석        | 지운 문서(`docs/10` · `TASKS.md` · `W1_schema.md`) · "사용자 지시"                                                                                                           | T11 과 함께                                                                                                      |
| 실습서 1·2장 수치                      | T2 · T4 이후 명령·타임아웃 수치가 바뀐다                                                                                                                                          | 예제를 다시 돌려 출력 갱신 ("책의 수치는 지어낸 것이 없다" 원칙 유지)                                                                    |
| GPU 요구                           | README 12GB · 실습서 8GB · 실측 8GB                                                                                                                                      | "8GB 에서 실측, 12GB 이상 권장(8B 모델 KV 캐시)"로 통일                                                                      |


**건드리지 않는 것** `masterdata/*.json` 의 `_comment` 도 지운 문서(`docs/01 §6` 등)를 가리키지만 **고치지 않는다** — 파일 바이트가 ContentHash 에 들어가 핸드셰이크 경고와 플랜 스토어 낡음이 난다. 고칠 가치가 있으면 무효화 범위(`npc diff`)를 보고 별도로 한다.

**드리프트 테스트** (신규 `tests/Npc.Tests/Docs/ReadmeTests.cs`)

- README 에 적힌 `npc <명령>` 이 CLI 명령표에 있다
- README 에 적힌 `--옵션` 이 `HostOptions` 가 아는 옵션이다 (`Pack_ReferencedOptionsExist` 와 같은 방식)
- README 가 링크한 상대 경로 파일이 존재한다
- 대조군: 없는 명령을 넣은 문자열을 검사기가 잡는다

**검증** 테스트 통과 · `docs/` `README.md` `CLAUDE.md` 에서 `grep -n -E "미착수|골격만|TCP 골격|기준일 2026-07|구현 진행 중|아직 목차만"` 0건(의도된 이력 표기가 남으면 그 줄을 명시)

**완료 기준** 위 표의 어긋남이 전부 해소되고, README 의 명령·옵션·링크가 테스트로 묶인다.

**커밋** `docs: 허브 문서에서 낡은 상태 수치를 걷어 냈다` · `docs: 실습서·FAQ·CLAUDE.md 의 낡은 표기` · `tests: README 의 명령·옵션·링크 드리프트 검사`

---

<a id="t14">

</a>

### T14. HTML 문서를 GitHub 에서 읽히게 한다 (GitHub Pages) — ⚠ 사람 결정

**목적** 공개 저장소에서 문서를 클론 없이 읽게 한다.

**근거** 핵심 문서가 거의 다 `.html` 이다 — 레퍼런스 3 · 책 13 · 실습서 21 · FAQ · Studio 매뉴얼 · 허브. GitHub 은 HTML 파일을 **소스로** 보여 준다. README 의 문서 링크를 누르면 태그가 보인다.

**결정** GitHub Pages 켜기 (저장소 설정 — 공개 행위라 승인 필요). 권장: `main` 브랜치 `/docs` 폴더

**구현**

1. 승인 후 `gh api -X POST repos/jacking75/LlmNpcServer/pages -f "source[branch]=main" -f "source[path]=/docs"` (또는 사용자가 설정 화면에서)
2. `docs/` 밖을 가리키는 링크(`../README.md` 등)는 Pages 에서 404 다 — `grep -rn 'href="\.\./' docs --include=*.html` 로 뽑아 GitHub blob URL 로 바꾼다
3. `docs/.nojekyll` 추가 (밑줄로 시작하는 파일·폴더 무시 방지)
4. README 문서 지도에 Pages URL 병기 (`https://jacking75.github.io/LlmNpcServer/…`)
5. HTML 이 외부 폰트·스크립트를 부르면 Pages 에서도 동작하는지 확인

**검증** Pages URL 에서 `index.html` · `reference_link.html` · `tutorial/ch01.html` 이 열리고 내부 링크가 동작 (`curl -sI` 200)

**완료 기준** README 의 문서 링크를 누르면 브라우저에서 바로 읽힌다.

**커밋** `docs: GitHub Pages 에서 읽히게 링크를 정리했다`

---

<a id="t15">

</a>

### T15. 엔진 코어 어휘 등록부 + 친절한 오류

**목적** 도입자가 "무엇은 바꿔도 되고, 무엇은 엔진이 이름으로 알고 있어 못 바꾸는가"를 안다. 어기면 알아볼 수 없는 빌드 오류 대신 한국어 한 문장으로 막는다.

**근거**

- `world_flags.json` 은 **저장소 경로에서 빌드 시 enum 으로 생성**된다 — `src/Npc.Core/Npc.Core.csproj` 의 `WorldFlagsJson = ..\..\masterdata\world_flags.json`.
다른 `--masterdata` 폴더가 플래그를 **더하면** `WorldFlagTable.TryParse` 실패로 로드가 깨지고, **빼면** `EventApplier` 등이 컴파일되지 않는다
- 런타임·검증기가 이름으로 참조하는 플래그 약 40종 — `EventApplier` 28곳 · `CoherenceValidator` 19곳 등 (IsRested · ThreatNearby · InCombat · PlayerNearby · HostilePlayerNearby · OnDuty · IsNight · AtWorkplace …)
- POI 심볼 10개 고정: `$home $workplace $market $tavern $temple $gate $nearest_field $nearest_safe $nearest_shelter $patrol_route` (`src/Npc.Core/Plan/PoiSymbol.cs`)
- 버킷 축 고정: 시간대 6 · 지역 상태 4 · 기후 3 (`src/Npc.Core/Planning/BucketKey.cs` 의 enum). `context_buckets.json` 은 값을 **설명**할 뿐 늘릴 수 없다
- 이름으로 찾는 액션: `MoveTo` · `Gather` (`TryGet("…")`)

**설계**

- 등록부 `src/Npc.MasterData/CoreVocabulary.cs` — 네 종류의 목록과 "왜 엔진이 아는가" 한 줄씩
- **로더 앞단 검사**: 주어진 폴더의 `world_flags.json` 이 빌드된 enum 과 이름·bit 가 같은가. 다르면
`이 폴더의 world_flags.json 이 빌드에 쓰인 것과 다르다: +X, −Y. 저장소 masterdata/world_flags.json 을 이것으로 바꾸고 다시 빌드한다`.
새 V-코드를 매길지는 `reference_masterdata.html` 의 검증 절을 **먼저** 고치고 정한다(번호는 뒤에만 추가)
- `npc explain core` — 등록부를 사람 말로. 플래그의 "누가 세우고 누가 읽는가"는 기존 `explain flag` 를 재사용
- 문서: `reference_masterdata.html` 에 "엔진 코어 어휘" 절

**테스트**

- 드리프트(핵심): `src/**/*.cs`(obj 제외)를 훑어 `WorldFlags.<이름>` 참조 집합 ⊆ 등록부 — 누가 새 플래그를 코드에 박으면 등록부 갱신을 강제한다. 대조군: 등록부에 없는 이름이 든 가짜 소스 문자열을 잡는다
- 로더 앞단 검사: 플래그 하나를 뺀 임시 사본 폴더 → 위 문장으로 실패

**변경할 파일** 위 + `tools/Npc.Cli`(explain core) · `docs/reference_masterdata.html` · `docs/llm/CONTEXT.md`(3,000 토큰 예산 — `Context_FitsTheBudget` 확인)

**완료 기준** 코어 어휘를 한 화면에서 보고, 어기면 원인을 말하는 오류를 받는다.

**커밋** `masterdata: 엔진 코어 어휘 등록부와 world_flags 빌드 일치 검사` · `cli: npc explain core`

---

<a id="t16">

</a>

### T16. 최소 월드 템플릿 — `npc init`

**목적** 예제 마을(아키타입 40 · POI 243 · NPC 5,000 · `npc_instances.json` 3.3MB)을 고쳐 들어가는 대신 **작은 빈 세계에서 시작**하게 한다.

**근거** 실습서는 전부 예제 마을에 더하는 방식이다. 생성기가 저장소 `masterdata/` 에 고정돼 있다(`tools/gen_poi_distances.cs` 26행 `Path.Combine(root, "masterdata")`) — 실습서 5장이 "실습장을 작은 저장소 루트로 만드는 트릭"으로 우회한다. `npc regen` 은 생성기 명령을 **출력만** 한다.

**설계**

1. 생성기에 `--masterdata <dir>` 인자 추가 (`tools/gen_npcs.cs` · `tools/gen_poi_distances.cs`). 잠금 파일(`derived.lock.json`)도 그 폴더에 쓴다
2. 템플릿 `samples/worlds/minimal/` — 존 2 · POI 12(집 4 · 일터 3 · 시장 · 선술집 · 신전 · 성문 · 들) · 아키타입 3(villager · farmer · guard — guard 는 `duty_hours` 필수, V12) · 아이템 8 · 레시피 2 · 인터럽트 4 · 폴백 3 · NPC 60.
 `world_flags.json` · `actions.json` · `context_buckets.json` 은 **예제와 같은 파일** (T15 — 코어 어휘·빌드 일치)
3. `npc init <dir> [--template minimal]` — 템플릿 복사 → 생성기 실행 → `npc validate --masterdata <dir>` 결과 출력
4. 템플릿이 계속 유효한지 CI 가 본다 — `build.ps1` 의 검증 단계에 `--masterdata samples/worlds/minimal` 한 줄

**테스트** 드리프트 1건: "템플릿이 V0\~V13 과 로더를 통과한다" — 마스터데이터 규칙이 바뀌면 템플릿도 같이 고치게 강제한다

**검증** `npc init D:\tmp\myworld` → `npc validate --masterdata D:\tmp\myworld` 종료 코드 0 →
`Npc.Host --masterdata D:\tmp\myworld --npcs 60 --no-llm --days 1` 판정 "정상"(T7) → `Npc.Studio --masterdata D:\tmp\myworld` 로 열린다

**완료 기준** 명령 하나로 작고 유효한 세계를 얻고, 그것으로 서버와 Studio 가 돈다.

**커밋** `tools: 생성기가 다른 마스터데이터 폴더를 받는다` · `samples: 최소 월드 템플릿` · `cli: npc init`

---

<a id="t17">

</a>

### T17. 도입 가이드 `docs/ADOPTION.md`

**목적** "우리 게임에 넣으려면 무엇을, 누가, 어떤 순서로, 얼마나"에 답하는 **한 장**. GitHub 에서 바로 읽히게 Markdown 으로.

**근거** 판단 재료가 흩어져 있다 — FAQ Q2·Q3(맞는가) · `reference_metrics.html`(비용·성능) · 실습서 목적별 경로(시간) · `reference_link.html`(연동). **예제 마을이 아닌 자기 세계로 옮기는 절차는 어디에도 없다.**

**목차**

1. 30초 판단 — 맞는 게임 / 안 맞는 게임
2. 역할과 준비물 — 콘텐츠 · 게임서버 · LLM · 운영 담당별
3. 1일차: 예제로 체험 — README 5분 체험 → Studio → `testbed/run_demo.ps1`
4. 세계 매핑 — 우리 게임 개념 → 이 서버 개념
   - 존 · POI · 아키타입 · 아이템 · 액션(40 상한)
   - **고정된 것**(T15): 버킷 3축 — "계절은 기후로, 던전 위험도는 지역 상태로" 같은 매핑 예 · POI 역할 10종 — "선술집 = 휴식·사교 장소" 같은 의미 매핑
   - 최소 템플릿에서 시작(T16) → Studio 로 늘리기
5. 게임서버 연동 — 번들(T5) → 응답 규약(T3) → 파이썬 예제(T6) → 적합성 키트 → 인증·TLS → 샤딩
6. 플랜 굽기 — 비용 산식 · 엔진 선택(T18) · 검수와 pin · 프롬프트를 고치면 무엇이 무효가 되는가
7. 운영 — Docker/k8s · 관리 API · 스냅샷 · 모니터링 · 킬스위치 · 시크릿
8. 상용 투입 전 남은 것 — `reference_metrics.html` §14 미측정 · 법무 미실시 · 샤딩 2단계 미구현 · 블라인드 평가 미실시

**비용 산식** (실측에서 나온 것만 쓰고, 외삽은 외삽이라고 적는다)

```
버킷 수           = 아키타입 수 × 72                  (시간대 6 × 지역 상태 4 × 기후 3)
전량 굽기          ≈ 버킷 수 × $0.0018               (2,880건 $5.12 실측에서 — 재시도 포함)
도달 집합만        ≈ 전량 × 0.092                     (예제 마을 실측 도달률 9.2 % — 세계마다 다르다: 외삽)
예) 아키타입 20   → 1,440 버킷 → 전량 약 $2.6 · 도달 집합 약 $0.24
```

공수는 **미측정**이다 — 실습서의 목적별 소요 시간(예: "우리 서버에 붙여야 한다" 경로 약 8시간)을 "실습 기준 하한"으로만 적는다 (CLAUDE.md §9 — 추정치를 실측처럼 쓰지 않는다).

**변경할 파일** `docs/ADOPTION.md`(신규) · README 링크(T12) · `docs/index.html` 허브 링크 · `tools/Npc.Mcp/Tools/DocsTools.cs` 의 문서 검색 대상 목록

**검증** 문서의 모든 명령을 깨끗한 클론에서 실제로 친다(부록 B). 링크가 전부 열린다.

**완료 기준** 도입을 검토하는 팀이 이 문서 하나로 "하겠다/안 하겠다"와 첫 주 계획을 정할 수 있다.

**커밋** `docs: 도입 가이드`

---

<a id="t18">

</a>

### T18. LLM 엔진 연결을 쉽게 — OpenAI 키 · Ollama · LM Studio

**목적** "내 OpenAI 키로", "내 PC 의 Ollama 로"를 저장소 파일을 고치지 않고 3줄로 붙인다.

**근거** `appsettings.Llm.json` 의 엔진 15개가 dotLLM · llama.cpp(포트 1234) · OpenRouter · Poe · Gemini 뿐이다. `preferred` 는 "사용자 지시"로 Poe 가 먼저다.
README 의 LLM 절은 dotLLM 실행 파일(별도 배포 · GPLv3)부터 시작한다. 엔진은 OpenAI 호환 HTTP 라 Ollama(`http://localhost:11434/v1`)·LM Studio(`http://localhost:1234/v1`)는 **항목만 있으면 된다.**

**설계**

1. 엔진 항목 추가
   - `openai-<모델>` — External · `OPENAI_API_KEY`. 단가는 **공식 가격표를 확인해** 적는다. 확인 못 하면 넣지 않거나, 넣더라도 "단가 미확인 — 비용 캡이 정확하지 않다" 주석
   - `ollama-qwen3-8b` — `http://localhost:11434/v1`, 키 없음, 비용 0
   - 기존 `llamacpp-*`(포트 1234)가 LM Studio 기본 포트라는 것을 주석으로
   - Anthropic 은 OpenAI 호환 엔드포인트를 구현 시점에 확인하고, 불확실하면 넣지 않는다
2. **로컬 덮어쓰기** `appsettings.Llm.local.json`(gitignore) — 있으면 병합(같은 id 는 덮고 새 id 는 더한다, `preferred`·`chains` 는 로컬이 이긴다). 먼저 `Npc.Llm` 설정 로더에 비슷한 장치가 있는지 확인
3. `doctor`(T8) 6·7 항목이 엔진 연결을 확인
4. 문서: `docs/ADOPTION.md` §5 · README LLM 절을 "① 외부 API 키 ② Ollama ③ dotLLM(고급)" 순서로 각 3줄

**테스트** 설정 병합 1건 (덮기 + 더하기 + 체인 우선). 엔진 항목 추가 자체는 테스트하지 않는다.

**검증** Ollama 가 있는 기계면 `ollama pull qwen3:8b` 후 `--tier t1 --t1-engine ollama-qwen3-8b` 로 재계획 1건 성공 (없으면 "미실시"로 기록). OpenAI 키는 사용자가 줄 때만 1건.

**완료 기준** 흔한 세 경로(OpenAI 키 · Ollama · LM Studio)가 문서 3줄 + 환경변수로 동작한다.

**커밋** `llm: OpenAI·Ollama 엔진 항목과 로컬 덮어쓰기 설정`

---

<a id="t19">

</a>

### T19. 배포 산출물(자체 포함 zip) + 플랫폼 표기 정정 — ⚠ 릴리스 게시는 사람 결정

**목적** .NET SDK 가 없는 사람(게임서버 C++ 팀 · 기획자)도 받아서 실행한다. 플랫폼 표기를 사실대로 한다.

**근거** 릴리스·태그 0 · 이미지 미게시 · 소스 빌드만. README "OS: Windows 10/11 x64" — 그러나 `net10.0-windows` 는 WinForms 뷰어(`Npc.TestClient`) 하나이고, Host · CLI · Studio · Conformance · TestGameServer 는 `net10.0` 이다(Docker 이미지가 리눅스에서 Host 를 돌린다).

**설계**

- `tools/publish.ps1 -Rid win-x64|linux-x64|osx-arm64` → `dist/npc-server-<버전>-<rid>.zip`
  - Host · npc · Studio · Conformance · TestGameServer (자체 포함. 단일 파일은 Studio 의 정적 자산 때문에 확인 후)
  - `masterdata/` · `planstore/pinned/` + `manifest.json` · `appsettings.Llm.json` · `scenarios/` · `QUICKSTART.md`(5줄) · LICENSE
  - **dotLLM · 모델은 넣지 않는다** (GPLv3 · CLAUDE.md §2.7)
- 실행 파일 옆 `masterdata/` 를 찾는지 확인 — `HostOptions` 가 이미 `AppContext.BaseDirectory` 를 탐색한다
- README 플랫폼 표: 서버·도구 = Windows · Linux · macOS(macOS 는 "미검증") · 뷰어 = Windows
- (사람 결정) `gh release create v<버전> dist/*.zip` — 태그 규칙은 `Directory.Build.props` 의 VersionPrefix 주석과 맞춘다

**검증** 깨끗한 폴더에 zip 을 풀고 `dotnet` 이 PATH 에 없는 셸에서 `npc-server --no-llm --npcs 500 --days 1` → 판정 "정상"(T7).
리눅스는 이 기계의 WSL `Ubuntu-24.04` 에서 linux-x64 zip 으로 같은 확인 — 안 되면 "미실시"로 적는다.

**완료 기준** zip 하나로 Windows · Linux 에서 5분 체험이 된다.

**커밋** `tools: 자체 포함 배포 zip 을 만든다` · `docs: 플랫폼 표기를 사실대로`

---

<a id="appendix-a">

</a>

## 부록 A — 트레이스 분석 스크립트 (T2 · T4 검증용)

기록 트레이스는 `--link record --trace <파일>` 로 만든다. 한 줄이 `{"Kind":…, "Command":{…}}` 또는 `{"Kind":…, "Event":{…}}` 다.
명령 종류 번호: 1 Spawn · 2 Despawn · 3 MoveTo · 4 Stop · 5 FaceTo · 6 PlayAnimation · 7 SetVisualState · 8 Interact · 9 Speak · 10 InventoryChange · 11 CombatAction · 12 SetAggro.
이벤트: 3 NpcSpawned · 5 NpcTransform · 6 NpcArrived · 7 NpcActionCompleted · 8 NpcActionFailed.

**A-1. MoveTo 결과를 거리별로** (`move_outcomes.py <trace.jsonl>` — 저장소 루트에서)

```python
import json, sys, math, collections
pois = {q['code']: (q['pos']['x'], q['pos']['z'])
        for q in json.load(open('masterdata/pois.json', encoding='utf-8'))['pois']}
pos, cmds, arrived, failed, last = {}, {}, set(), set(), 0
for line in open(sys.argv[1], encoding='utf-8'):
    r = json.loads(line)
    if 'Command' in r:
        c = r['Command']
        if c['Kind'] == 3 and c['TargetPoi']['Value'] and not c['TargetNpc']['Value']:
            n, tgt = c['Npc']['Value'], pois.get(c['TargetPoi']['Value'])
            if n in pos and tgt:
                cmds[c['Correlation']['Value']] = (c['IssuedAt']['Value'], math.dist(pos[n], tgt))
    else:
        e = r['Event']; last = max(last, e['OccurredAt']['Value'])
        if e['Kind'] in (3, 5, 6): pos[e['Npc']['Value']] = (e['Pos']['X'], e['Pos']['Z'])
        if e['Kind'] == 6: arrived.add(e['Correlation']['Value'])
        if e['Kind'] == 8: failed.add(e['Correlation']['Value'])
bins = collections.defaultdict(lambda: [0, 0, 0])
for corr, (t, d) in cmds.items():
    if last - t < 200: continue                      # 아직 진행 중일 수 있는 것은 뺀다
    b = bins[int(d // 250) * 250]
    b[0 if corr in arrived else 1 if corr in failed else 2] += 1
print('거리(m)  도착  실패  무응답')
for k in sorted(bins): print(f'{k:>6}', *bins[k])
```

**A-2. NPC 한 마리의 명령 열** (`npc_timeline.py <trace.jsonl> <npc_id> [시작 시] [time_scale]`)

```python
import json, sys
K = {1:'Spawn',2:'Despawn',3:'MoveTo',4:'Stop',5:'FaceTo',6:'PlayAnim',7:'SetVisual',
     8:'Interact',9:'Speak',10:'InvChange',11:'Combat',12:'SetAggro'}
V = ['Idle','Walking','Running','Working','Fighting','Sleeping','Sitting','Dead']
npc = int(sys.argv[2]); start = float(sys.argv[3]) if len(sys.argv) > 3 else 6.0
scale = int(sys.argv[4]) if len(sys.argv) > 4 else 600
for line in open(sys.argv[1], encoding='utf-8'):
    r = json.loads(line)
    c = r.get('Command')
    if not c or c['Npc']['Value'] != npc: continue
    t = c['IssuedAt']['Value']; h = start + t * scale / 10 / 3600
    extra = (f"poi={c['TargetPoi']['Value']}" if c['Kind'] == 3 else
             V[c['Visual']] if c['Kind'] == 7 else f"item={c['Item']['Value']}")
    print(f"t={t:>5} ({int(h) % 24:02d}:{int(h * 60) % 60:02d}) {K[c['Kind']]:<10} {extra}")
```

<a id="appendix-b">

</a>

## 부록 B — 깨끗한 클론 검증 절차

작업 트리의 로컬 산출물(`planstore/plans/` 718개 등 gitignore 대상)이 결과를 오염시키므로, 사용자 경험 확인은 **항상 깨끗한 클론**에서 한다.

```powershell
$sp = "<세션 스크래치 폴더>"
git clone C:\github_edu\LlmNpcServer "$sp\fresh"
cd "$sp\fresh"
dotnet build -c Release
dotnet run -c Release --no-build --project src/Npc.Host -- --loopback --npcs 500 --time-scale 600 --days 1 --no-llm
# 게임 1일 = 1,440틱 = 실시간 약 2분 24초
```

- 클론 원본은 **로컬 저장소**다 — 커밋하지 않은 변경은 클론에 없다. 확인하려는 변경은 먼저 커밋한다
- 확인할 것: 기동 로그의 `warn` 줄 수 · 종료 요약 · `/status` · `/metrics` · 대시보드

<a id="appendix-c">

</a>

## 부록 C — 이번 조사의 원자료 (2026-09-24)

**환경** Windows 11 Pro · .NET SDK 10.0.400 · 커밋 `5f589a4` · 깨끗한 클론 61MB

**README 빠른 시작 (`--npcs 500 --time-scale 600 --days 1 --no-llm`) 종료 요약**

```
tick p50 0.055ms · p99 0.323ms · max 7.317ms · overruns 0 · gen0 0 · bytes/tick 0 · heap 73.8MB
ticks 1440 · game day 1 · events 344030 · commands 299096 · steps 298092 · timeouts 40209 · scan/tick 48 · interrupts 500 · replan-q 500 · drops link 0 sim 0 · backlogs 0 · llm 0
```

**같은 조건, 446틱 시점 `/status` 발췌** — `stepsAdvanced 91831 · timeoutsSynthesized 12498 (13.6 %) · deviations 16327 · replanQueued 397 · llmCalls 0`

**같은 조건 `/metrics` 발췌** — `cache.hitRate 0 · filledBuckets 19 · coldBuckets 2861 · pinnedBuckets 19 · replan.queueDepth 395`

**기록 트레이스 (`--npcs 100`, 883틱 ≈ 게임 14.7시간) 명령 종류별 건수** — MoveTo 18,620 · SetVisualState 8,254 · InventoryChange 3,007 · Speak 2,943 · Interact 1,751 · PlayAnimation 1,029 · FaceTo 803 (NPC 당 364건)

**월드 규모** — POI 243 · x −1,939.4 \~ 1,894.7 · z −1,591.6 \~ 1,755.9 · POI 쌍 거리 &gt; 500m 86.9 % · &gt; 1,000m 59.8 %

**레시피 소요** — 37종 · 120 \~ 1,800s (600s 6종 · 480s 6종 · 300s 5종 · 360s 5종 …)

**도움말** — `Npc.Host --help` 73줄 · 옵션 64 · 내부 ID 11 · `npc --help` 42줄 · 내부 ID 4

**저장소** — GitHub `PUBLIC` · 라이선스 없음 · 태그·릴리스 0 · 추적 파일 942 · `src` C# 48,768줄 · 테스트 파일 206
