# 개인정보 — 플레이어 id 가 어디에 남는가

## 무엇이 개인정보인가

이 서버가 다루는 것 중 개인과 이어지는 것은 **플레이어 id** 하나다.

**플레이어가 쓴 문자열(캐릭터명·채팅·길드명)은 이 서버에 들어오지 않는다.**
계약이 그것을 막는다 — 패킷에 `string` 필드가 없고, 프롬프트에는 구조화 enum 과
내부 ID 만 실린다 (`CLAUDE.md` §2.2·§2.5, 리플렉션 테스트가 강제).

## 어디에 남는가

| 위치 | 무엇이 | 보존 | 삭제 경로 |
|---|---|---|---|
| **명령·이벤트 스트림** | `PlayerId`(숫자) | 메모리. 틱이 지나면 사라진다 | 자동 |
| **`--link record` 기록** | `PlayerId` | 파일(`logs/`). `.gitignore` | 파일 삭제 |
| **리플레이 로그** | 위와 같음 | `replays/`. `.gitignore` | 파일 삭제 |
| **NPC 상태 스냅샷** | 근접 플레이어 참조 | `state/`. `.gitignore` | 파일 삭제 · 스냅샷 폐기 |
| **감사 로그** | 운영자 식별자 (플레이어 아님) | `state/audit.jsonl` | 파일 삭제 |
| **구조화 로그** | `PlayerId` 가 실릴 수 있다 | 로그 수집기 정책 | 수집기에서 |
| **LLM 제공사** | **나가지 않는다** | — | 해당 없음 |
| **NPC 기억** (D-03) | `PlayerId`(숫자) · 호감도 · enum·키 | `--memory <dir>` 의 `memory.json`. `.gitignore` | **`DELETE /admin/memory/forget?player=N`** · `--memory-ttl-days` |

## 확인

```powershell
# 프롬프트에 문자열이 실리는지 — 테스트가 강제한다
dotnet test -c Release --filter "FullyQualifiedName~PromptIsolation"

# 패킷에 string·DateTime 이 없는지 (N3·N4)
dotnet test -c Release --filter "Category=Contracts"
```

## NPC 기억 저장소 (D-03)

**자연어를 저장하지 않는다.** 한 줄이 가진 문자열 필드는 `summary_key` 하나이고 그것은
로컬라이즈 키다 (`dialogue.trade`) — 대화 원문도, 플레이어가 쓴 문장도 들어가지 않는다.
`MemoryStoreTests.Records_HaveNoFreeTextField` 가 타입 수준에서 그것을 강제한다.

재계획 프롬프트에 실리는 것은 **3단 enum 하나**(`hostile`·`neutral`·`friendly`)다.
호감도 원값도 플레이어 id 도 서픽스에 나가지 않는다
(`RelationshipSuffixTests.Suffix_LeaksNeitherPlayerNorAffinity`).

```powershell
# 한 플레이어의 관계·기억·평판을 전부 지운다. 감사 로그에 줄 수가 남는다
curl -X DELETE "http://127.0.0.1:5080/admin/memory/forget?player=42&reason=탈퇴" `
     -H "Authorization: Bearer $env:NPC_ADMIN_TOKEN"
```

`--memory-ttl-days N` 은 그보다 오래된 기록을 **종료 시** 지운다. 기준은 벽시계가 아니라
게임 틱이다 (`CLAUDE.md` §2.3) — 배속 회차에서 "실시간 며칠" 로 재면 값이 회차마다 달라진다.

## 삭제 요청이 오면

1. `logs/` · `replays/` · `state/` 에서 해당 기간 파일을 찾는다 — 전부 `.gitignore` 라
   저장소에는 없다.
2. **기억 저장소에 `DELETE /admin/memory/forget?player=N` 을 부른다** (D-03).
   관계·기억·평판 셋 다 지워야 "지웠다" 고 말할 수 있고, 그 API 가 셋을 같이 지운다.
3. 로그 수집기의 보존 정책에 따라 삭제한다.
4. **스냅샷은 통째로 폐기하는 것이 가장 확실하다** — 그 안에서 한 플레이어만 지우면
   상태 해시가 깨져 복구가 거부된다.

## 잔여 — **지우지 않는다**

| 항목 | 상태 |
|---|---|
| 보존 기간 정책 | **미정** — 운영 정책이 정해지면 여기 적는다 |
| 로그 수집기 설정 | **미정** |
| D-03 기억 저장소 보존 기간 | **미정** — `--memory-ttl-days` 는 있고 값은 운영 정책이 정한다 |
| 외부 저장소(Redis·PostgreSQL) 어댑터 | **미구현** — 붙여 볼 인스턴스가 없다. `IMemoryStore` 가 자리를 잡아 두었다 |
| 법무 검토 | **미실시** |
