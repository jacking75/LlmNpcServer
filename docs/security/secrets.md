# 시크릿 — 무엇이 있고 어떻게 돌리는가

이 서버가 쓰는 비밀은 셋이다. **전부 환경변수에서만 온다** — 인자는 `ps` 에 보이고
파일은 이미지에 굽힌다.

## 목록

| 환경변수 | 무엇 | 형식 | 읽는 곳 | 없으면 |
|---|---|---|---|---|
| `NPC_LINK_SECRET` | 링크 상호 인증 HMAC 키 | **hex 64자**(32바이트) | `Program.cs` 기동 시 1회 | `--require-link-auth` 를 켤 수 없다 |
| `NPC_LINK_CERT_PASSWORD` | 클라이언트 인증서 비밀번호 (mTLS) | 문자열 | `TlsStreamFactory` | mTLS 인증서를 못 연다 |
| `NPC_ADMIN_TOKEN` | 관리 API Bearer 토큰 | 문자열 | `AdminAuth` 생성 시 1회 | **`--bind 0.0.0.0` 이 거부된다** |
| `<엔진별>_API_KEY` | 외부 LLM 제공사 키 | 제공사 형식 | `ChatClientFactory` | 그 엔진을 **끄고** 계속 간다 |

엔진별 키 이름은 `appsettings.Llm.json` 의 `api_key_env` 에 있다. 코드에 키를 쓰지 않는다.

```powershell
# 링크 비밀 만들기 — 32바이트 hex (Windows PowerShell 5.1 에서도 돈다)
$b = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
-join ($b | ForEach-Object { $_.ToString("x2") })
```

## 회전 절차

**세 비밀 모두 기동 시 1회 읽는다. 무중단 회전 경로는 없다.**
이것은 결함이 아니라 지금의 사실이다 — 아래 잔여에 적혀 있다.

### `NPC_ADMIN_TOKEN`

1. 새 토큰을 시크릿 저장소에 넣는다.
2. NPC 서버를 롤링 재기동한다. `AdminAuth` 가 새 값을 잡는다.
3. 재기동 중 이전 토큰으로 온 요청은 **401** 이다. 대시보드·수집기에 먼저 알린다.
4. `state/audit.jsonl` 에서 회전 전후의 401 급증을 확인한다 — 남은 클라이언트가 있다는 뜻이다.

### `NPC_LINK_SECRET`

**양쪽이 같은 값을 봐야 한다.** 한쪽만 바꾸면 핸드셰이크가 `AuthFailed` 로 떨어진다.

1. 게임서버 운영자와 **회전 시각을 합의**한다.
2. 두 서버의 환경변수를 바꾼다.
3. **NPC 서버를 먼저 내린다** — 게임서버가 붙어 있는 채로 NPC 서버만 바뀌면
   재접속 시도가 인증 실패로 쌓인다.
4. 게임서버 → NPC 서버 순으로 올린다.
5. `/metrics` 의 링크 상태와 시퀀스 갭 0 을 확인한다.

### `NPC_LINK_CERT_PASSWORD` · 인증서

인증서 갱신과 같이 돈다. 비밀번호만 바꾸는 회전은 인증서를 다시 내보내야(export) 하므로
**인증서 만료 주기에 묶는다** — 따로 돌리지 않는다.

### LLM API 키

1. 제공사 콘솔에서 새 키를 만든다 (**옛 키를 지우기 전에**).
2. 환경변수를 바꾸고 재기동한다.
3. 기동 로그의 `warn: ... 키가 없다. 이 티어를 끈다.` 가 없는지 본다.
4. 제공사 콘솔에서 옛 키를 폐기한다.

> 키가 없으면 그 엔진만 꺼진다 — 서버는 뜬다. **조용한 T1 강등**이므로
> 기동 로그를 확인하지 않으면 알아채지 못한다.

## 유출되면

1. **먼저 폐기한다.** 회전보다 폐기가 먼저다.
   - `NPC_ADMIN_TOKEN` → 값을 지우고 재기동하면 관리 API 는 `--bind 127.0.0.1` 로만 열린다.
   - LLM 키 → 제공사 콘솔에서 즉시 revoke. 청구서를 같이 본다.
   - `NPC_LINK_SECRET` → 링크를 끊고 `--require-link-auth` 로 인증 없는 재접속을 거절한다.
2. 노출 경로를 찾는다 — 로그·코어덤프·이미지 레이어·CI 로그.
3. `state/audit.jsonl` 로 그 사이의 관리 API 호출을 훑는다.

## 저장소에 들어가지 않게

`.gitignore` 가 `logs/` · `state/` · `replays/` · `artifacts/` 를 막는다.
`appsettings*.json` 에는 **키 이름만** 있고 값이 없다.

```powershell
# 값이 들어간 적 없는지 — 히스토리 전체
git log -p -S "sk-" --all | Select-String "sk-"
```

## 잔여 — **지우지 않는다**

| 항목 | 상태 |
|---|---|
| 무중단 회전(이중 비밀 창) | **미구현** — `LinkAuth` 는 비밀 하나만 받는다. 회전 = 재기동이다 |
| 저장소 시크릿 스캔 | **미실시** — 도구를 고르지 않았다 (`gitleaks`·`trufflehog` 후보) |
| 회전 주기 | **미정** — 운영 정책이 정해지면 여기 적는다 |
| 시크릿 저장소 | **미정** — 지금은 환경변수만 본다. Vault·KMS 연동 없음 |
