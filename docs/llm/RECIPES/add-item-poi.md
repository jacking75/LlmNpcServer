# 아이템·POI 추가

## 전제

- `npc validate` 가 지금 통과한다. 깨진 상태에서 시작하면 무엇이 내 탓인지 모른다.
- 새 POI 의 `subtype` 을 쓰는 아키타입이 있거나, 곧 만들 것이다.

## 순서

1. **번호를 받는다.** 눈으로 세지 않는다.
   ```
   npc next-code items
   npc next-code pois
   ```
2. `masterdata/items.json` 에 아이템을 넣는다 (`npc-item` 스니펫).
   - `grants` 가 플래그를 만든다. **코드에 하드코딩하지 않는다.**
   - 레시피가 있으면 `recipes` 에도 (`npc-recipe`). `id` 는 산출 아이템 id 와 같다.
3. `masterdata/pois.json` 에 POI 를 넣는다 (`npc-poi`).
   - **`capacity` 합이 그 일터를 쓰는 아키타입 인구 이상**이어야 한다 (V10).
   - `zone` 은 존재하는 존이어야 한다 (V3).
   - `allowed_archetypes` 를 비우면 제한 없음이다.
4. 거리표를 다시 만든다.
   ```
   dotnet run tools/gen_poi_distances.cs
   ```

## 확인

```
npc validate            # 0 = 통과. V3(참조) · V10(정원) · V11(도달)을 본다
npc regen --check       # 0 = 파생물 최신
npc explain poi <id>    # 정원·근무 허가·자원이 의도대로인가
npc diff                # 파급
```

## 되돌리기

```
git restore masterdata/
```
`poi_distances.bin` 은 생성물이라 같이 되돌아온다. 되돌린 뒤 `npc regen --check` 로 확인한다.

## 파급

| 무엇 | 범위 |
|---|---|
| 플랜 | **부분 무효** — 새 POI 를 쓰는 버킷만 재생성 |
| 프리픽스 | `items.json` 은 카탈로그에 실린다 → **바뀐다** |
| 구조 해시 | **바뀐다** → 게임서버 재배포 |
| 파생물 | `poi_distances.bin` · `npc_instances.json` |
