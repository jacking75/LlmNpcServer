# 액션 추가

**40개 상한이다** (현재 37). 넘기려면 하나를 빼거나 기존 액션의 파라미터로 흡수한다.

## 전제

- 정말 새 액션이어야 하는가. `MoveTo` 의 `speed` 처럼 **파라미터로 흡수**되지 않는가.
- 게임서버가 그 명령을 처리할 수 있는가 — 없으면 NPC 가 타임아웃만 쌓는다.

## 순서

1. ```
   npc next-code actions
   ```
2. `masterdata/actions.json` 에 넣는다 (`npc-action` 스니펫).
   - `requires`/`requires_any`/`forbids`/`grants`/`clears` 는 **월드 플래그 이름**이다 (V2).
   - `desc` 는 **프롬프트 카탈로그에 실린다.** 언제 쓰는 액션인지 적는다.
   - `default_timeout_s` 는 0 이면 안 된다.
   - `emits` 가 게임서버 명령으로의 매핑이다.
3. 필요하면 `CommandEmitter.cs` 에서 `emits.map` 해석을 확인한다.
4. 이 액션을 쓸 아키타입의 `allowed_actions` 에 넣는다 (V4).
5. 폴백 플랜이 이 액션을 쓴다면 그 아키타입에 허용돼 있어야 한다 (V8).

## 확인

```
npc validate
npc explain action <Id>     # 전제·효과·누가 쓸 수 있는가
.\build.ps1
```

## 되돌리기

```
git restore masterdata/
```
**이미 프리베이크를 돌렸다면** `planstore/plans/` 는 새 프리픽스로 만들어진 것이라
되돌린 뒤 프리픽스 해시가 안 맞는다. 지우거나 다시 만든다. `pinned/` 는 지우지 않는다.

## 파급

| 무엇 | 범위 |
|---|---|
| 플랜 | **전량 무효** |
| 프리픽스 | **바뀐다** — 프리베이크 2,880건 ≈ $5.12 |
| 구조 해시 | **바뀐다** → 게임서버 재배포 |
| 게임서버 | 새 명령을 처리해야 한다. 안 하면 타임아웃만 쌓인다 |
