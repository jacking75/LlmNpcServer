# W1 측정 환경 (T0-02)

> 이 문서는 W1 실측치가 **어떤 기계에서 나온 값인지**를 고정한다.
> M1~M6 (`W1_results.md`) 을 다시 읽을 때 반드시 이 문서와 같이 본다.

측정 시각: 2026-07-25 (KST)

---

## 1. 하드웨어 · OS

| 항목 | 값 |
|---|---|
| CPU | Intel Core i5-14400F (10C / 16T) |
| RAM | 31.7 GB |
| GPU | NVIDIA GeForce RTX 4060 (8,188 MiB) |
| 드라이버 | 610.74 |
| CUDA Toolkit | **설치되지 않음** (§4.1 참조) |
| OS | Windows 11 Pro 10.0.26200 |
| .NET SDK | 10.0.301 (런타임 10.0.9) |

> **VRAM 8GB 가 이 스파이크의 가장 큰 제약이다.** 8B Q4_K_M 가중치만 4,789 MiB 이고,
> 여기에 KV 캐시(프리픽스 4,300 토큰 + 서픽스)가 얹힌다. f32 KV 캐시로는 들어가지 않아
> `--cache-type-k q8_0 --cache-type-v q8_0` 을 기본으로 쓴다.

---

## 2. 추론 엔진

### 2.1 dotLLM

| 항목 | 값 |
|---|---|
| 버전 | 0.1.0-preview.3 (win-x64 self-contained) |
| 라이선스 | GPLv3 — **별도 프로세스 + HTTP 로만 사용**. NuGet 인프로세스 임베딩 금지 (CLAUDE.md §2.7) |
| 설치 위치 | `tools/dotllm/` (저장소에 커밋하지 않는다) |
| 기동 스크립트 | `spike/run-dotllm.ps1` |
| 모델 캐시 | `~/.dotllm/models/` |

기동:

```powershell
.\spike\run-dotllm.ps1 -Fetch          # 최초 1회 — 릴리스 zip 다운로드
.\spike\run-dotllm.ps1                 # serve + 스모크
.\spike\run-dotllm.ps1 -SmokeOnly      # 이미 떠 있는 서버에 스모크만
```

### 2.2 llama.cpp (LM Studio)

작업 지시서의 "Ollama" 자리를 **LM Studio 1.x (llama.cpp 백엔드, CUDA 12 vendor)** 로 대체했다.
이 기계에 Ollama 가 설치되어 있지 않고, 두 엔진 모두 llama.cpp 를 감싼 OpenAI 호환 서버라
"dotLLM 대비 llama.cpp 계열이 얼마나 빠른가"라는 측정 목적은 동일하게 달성된다.

| 항목 | 값 |
|---|---|
| 엔드포인트 | `http://localhost:1234/v1` |
| 기동 | `lms server start` + `lms load <model>` |

### 2.3 외부 API

| 항목 | 값 |
|---|---|
| 제공사 | Google Gemini (`generativelanguage.googleapis.com`) |
| 인증 | `GEMINI_API_KEY` 환경변수 |
| OpenAI 호환 엔드포인트 | `https://generativelanguage.googleapis.com/v1beta/openai` |

**OpenAI 는 이번 측정에서 뺐다.** 이 기계의 `OPENAI_API_KEY` 가 401 (invalid_api_key) 이고,
Anthropic 키는 아예 없다. 작업 지시서의 "외부 2종"은 Gemini 계열 2종으로 대체한다.

---

## 3. 스모크 결과 (T0-02 완료 조건)

dotLLM `serve` (CPU, DeepSeek-R1-0528-Qwen3-8B-Q4_K_M):

| 체크 | 결과 |
|---|---|
| `GET /v1/models` | **200** · `{"object":"list","data":[{"id":"DeepSeek-R1-0528-Qwen3-8B-Q4_K_M",...}]}` |
| `POST /v1/chat/completions` | **200** · `usage{prompt_tokens:18, completion_tokens:8, total_tokens:26}` |
| SSE 스트리밍 | **동작** · `content-type: text/event-stream` · 27 청크 수신 |
| `--gpu-layers` VRAM 점유 | **확인** — GPU 경로로 로드 시 `nvidia-smi` 사용량이 가중치(4,789 MiB)만큼 증가 |

GPU 경로 확인 (Qwen3-8B, `--device gpu --gpu-layers 99`):

```
[dotllm] GPU 0 inference
WARNING: Model weights (~4789 MB) exceed available VRAM (1010/8187 MB free).
         Performance will be degraded due to PCIe memory paging.
Prefill 10,347 ms /   5 tokens
Decode  30,435 ms /  15 tokens   → 0.49 tok/s
```

즉 **GPU 경로 자체는 동작하지만, 측정 시점에 VRAM 이 다른 프로세스에 점유되어 있으면 값이 무의미해진다.**
성능 측정(T0-10) 전에 §4.3 을 반드시 확인한다.

---

## 4. 확인된 제약 — 여기서 걸린 것들

### 4.1 dotLLM CUDA 백엔드는 cuBLAS 를 요구한다

`--device gpu` 로 띄우면 `Error: Dll was not found.` 로 죽는다.
드라이버의 `nvcuda.dll` 은 있지만 `cublas64_12.dll` 이 없어서다
(dotLLM 의 `CudaLibraryResolver` 가 `cublas64_{13,12,11}.dll` 을 순서대로 찾는다).

CUDA Toolkit 을 설치하지 않고, LM Studio 가 들고 있는 vendor 폴더를 PATH 에 얹어 해결했다.

```
$env:PATH = "$env:USERPROFILE\.lmstudio\extensions\backends\vendor\win-llama-cuda12-vendor-v2;$env:PATH"
```

