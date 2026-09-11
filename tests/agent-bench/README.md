# 에이전트 벤치 (E-06)

온보딩 팩(E-01)·스키마(E-02)·MCP(E-03)가 **실제로** LLM 에이전트의 성공률을 올리는지 잰다.
재지 않으면 문서는 다시 추측으로 돌아간다.

## 무엇이 있나

```
tests/agent-bench/
  lib/Check.ps1          채점 공통 함수 — 시각도 난수도 없다
  <NN>-<이름>/
    task.md              사용자 요청 원문. 에이전트에게 이것을 그대로 준다
    check.ps1            자동 채점
  07-repair-plans/broken/   실제 반려 플랜 8건 (planstore/rejected 에서 뽑았다)
```

## 어떻게 쓰나

```powershell
# 1. 과제를 본다
powershell -File tools/agent-bench.ps1 -List

# 2. 깨끗한 트리에서 시작한다. 이것이 규약이다 — changed_files 가 그 회차의 값이어야 한다
git status --porcelain   # 비어 있어야 한다

# 3. task.md 를 에이전트에게 그대로 준다. 러너는 에이전트를 부르지 않는다

# 4. 끝나면 채점한다
powershell -File tools/agent-bench.ps1 -Task 04-fallback-plan -Agent claude-code -Minutes 7 -Stamp 2026-09-12
```

## 왜 러너가 에이전트를 부르지 않나

에이전트마다 호출 방식이 다르고, 러너가 그것을 흉내 내면 **"이 러너로 잰 점수"** 가 되어 버린다.
러너가 하는 일은 채점과 기록이다. 실행은 사람이 그 에이전트의 제 방식대로 시킨다.

## 채점기를 믿을 수 있나

```powershell
powershell -File tools/agent-bench.ps1 -SelfTest
```

과제를 **풀지 않은 상태**에서 10종을 전부 채점해, 하나라도 통과하면 실패로 끝난다.
**언제나 통과하는 채점기는 점수가 아니라 상수다.** 벤치를 고칠 때마다 이것부터 돌린다.

## 채점 원칙

- **부분 점수가 없다.** 하나라도 떨어지면 그 과제는 실패다 — 검증에 떨어진 마스터데이터는
  기동을 막으므로 "절반 맞았다" 는 콘텐츠 파이프라인에서 의미가 없다.
- **바뀐 파일을 본다.** 과제를 해냈는지만 보면 옆 파일을 부수고도 통과한다.
  실제로 가장 흔한 사고가 "요청하지 않은 파일이 같이 바뀌었다" 다.
- **거절이 정답인 과제가 있다** (09). 시키는 대로 하는 것이 언제나 옳지는 않다.

## 기록

`docs/measurements/agent_bench.csv` 에 append 된다.
시각은 인자로 받는다 — 스크립트가 벽시계를 읽으면 같은 회차를 다시 채점했을 때 다른 줄이 남는다.
