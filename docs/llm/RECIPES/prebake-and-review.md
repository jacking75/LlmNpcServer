# 프리베이크 → 검수 → 핀

**돈이 든다.** 2,880건 ≈ $5.12, 도달 집합 264건은 $0.37.

## 전제

- `npc validate` 가 통과한다.
- 프리픽스가 확정됐다. 프리베이크 뒤에 프롬프트를 고치면 전량이 무효다.
- **예산 승인을 받았다.**

## 순서

1. **무엇을 만들지 먼저 본다.**
   ```
   npc buckets                       # 지금 무엇이 채워져 있나
   npc diff                          # 무엇이 무효인가
   ```
2. **작게 시작한다.**
   ```
   dotnet run -c Release --project tools/Npc.Prebake -- `
       --budget-usd 1 --limit 100 --concurrency 8
   ```
   `--concurrency` 는 AIMD 초기값이다. 8 에서 시작해 올린다 —
   실측은 24까지 429 가 0이었고 32·64 는 **미측정**이다.
3. **이어서 돌린다.** 끊겼으면 `--resume` 이 없는 것만 만든다.
4. **검수한다.** 실패분은 `planstore/rejected/` 에 코드와 함께 남는다 —
   **조용히 버리지 않는다.** 그것이 품질 개선의 원자료다.
5. **고친 것을 핀한다.**
   ```
   npc plan validate planstore/plans/<버킷>.json    # 4단 통과 확인
   npc pin <버킷> --apply
   ```
   `pinned/` 는 **커밋한다.** 잃으면 검수 작업이 날아간다.

## 확인

```
npc buckets --state missing        # 아직 없는 것
npc buckets --state fallback       # 폴백으로 때운 것
npc plan explain <파일>            # 스텝 트레이스·인벤토리 수지
```

## 되돌리기

`planstore/plans/` 는 생성물이라 지워도 된다. **`pinned/` 는 지우지 않는다.**

## 파급

| 무엇 | 범위 |
|---|---|
| 비용 | 실제로 발생한다 |
| 런타임 | 히트율이 오른다. 실측 98.67% (195버킷으로 달성) |
