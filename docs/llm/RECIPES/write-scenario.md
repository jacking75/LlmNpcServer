# 시나리오 작성

`scenarios/*.jsonl` 은 세계에 사건을 주입한다. 데모와 장애 시험에 쓴다.

## 전제

- 무엇을 **보려는지** 정해져 있다. "공성 때 위병이 어떻게 반응하나" 처럼.

## 순서

1. `scenarios/<이름>.jsonl` 을 만든다. 한 줄에 사건 하나다.
2. 사건에는 **틱**과 내용이 있다. `DateTime` 을 쓰지 않는다.
3. 기존 셋을 본다 — `siege.jsonl`(지역 상태 전환) · `blackout.jsonl`(LLM 차단) ·
   `daily.jsonl`(평상시).

## 확인

```
dotnet run -c Release --project src/Npc.Host -- `
    --loopback --npcs 500 --time-scale 600 --days 1 --no-llm `
    --scenario ./scenarios/<이름>.jsonl
```

`/metrics` 와 `/status` 에서 의도한 변화가 보이는가. 안 보이면 사건이 안 걸린 것이다.

## 되돌리기

파일을 지운다. 마스터데이터를 건드리지 않았으므로 파급이 없다.

## 파급

| 무엇 | 범위 |
|---|---|
| 전부 | **없음** — 시나리오는 실행 시 주입이다 |