`run-dotllm.ps1` 의 `-CudaDir` 파라미터가 이 경로를 기본값으로 갖는다. 파일을 복사하거나
저장소에 넣지 않는다 (재배포 회피).

### 4.2 dotLLM preview.3 은 Qwen3 의 채팅 템플릿을 파싱하지 못한다

`serve` 로 Qwen3-8B-Q4_K_M 을 올리면 모델 로드 직후 죽는다.

```
Error: Line 18, Col 30: Expected expression, got Colon
```

`run`(원시 컴플리션)은 정상이고 `serve`(채팅 템플릿 적용)만 실패하므로, GGUF 메타데이터의
Jinja 채팅 템플릿 파서 한계다. `--no-warmup` 으로도 회피되지 않는다.
gemma-3-4b 는 아예 아키텍처 미지원(`Unsupported GGUF architecture: 'gemma3'`)이다.

**대응** — dotLLM 쪽 모델을 템플릿이 단순한 것으로 바꾼다.

| 역할 | 모델 | 상태 |
|---|---|---|
| 8B 급 | `bartowski/Qwen2.5-7B-Instruct-GGUF` Q4_K_M | dotLLM 용으로 별도 다운로드 |
| 4B 급 | `bartowski/microsoft_Phi-4-mini-instruct-GGUF` Q4_K_M (3.8B) | 상위 계획의 Phi-4-mini 매트릭스 항목과 일치 |
| (참고) | `DeepSeek-R1-0528-Qwen3-8B` | serve 는 되지만 추론형이라 `<think>` 로 토큰을 태운다. 측정 대상에서 제외 |

llama.cpp(LM Studio) 쪽은 Qwen3-8B / gemma-3-4b 를 그대로 쓴다 — 템플릿·아키텍처 제약이 없다.

### 4.3 측정 시점의 VRAM 점유

스모크 시점에 다른 응용(게임 클라이언트 · 브라우저 · LM Studio)이 GPU 를 **7,637 / 8,188 MiB,
utilization 92%** 로 점유하고 있었다. 이 상태의 로컬 지연은 PCIe 페이징 때문에 20~40배 느리다.

**T0-10 / T0-12 를 돌리기 전 반드시 아래를 확인한다.**

```powershell
nvidia-smi --query-gpu=memory.used,memory.total,utilization.gpu --format=csv
# 8B Q4_K_M 을 GPU 로 재려면 free VRAM >= 6,000 MiB 가 필요하다
```

### 4.4 캐시 적중 토큰(`cached_tokens`) 보고 여부

| 엔진 | `usage.prompt_tokens_details.cached_tokens` | 비고 |
|---|---|---|
| Gemini 3.x (OpenAI 호환) | **보고함** — 4,300 토큰 프리픽스 2회차에서 4,079~4,080 | 암묵 캐시가 걸린다 |
| dotLLM 0.1.0-preview.3 | **보고하지 않음** (항상 0) | KV 재사용은 하지만 usage 에 노출하지 않는다 |
| llama.cpp (LM Studio) | 미확인 (T0-10 에서 확인) | |

따라서 **캐시 적중 판정을 토큰 수에만 의존하면 안 된다.** 로컬 엔진은 `cached_tokens` 대신
prefill 시간(첫 토큰까지의 시간)의 감소로 판정한다 — G0-3 의 판정 기준도 그쪽이다.

### 4.5 로컬 CPU 추론은 이 워크로드에 쓸 수 없다

Phi-4-mini(3.8B) Q4_K_M, CPU 16스레드, 프리픽스 약 4,400 토큰:

```
단건 요청이 10분 타임아웃을 초과 (프리필 미완)
warm-up (10 프롬프트 토큰 + 16 생성): 3,735 ms  → 디코드 4~5 tok/s
짧은 프롬프트(92 토큰): 1회차 7,817 ms / 2회차 374 ms
```

**T0-09 이후의 로컬 측정은 GPU 가 비어 있어야만 가능하다** (§4.3).

### 4.6 PowerShell 5.1 관련

- 이 저장소의 `.ps1` 은 한국어 주석을 포함하므로 **UTF-8 BOM 으로 저장**해야 한다. BOM 이 없으면
  Windows PowerShell 5.1 이 ANSI 로 읽어 파서 오류가 난다.
- `--gpu-layers` 는 `--device cpu` 와 같이 주면 GPU 경로를 켜버린다. CPU 서빙에서는 넘기지 않는다.

---

## 5. 최종 엔진 라인업 (T0-09 ~ T0-12 에서 사용)

| # | 엔진 | 모델 | 위치 |
|---|---|---|---|
| E1 | dotLLM 0.1.0-preview.3 | Qwen2.5-7B-Instruct Q4_K_M | localhost:8080 |
| E2 | dotLLM 0.1.0-preview.3 | Phi-4-mini-instruct Q4_K_M (3.8B) | localhost:8080 |
| E3 | llama.cpp (LM Studio) | Qwen3-8B Q4_K_M | localhost:1234 |
| E4 | llama.cpp (LM Studio) | gemma-3-4b-it Q4_K_M | localhost:1234 |
| E5 | 외부 | gemini-3.1-flash-lite | Gemini OpenAI 호환 |
| E6 | 외부 | gemini-3.5-flash-lite | Gemini OpenAI 호환 |
| E7 | 외부 | gemini-3.5-flash | Gemini OpenAI 호환 |

Gemini **2.5 계열은 이 키로 쓸 수 없다** — `404 "no longer available to new users"`.
3.x 만 남으므로 외부 3종을 그대로 T0-10 매트릭스의 "엔진 7종"으로 삼는다.

G0-4(4B 가 8B 의 80% 이상)는 **E2 vs E1** (dotLLM 내부 비교) 와 **E4 vs E3** (llama.cpp 내부 비교)
두 축으로 본다.
