using System.Collections.Immutable;
using System.Globalization;
using Npc.Gateway;
using Npc.Host.Config;
using Npc.Host.Observability;
using Npc.Host.Persistence;
using Npc.MasterData;

namespace Npc.Host;

/// <summary>게임서버 링크 구현체. README §주요 실행 옵션.</summary>
public enum LinkKind
{
    /// <summary>Npc.Sim 인프로세스 월드에 직결 (기본).</summary>
    Loopback,

    /// <summary>아무것도 보내지 않는다. 링크 없이 기동되는지 확인할 때.</summary>
    Null,

    /// <summary>Loopback 을 감싸 명령·이벤트를 jsonl 로 기록한다.</summary>
    Record,

    /// <summary>기록된 jsonl 을 재생한다. 게임서버 없이 같은 이벤트 열을 다시 먹인다.</summary>
    Replay,

    /// <summary>
    /// 실제 게임서버에 TCP 로 붙는다 (P6 · docs/20 §10.3).
    ///
    /// <b><c>Npc.Sim</c> 을 만들지 않는다</b> — <see cref="Replay"/> 와 같은 경로다.
    /// 세계를 미는 것은 게임서버이고, 우리는 이벤트를 받아 명령을 낼 뿐이다.
    /// </summary>
    Tcp,
}

/// <summary>
/// 어느 티어까지 켤 것인가. docs/14 §6 부하 매트릭스의 "티어 구성" 축.
///
/// <b>P1 에는 <c>--no-llm</c> 하나뿐이라 "T0만"과 "T0+T1+T2"를 가를 수 없었다</b> (T4-15).
/// </summary>
public enum TierMode
{
    /// <summary>T0 만 — 프리베이크 캐시 + 폴백. LLM 호출이 0 이다. <c>--no-llm</c> 의 정식 이름.</summary>
    None = 0,

    /// <summary>T0 + T1 — 로컬 엔진으로 개별 재계획만 한다. 무비용.</summary>
    T1 = 1,

    /// <summary>T0 + T2 — 외부 엔진으로 아키타입 버킷 미스만 채운다.</summary>
    T2 = 2,

    /// <summary>T0 + T1 + T2 — 전부.</summary>
    All = 3,
}

/// <summary>
/// 실행 프로파일 (A-04 · A-02).
///
/// 옵션 30개를 매번 정확히 주는 것은 사람의 일이 아니다. 프로파일은 "운영에서 반드시 이래야
/// 하는 값" 을 묶어 강제한다 — 특히 <c>--days</c> 기본값 1 이 운영 프로세스를 24분 뒤
/// exit 0 으로 조용히 사라지게 하는 함정을 막는다.
/// </summary>
public enum HostProfile
{
    /// <summary>개발 (기본). 지금까지의 동작 그대로다.</summary>
    Dev,

    /// <summary>
    /// 서비스. <c>--days 0</c>(무제한)을 강제하고 스냅샷·복원을 켠다.
    /// 대시보드는 끄지 않는다 — 헬스 프로브가 필요하다 (A-03).
    /// </summary>
    Service,
}

/// <summary>
/// 호스트 실행 옵션. README §주요 실행 옵션 · docs/11 §11.
///
/// <b>인자 파싱은 여기서만 한다.</b> ASP.NET 의 명령줄 설정 공급자는
/// <c>--loopback</c> 같은 값 없는 플래그를 거부하므로 <c>CreateBuilder(args)</c> 에 넘기지 않는다.
/// </summary>
public sealed record HostOptions
{
    /// <summary>대시보드·메트릭 기본 포트.</summary>
    public const int DefaultPort = 5080;

    /// <summary>링크 구현체.</summary>
    public LinkKind Link { get; init; } = LinkKind.Loopback;

    /// <summary>NPC 수.</summary>
    public int Npcs { get; init; } = 500;

    /// <summary>시간 압축. 1=실시간, 60=1초당 게임 1분.</summary>
    public int TimeScale { get; init; } = 60;

    /// <summary>돌릴 게임 일수. 0 이면 무제한.</summary>
    public int Days { get; init; } = 1;

    /// <summary>
    /// 어느 티어까지 켤 것인가. docs/14 §6 의 티어 축.
    /// <c>--no-llm</c> 은 <see cref="TierMode.None"/> 의 별칭이다 (P1 게이트 스크립트가 쓴다).
    /// </summary>
    public TierMode Tier { get; init; } = TierMode.None;

    /// <summary>T1·T2 비활성. 캐시 + 폴백만. <c>--no-llm</c> 의 상태값.</summary>
    public bool NoLlm => Tier == TierMode.None;

    /// <summary>T1 이 켜졌는가.</summary>
    public bool UsesT1 => Tier is TierMode.T1 or TierMode.All;

    /// <summary>T2 가 켜졌는가.</summary>
    public bool UsesT2 => Tier is TierMode.T2 or TierMode.All;

    /// <summary>
    /// T1(로컬) 워커 수. 기본 2 — W1 실측에서 동시 2 에서 처리량이 최대였다
    /// (<c>W1_concurrency.md</c>). T4-15 에서 1/2/4 를 재서 확정한다.
    /// </summary>
    public int T1Workers { get; init; } = 2;

    /// <summary>T2(외부) 워커 수. 기본 8 — 파일럿에서 동시 24 까지 429 가 없었다.</summary>
    public int T2Workers { get; init; } = 8;

    /// <summary>T1 엔진 id. null 이면 <c>appsettings.Llm.json</c> 의 로컬 엔진 중 첫 번째.</summary>
    public string? T1Engine { get; init; }

    /// <summary>T2 엔진 id. null 이면 <c>appsettings.Llm.json</c> 의 <c>default</c>.</summary>
    public string? T2Engine { get; init; }

    /// <summary>시나리오 jsonl 경로.</summary>
    public string? Scenario { get; init; }

    /// <summary>Sim 의 액션 실패 확률 0~1.</summary>
    public double FailRate { get; init; }

    /// <summary>Sim 의 명령 유실 확률 0~1.</summary>
    public double DropRate { get; init; }

    /// <summary>마스터데이터 디렉터리.</summary>
    public string MasterData { get; init; } = "./masterdata";

    /// <summary>
    /// 플랜 스토어 디렉터리. 기동 시 <c>plans/</c>·<c>pinned/</c> 를 로드한다 (docs/13 §2).
    /// 없으면 폴백 40개로만 돈다 — P1 과 같은 동작이다.
    /// </summary>
    public string PlanStore { get; init; } = "./planstore";

    /// <summary><c>--link record</c> 의 출력 경로 · <c>--link replay</c> 의 입력 경로.</summary>
    public string? TracePath { get; init; }

    /// <summary>게임서버 호스트. <c>--link tcp</c> 일 때만 쓴다 (docs/20 §10.3).</summary>
    public string GameServerHost { get; init; } = "127.0.0.1";

    /// <summary>게임서버 링크 포트. docs/20 §12 의 7010.</summary>
    public int GameServerPort { get; init; } = 7010;

    /// <summary>
    /// <c>--gs-host</c>·<c>--gs-port</c> 를 <b>명시했는가.</b>
    ///
    /// <c>--link record</c> 가 무엇을 감쌀지 이 값으로 가른다 (docs/20 §10.3) —
    /// 기본값과 명시값을 구별하지 못하면 "포트가 7010 이니까 TCP 겠지" 라는 추측이 된다.
    /// </summary>
    public bool UsesGameServer { get; init; }

    /// <summary>
    /// 로스터 존 필터. 비어 있으면 전체다.
    ///
    /// <b>게임서버와 같아야 한다</b> — 다르면 로스터 해시가 어긋나 핸드셰이크에서 거절된다
    /// (docs/20 §5.5). 그게 의도된 동작이다: 첨자가 어긋난 채로 도는 것보다 낫다.
    /// </summary>
    public ImmutableArray<string> Zones { get; init; } = [];

    /// <summary>
    /// 맡을 샤드 번호 (A-08). 0 = 단일 샤드(오늘과 같다).
    ///
    /// <para>
    /// <b>1 프로세스 = 1 샤드 = 1 링크다.</b> 프로세스 안에서 링크를 여러 개 만들지 않는다 —
    /// 틱 루프의 "<c>await</c> 는 <c>link.FlushAsync</c> 하나" 규칙을 지키기 쉽고 장애가 격리된다.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="Zones"/> 보다 우선한다.</b> 둘 다 주면 <c>--zone</c> 은 무시된다 —
    /// 샤드 정의가 존 목록을 이미 담고 있고, 두 곳에서 존을 정하면 어긋나는 날이 온다.
    /// </para>
    /// </summary>
    public int Shard { get; init; }

    /// <summary>샤드 정의 파일 (A-08). 기본 <c>deploy/shards.json</c>.</summary>
    public string ShardsPath { get; init; } = ShardTable.DefaultPath;

    /// <summary>대시보드·메트릭 포트.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>가상 플레이어 봇 수. 인지 LOD 가 실제로 갈리는지 보려면 0 보다 커야 한다.</summary>
    public int PlayerBots { get; init; } = 20;

    /// <summary>
    /// 그중 <b>적대</b>로 판정할 봇 수 (B-06). 기본 0 — 켜야 선제공격 경로가 돈다.
    ///
    /// <b>게임서버 대역 전용이다.</b> 실제 게임서버에 붙을 때 적대 판정은 그쪽이 하고,
    /// 우리는 <c>PlayerHostility</c> 를 받을 뿐이다.
    /// </summary>
    public int HostileBots { get; init; }

    /// <summary>Sim·지터의 시드.</summary>
    public int Seed { get; init; } = 20260725;

    /// <summary>
    /// 재계획 점수 가중치 세트 이름. null 이면 <c>Weights.Default</c>.
    /// <c>docs/14 §2</c> 표의 <c>A-proximity</c>·<c>B-baseline</c>·<c>C-deviation</c>·<c>D-uniform</c>
    /// 또는 그 앞 글자(<c>a</c>~<c>d</c>)를 받는다.
    ///
    /// <b>T4-17 의 A/B 자동화가 쓴다</b> — 감으로 튜닝하면 재현이 안 된다 (docs/14 §10).
    /// </summary>
    public string? Weights { get; init; }

    /// <summary>
    /// 인지 스캔 틱당 상한. 기본은 <c>CognitionScheduler.MaxScansPerTick</c>(150),
    /// <b>0 이면 상한을 푼다</b>.
    ///
    /// <b>측정 전용이다</b> — 상한이 걸린 채로는 차수를 판정할 수 없다(무엇을 넣어도 150 에서 잘려
    /// O(1) 로 보인다). T4-16 의 스케일 곡선이 이 옵션을 쓴다 (docs/14 §6).
    /// 운영에서 풀면 그것이 틱 예산을 지키는 장치를 없애는 것이다.
    /// </summary>
    public int ScanCap { get; init; } = -1;

    /// <summary>
    /// 동적 로스터 (B-05). 게임서버가 런타임에 NPC 를 스폰·디스폰할 수 있게 한다.
    ///
    /// <para>
    /// <b>양쪽이 같이 켜야 한다.</b> 켜면 핸드셰이크의 로스터 해시가 "초기 활성 집합" 에서
    /// <b>"누가 존재할 수 있는가"(인스턴스 테이블 전체)</b> 로 바뀐다 — 한쪽만 켜면 해시가
    /// 어긋나 <c>RosterMismatch</c> 로 거절된다. 그것이 의도된 동작이다: 기능 협상은
    /// 핸드셰이크 중에 끝나므로 해시를 협상 결과로 고를 수 없고, 어긋난 채 붙는 것보다
    /// 거절이 싸다.
    /// </para>
    /// </summary>
    public bool DynamicRoster { get; init; }

    /// <summary>
    /// <c>NpcStore</c> 슬롯 수 (B-05). 0 이면 동적 로스터일 때 로스터 × 1.2, 아니면 로스터 수다.
    ///
    /// <b>틱 루프에서 배열을 늘릴 수 없다</b> (CLAUDE.md §2.1) — 여유 슬롯은 기동 시 잡는다.
    /// 넘는 스폰은 무시하고 센다.
    /// </summary>
    public int NpcCapacity { get; init; }

    /// <summary>
    /// 플랜 스토어를 특정 프리픽스 회차로 고정한다 (C-03). 빈 문자열이면 지금 프리픽스다.
    ///
    /// <para>
    /// <b>진단용이다.</b> "이 회차의 플랜으로 돌려 보자" 를 프롬프트 파일을 건드리지 않고
    /// 해 보는 길이다 — 운영에서는 <b>프롬프트를 되돌리는 쪽이 정직하다</b>. 그래야
    /// 만들어진 플랜과 지금 쓰는 프롬프트가 같아진다.
    /// </para>
    /// </summary>
    public string PlanStoreSha { get; init; } = string.Empty;

    /// <summary>
    /// 동시에 열어 줄 조회 스트림(SSE) 수 (B-08). 0 이면 스트림을 열지 않는다.
    ///
    /// <b>상한이 없으면 GM 도구를 여러 개 띄운 것만으로</b> 응답 조립이 틱마다 수십 번 돈다 —
    /// 조회는 다른 스레드지만 CPU 는 같이 쓴다.
    /// </summary>
    public int QueryMaxStreams { get; init; } = 8;

    /// <summary>
    /// <c>planstore/</c> 와 <c>masterdata/</c> 를 감시해 바뀌면 자동으로 리로드한다 (A-07).
    ///
    /// <b>개발용이다.</b> 운영에서는 <c>POST /admin/reload</c> 로 사람이 부른다 —
    /// 파일이 반쯤 쓰인 순간에 워처가 깨어나는 경쟁이 있고, 운영에서 그 경쟁을 감수할
    /// 이유가 없다. 감시 경로에서는 실패해도 현 상태 그대로라 안전하지만 로그가 시끄럽다.
    /// </summary>
    public bool Watch { get; init; }

    /// <summary>
    /// 10Hz 실시간 페이싱을 끄고 최대 속도로 돈다. 부하·게이트 측정용.
    /// 켜면 벽시계를 보지만 <b>게임 로직은 여전히 Tick 만 본다</b> — 리플레이는 깨지지 않는다.
    /// </summary>
    public bool MaxSpeed { get; init; }

    /// <summary>웹 호스트를 띄우지 않는다. 헤드리스 스모크용.</summary>
    public bool NoDashboard { get; init; }

    /// <summary>
    /// <c>POST /control/*</c> 를 연다. docs/20 §11.4.
    ///
    /// <b>기본은 꺼져 있다.</b> 상태를 바꾸는 HTTP 를 기본으로 열지 않는다 —
    /// 데모용 제어 패널이 쓰는 경로이지 운영 경로가 아니다.
    /// </summary>
    public bool DevControl { get; init; }

    /// <summary>
    /// 링크가 <c>Faulted</c> 로 갔을 때 무엇을 할까 (A-03). 기본 <c>exit</c>.
    ///
    /// <b>기본이 종료인 이유.</b> 핸드셰이크 거절은 사람이 고쳐야 하는 상태이고, 프로세스가
    /// 살아 있으면 오케스트레이터가 재시작하지 않는다 — 좀비로 남는다.
    /// </summary>
    public LinkFaultAction OnLinkFault { get; init; } = LinkFaultAction.Exit;

    /// <summary><c>Faulted</c> 진입 후 종료까지의 유예(초). 기본 5.</summary>
    public int FaultGraceSeconds { get; init; } = 5;

    /// <summary>liveness 가 허용하는 루프 정지(초). 기본 30.</summary>
    public int LiveStallSeconds { get; init; } = 30;

    /// <summary>readiness 가 허용하는 틱 정지(초). 기본 10.</summary>
    public int ReadyTickStallSeconds { get; init; } = 10;

    /// <summary>
    /// 프로브 전용 포트 (A-03). null 이면 <see cref="Port"/> 하나로 같이 낸다.
    ///
    /// <b><c>--no-dashboard</c> 와 함께 주면 프로브 세 라우트만 뜬다.</b> 대시보드·질의 API 는
    /// 토큰 뒤로 가고(A-06) 프로브만 무인증으로 남아야 해서 포트를 가를 수 있게 둔다.
    /// 주지 않으면 포트를 물지 않는다 — 헤드리스 스모크가 병렬로 도는 회차를 깨지 않는다.
    /// </summary>
    public int? HealthPort { get; init; }

    /// <summary>
    /// 웹 호스트 바인드 주소 (A-04). 기본 <c>127.0.0.1</c>.
    ///
    /// <b><c>0.0.0.0</c> 은 관리 토큰(<c>NPC_ADMIN_TOKEN</c>)이 있을 때만 허용한다</b> —
    /// 상태를 바꾸는 HTTP 를 무인증으로 외부에 여는 것은 사고이지 설정이 아니다.
    /// 검사는 <see cref="TryValidateBind"/> 가 한다.
    /// </summary>
    public string Bind { get; init; } = "127.0.0.1";

    /// <summary>실행 프로파일 (A-04). <c>service</c> 는 운영 필수값을 강제한다.</summary>
    public HostProfile Profile { get; init; } = HostProfile.Dev;

    /// <summary>설정 파일 경로. null 이면 <c>npc.settings.json</c> 을 실행 파일·작업 폴더에서 찾는다.</summary>
    public string? ConfigPath { get; init; }

    /// <summary><c>--days</c> 를 명시했는가. 프로파일 경고가 이 값으로 갈린다 (A-02).</summary>
    public bool DaysSpecified { get; init; }

    /// <summary><c>--snapshot-interval-s</c> 를 명시했는가. 프로파일이 이 값을 존중한다.</summary>
    public bool SnapshotSpecified { get; init; }

    /// <summary><c>--prometheus</c> 를 명시했는가.</summary>
    public bool PrometheusSpecified { get; init; }

    /// <summary><c>--log-format</c> 을 명시했는가.</summary>
    public bool LogFormatSpecified { get; init; }

    /// <summary>읽은 설정 파일 경로. 없으면 null. 기동 로그에 적는다.</summary>
    public string? LoadedConfigPath { get; init; }

    /// <summary>
    /// 스냅샷 디렉터리 (A-01). 기본 <c>./state</c>.
    ///
    /// 재기동·크래시·롤아웃마다 NPC 전원이 집으로 돌아가고 하던 일을 잊는 것을 막는다.
    /// </summary>
    public string SnapshotDir { get; init; } = "./state";

    /// <summary>
    /// 스냅샷 주기(초). 기본 60.
    ///
    /// <b>이 값이 상태 손실 창의 상한이다</b> — 마지막 스냅샷과 크래시 사이가 통째로 날아간다.
    /// G-05 의 수용 기준(≤ 60초)이 이 값을 본다.
    /// </summary>
    public int SnapshotIntervalSeconds { get; init; } = 60;

    /// <summary>보존할 스냅샷 수. 기본 3. 최신 것이 깨졌을 때 물러날 자리다.</summary>
    public int SnapshotKeep { get; init; } = 3;

    /// <summary>
    /// 기억 저장소 폴더 (D-03). null 이면 <b>꺼져 있다</b> — 밴드가 서픽스에 실리지 않는다.
    ///
    /// <para>
    /// <b>기본이 꺼짐인 이유.</b> 기억은 게임서버·대화 서비스가 쓰는 것이고,
    /// 그것들이 없는 회차에서 빈 저장소를 켜 두면 "켜져 있는데 아무것도 안 나온다" 가 된다.
    /// </para>
    /// </summary>
    public string? MemoryDir { get; init; }

    /// <summary>
    /// 기억 보존 기간(게임 일). 0 이면 지우지 않는다 (D-03).
    ///
    /// <b>기준은 벽시계가 아니라 게임 틱이다</b> (CLAUDE.md §2.3).
    /// </summary>
    public int MemoryTtlDays { get; init; }

    /// <summary>복원 정책 (A-01). 기본 <c>auto</c>.</summary>
    public RestoreMode Restore { get; init; } = RestoreMode.Auto;

    /// <summary><c>--restore</c> 에 경로를 줬으면 그 경로. 아니면 null.</summary>
    public string? RestorePath { get; init; }

    /// <summary>
    /// 스냅샷을 쓰는가. 서비스 프로파일이면 켜지고, <c>--snapshot-interval-s 0</c> 이면 꺼진다.
    ///
    /// <b>개발 기본은 꺼져 있다.</b> 매 회차가 <c>./state</c> 에 파일을 남기면 실습장이 지저분해지고,
    /// 무엇보다 앞 회차의 상태가 다음 회차에 되살아나 "왜 이 NPC 는 이미 밥을 먹었지" 가 된다.
    /// </summary>
    public bool SnapshotEnabled { get; init; }

    /// <summary>
    /// 정상 종료 예산(초). 기본 15 (A-02).
    ///
    /// 넘기면 종료 코드 2 다 — 오케스트레이터가 "정상 종료" 와 "드레인 실패" 를 구별한다.
    /// </summary>
    public int ShutdownTimeoutSeconds { get; init; } = 15;

    /// <summary>
    /// <c>TickSync</c> 가 멈춰도 되는 상한(초). 기본 5. 0 이면 워치독을 끈다 (A-10).
    ///
    /// 하트비트 타임아웃은 소켓이 죽었을 때만 뜬다 — 소켓은 멀쩡한데 틱만 안 오는 경우가 남는다.
    /// </summary>
    public int TickSyncStallSeconds { get; init; } = 5;

    /// <summary>
    /// OTLP 수집기 주소 (A-05). null 이면 OTLP 를 켜지 않는다.
    ///
    /// 예: <c>http://otel-collector:4317</c>.
    /// </summary>
    public string? OtlpEndpoint { get; init; }

    /// <summary>
    /// Prometheus 스크레이프 엔드포인트를 열까 (A-05). 기본은 <c>--profile service</c> 에서 켜진다.
    ///
    /// 기존 <c>/metrics</c> JSON 은 그대로 둔다 — 대시보드가 그것을 쓴다.
    /// </summary>
    public bool Prometheus { get; init; }

    /// <summary>경보 웹훅 주소 (A-05). Slack·Teams 호환 JSON 을 보낸다. null 이면 로그만.</summary>
    public string? AlarmWebhook { get; init; }

    /// <summary>
    /// 같은 <c>(종류, 열쇠)</c> 경보의 재발화 간격(초). 기본 300.
    ///
    /// <b>없으면 링크가 흔들릴 때 초당 수십 건이 나가고 진짜 경보가 묻힌다.</b>
    /// </summary>
    public int AlarmCooldownSeconds { get; init; } = 300;

    /// <summary>로그 형식 (A-05). 기본은 평문, <c>--profile service</c> 는 JSON.</summary>
    public LogFormat LogFormat { get; init; } = LogFormat.Text;

    /// <summary>링크 암호화 모드 (A-06). 기본 <c>off</c>.</summary>
    public LinkTlsMode LinkTls { get; init; } = LinkTlsMode.Off;

    /// <summary>클라이언트 인증서(pfx) 경로. <c>--link-tls mtls</c> 에서만 쓴다.</summary>
    public string? LinkCertificate { get; init; }

    /// <summary>TLS SNI 이름. null 이면 <c>--gs-host</c> 를 쓴다.</summary>
    public string? LinkTlsHost { get; init; }

    /// <summary>
    /// 링크 인증을 반드시 요구하는가 (A-06).
    ///
    /// 기본은 요구하지 않는다 — v1 게임서버·개발 회차와 붙어야 한다.
    /// 켜면 게임서버가 <c>Auth</c> 기능 비트를 안 켠 회차를 <c>AuthFailed</c> 로 거절한다.
    /// </summary>
    public bool RequireLinkAuth { get; init; }

    /// <summary>
    /// 벽시계 하루 비용 캡(USD). 0 이면 끈다 (C-02).
    ///
    /// <b><c>ReplanBudget</c> 의 캡을 우회하는 것이 아니라 더하는 것이다</b> (CLAUDE.md §2.7).
    /// 그쪽 하루는 <c>Tick</c> 기준이라 배속 회차에서 실제 청구일 하루에 여러 번 리셋된다.
    /// </summary>
    public double BillingCapUsd { get; init; }

    /// <summary>청구일이 바뀌는 UTC 시각(0~23). 제공사 청구 주기에 맞춘다.</summary>
    public int BillingResetHour { get; init; }

    /// <summary>
    /// 개체 재계획이 쓸 수 있는 T2 예산의 몫 0~1. 기본 0.20 (C-07).
    ///
    /// <b>예산을 나누기만 하고 총 캡은 그대로다</b> (CLAUDE.md §2.7). 없으면 개별 스필오버가
    /// 하루 예산(약 649건)을 몇 분 만에 태우고, 정작 수천 NPC 가 공유하는 버킷 미스 보충이 굶는다.
    /// </summary>
    public double BudgetIndividualShare { get; init; } = Npc.Core.SpilloverQuota.DefaultShare;

    /// <summary>도움말만 출력한다.</summary>
    public bool Help { get; init; }

    /// <summary>사용법.</summary>
    public static string Usage =>
        """
        사용법: Npc.Host [validate ...] [healthcheck ...] [hints ...] [schema ...] [옵션]

          --loopback              Npc.Sim 인프로세스 월드에 직결 (기본)
          --link null|record|replay|loopback|tcp
                                  링크 구현체 교체. tcp 는 실제 게임서버에 붙는다 (P6)
          --trace <path>          --link record 의 출력 · --link replay 의 입력 (jsonl)
          --gs-host <host>        게임서버 호스트 (기본 127.0.0.1). --link tcp 전용
          --gs-port N             게임서버 링크 포트 (기본 7010)
          --zone <id>[,<id>]      로스터 존 필터. 게임서버와 같아야 한다
          --shard N               맡을 샤드 (A-08). 0=단일. --zone 보다 우선한다
          --shards <path>         샤드 정의 파일 (기본 deploy/shards.json)
          --dev-control           POST /control/* 을 연다 (기본 꺼짐). 데모용이다
          --watch                 planstore/·masterdata/ 를 감시해 자동 리로드 (A-07). 개발용이다
          --npcs N                NPC 수 (기본 500)
          --time-scale N          시간 압축. 1=실시간, 60=1초당 게임 1분 (기본 60)
          --days N                돌릴 게임 일수. 0=무제한 (기본 1)
          --tier none|t1|t2|all   어느 티어까지 켤까 (기본 none)
          --no-llm                --tier none 의 별칭
          --t1-workers N          T1(로컬) 워커 수 (기본 2)
          --t2-workers N          T2(외부) 워커 수 (기본 8)
          --t1-engine <id>        T1 엔진 id (기본: appsettings.Llm.json 의 첫 로컬 엔진)
          --t2-engine <id>        T2 엔진 id (기본: appsettings.Llm.json 의 default)
          --scenario <jsonl>      시나리오 이벤트 주입
          --fail-rate <0~1>       Sim 의 액션 실패 주입
          --drop-rate <0~1>       Sim 의 명령 유실 주입
          --player-bots N         가상 플레이어 수 (기본 20)
          --hostile-bots N        그중 적대로 판정할 봇 수 (기본 0. B-06 · 대역 전용)
          --masterdata <dir>      마스터데이터 디렉터리 (기본 ./masterdata)
          --planstore <dir>       프리베이크된 플랜 스토어 (기본 ./planstore). 없으면 폴백만
          --seed N                Sim 시드 (기본 20260725)
          --port N                대시보드·메트릭 포트 (기본 5080)
          --bind <addr>           웹 호스트 바인드 주소 (기본 127.0.0.1).
                                  0.0.0.0 은 NPC_ADMIN_TOKEN 이 있을 때만 허용한다
          --config <path>         설정 파일 (기본 npc.settings.json 을 자동 탐색)
          --profile dev|service   실행 프로파일. service 는 --days 0 과 스냅샷을 강제한다
          --snapshot-dir <dir>    NPC 상태 스냅샷 디렉터리 (기본 ./state)
          --snapshot-interval-s N 스냅샷 주기 초. 0=끔 (기본: service 60 · dev 꺼짐)
          --memory <dir>       NPC 기억·관계 저장소 폴더 (D-03). 없으면 꺼짐
          --memory-ttl-days N  기억 보존 게임 일. 0=지우지 않음
          --snapshot-keep N       보존할 스냅샷 수 (기본 3)
          --restore auto|none|<path>  복원 정책 (기본 auto). 조건이 안 맞으면 시드로 기동한다
          --shutdown-timeout-s N  정상 종료 예산 초 (기본 15). 넘기면 종료 코드 2
          --tick-sync-stall-s N   TickSync 가 멈춰도 되는 상한 초. 0=끔 (기본 5)
          --otlp-endpoint <url>   OpenTelemetry 수집기 주소 (예 http://collector:4317)
          --prometheus            /metrics/prometheus 를 연다 (--profile service 는 자동)
          --alarm-webhook <url>   경보 웹훅 (Slack/Teams 호환 JSON)
          --alarm-cooldown-s N    같은 경보의 재발화 간격 초 (기본 300)
          --log-format text|json  로그 형식 (기본 text · --profile service 는 json)
          --link-tls off|tls|mtls 링크 암호화 (기본 off). 비밀은 NPC_LINK_SECRET 환경변수
          --link-cert <pfx>       클라이언트 인증서. mtls 전용
                                  (비밀번호는 NPC_LINK_CERT_PASSWORD 환경변수)
          --link-tls-host <name>  TLS SNI 이름 (기본: --gs-host)
          --require-link-auth     게임서버가 인증을 지원하지 않으면 거절한다
          --billing-cap-usd <n>   벽시계 하루 비용 캡(USD). 0=끔. 넘으면 T2 를 끊는다
          --billing-reset-hour N  청구일이 바뀌는 UTC 시각 0~23 (기본 0)
          --budget-individual-share <0~1>
                                  개체 재계획이 쓸 T2 예산의 몫 (기본 0.20).
                                  넘으면 거절이 아니라 T1 대기다
          --weights A|B|C|D       재계획 점수 가중치 세트 (docs/14 §2 표. 기본 B)
          --dynamic-roster        런타임 스폰·디스폰 허용 (B-05). 게임서버도 켜야 한다
          --npc-capacity N        NpcStore 슬롯 수. 0=동적이면 로스터×1.2 (B-05)
          --planstore-sha <sha8>  플랜 스토어를 그 프리픽스 회차로 고정 (C-03. 진단용)
          --query-max-streams N   조회 스트림(SSE) 동시 연결 상한 (기본 8. 0=끔. B-08)
          --scan-cap N            인지 스캔 틱당 상한. 0=상한 해제 (측정 전용, T4-16)
          --max-speed             10Hz 페이싱 없이 최대 속도로 (측정용)
          --no-dashboard          웹 호스트를 띄우지 않는다 (헬스 라우트는 계속 뜬다)
          --on-link-fault exit|wait  링크 Faulted 정책 (기본 exit → 종료 코드 3)
          --fault-grace-s N       Faulted 후 종료까지 유예 초 (기본 5)
          --live-stall-s N        /healthz/live 가 허용하는 루프 정지 초 (기본 30)
          --ready-tick-stall-s N  /healthz/ready 가 허용하는 틱 정지 초 (기본 10)
          --health-port N         프로브 전용 포트. --no-dashboard 와 함께 쓰면 프로브만 뜬다
          -h, --help              이 도움말
        """;

    /// <summary>
    /// 마스터데이터 폴더를 실제 경로로 푼다.
    ///
    /// <c>dotnet run --project src/Npc.Host</c> 는 작업 폴더를 프로젝트 폴더로 잡는다.
    /// README 에 적힌 그대로 쳤을 때 <c>./masterdata</c> 가 없다고 죽으면 안 되므로,
    /// 없으면 작업 폴더와 실행 파일 폴더에서 저장소 루트를 위로 찾아 올라간다.
    /// </summary>
    public string ResolveMasterData()
    {
        if (Directory.Exists(MasterData))
        {
            return MasterData;
        }

        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(MasterData));

        if (name.Length == 0)
        {
            name = "masterdata";
        }

        foreach (string from in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, name);

                // 이름만 보면 안 된다. 윈도우는 대소문자를 구분하지 않아서
                // tests/Npc.Tests/MasterData 같은 소스 폴더가 먼저 걸린다.
                if (File.Exists(Path.Combine(candidate, "archetypes.json")))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException($"마스터데이터 폴더를 찾지 못했다: {MasterData}");
    }

    /// <summary>
    /// 샤드 정의 파일을 실제 경로로 푼다 (A-08).
    ///
    /// <see cref="ResolveMasterData"/> 와 같은 이유로 위로 올라가며 찾는다 —
    /// <c>dotnet run --project src/Npc.Host</c> 는 작업 폴더를 프로젝트 폴더로 잡는다.
    /// <b>못 찾으면 준 경로를 그대로 돌려준다</b> — 그래야 오류 메시지에 사람이 준 값이 나온다.
    /// </summary>
    public string ResolveShards()
    {
        if (File.Exists(ShardsPath))
        {
            return ShardsPath;
        }

        if (Path.IsPathRooted(ShardsPath))
        {
            return ShardsPath;
        }

        foreach (string from in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, ShardsPath);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return ShardsPath;
    }

    /// <summary>
    /// 스냅샷 폴더를 절대경로로 푼다 (A-01). 없으면 만들지 않는다 — 쓰기 시점에 만든다.
    ///
    /// <c>--masterdata</c> 와 달리 위로 올라가며 찾지 않는다. 스냅샷은 <b>이번 회차가 만드는 것</b>
    /// 이고, 상위 폴더의 남의 스냅샷을 주워 복원하면 그것이야말로 사고다.
    /// </summary>
    public string ResolveSnapshotDir() => Path.GetFullPath(SnapshotDir);

    /// <summary>
    /// 플랜 스토어 폴더를 실제 경로로 푼다.
    ///
    /// <see cref="ResolveMasterData"/> 와 같은 이유로 위로 올라가며 찾는다 —
    /// <c>dotnet run --project src/Npc.Host</c> 는 작업 폴더를 프로젝트 폴더로 잡는다.
    /// <b>못 찾으면 던지지 않는다.</b> 첫 기동에는 스토어가 없고, 그때는 폴백으로 돈다.
    /// </summary>
    public string ResolvePlanStore()
    {
        if (Directory.Exists(PlanStore))
        {
            return PlanStore;
        }

        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(PlanStore));

        if (name.Length == 0)
        {
            name = "planstore";
        }

        foreach (string from in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, name);

                // 폴더 이름만 보면 안 된다. 스토어의 표식은 manifest 나 3계층 폴더다.
                if (File.Exists(Path.Combine(candidate, "manifest.json"))
                    || Directory.Exists(Path.Combine(candidate, "plans"))
                    || Directory.Exists(Path.Combine(candidate, "pinned")))
                {
                    return candidate;
                }
            }
        }

        return PlanStore;
    }

    /// <summary>
    /// 파일 경로 옵션을 전부 절대경로로 푼다. 기동 직후 <b>1회</b>.
    ///
    /// <see cref="ResolveMasterData"/> 와 같은 이유다 — <c>dotnet run --project src/Npc.Host</c> 는
    /// 작업 폴더를 프로젝트 폴더로 잡으므로, 문서에 적힌 <c>./scenarios/siege.jsonl</c> 을 그대로 치면
    /// <c>src/Npc.Host/scenarios/</c> 를 보고 죽는다. <c>--masterdata</c> 만 위로 올라가 찾고
    /// <c>--scenario</c>·<c>--trace</c> 는 안 찾는 비대칭이 이 버그의 원인이었다.
    ///
    /// <b>여기서 미리 풀어 두면 아래 코드는 절대경로만 다룬다.</b>
    /// 못 찾은 것은 사용자 입력 문제이므로 예외 대신 <paramref name="error"/> 로 돌려준다 —
    /// 경로 오타에 스택트레이스를 쏟지 않는다.
    /// </summary>
    /// <param name="resolved">경로가 전부 절대경로로 바뀐 옵션.</param>
    /// <param name="error">실패 사유. 성공이면 null.</param>
    public bool TryResolvePaths(out HostOptions resolved, out string? error)
    {
        resolved = this;
        error = null;

        string masterData;

        try
        {
            masterData = Path.GetFullPath(ResolveMasterData());
        }
        catch (DirectoryNotFoundException e)
        {
            error = e.Message;
            return false;
        }

        string? scenario = Scenario;

        if (scenario is not null)
        {
            if (!TryFindFile(scenario, out string found))
            {
                error = $"시나리오 파일을 찾지 못했다: {scenario}";
                return false;
            }

            scenario = found;
        }

        string? trace = TracePath;

        if (trace is not null)
        {
            if (Link == LinkKind.Replay)
            {
                if (!TryFindFile(trace, out string found))
                {
                    error = $"재생할 트레이스 파일을 찾지 못했다: {trace}";
                    return false;
                }

                trace = found;
            }
            else
            {
                // 기록은 아직 없는 파일을 만든다. 상대경로를 작업 폴더 기준으로 두면
                // 같은 명령이 실행 방식에 따라 다른 곳에 쓴다 — 저장소 루트로 고정한다.
                trace = ToRepoAbsolute(trace);
            }
        }

        resolved = this with
        {
            MasterData = masterData,
            PlanStore = Path.GetFullPath(ResolvePlanStore()),
            Scenario = scenario,
            TracePath = trace,
        };

        return true;
    }

    /// <summary>있는 파일을 찾는다. 작업 폴더 → 저장소 루트 순.</summary>
    private static bool TryFindFile(string path, out string found)
    {
        if (File.Exists(path))
        {
            found = Path.GetFullPath(path);
            return true;
        }

        if (!Path.IsPathRooted(path) && FindRepoRoot() is { } root)
        {
            string candidate = Path.Combine(root, path);

            if (File.Exists(candidate))
            {
                found = Path.GetFullPath(candidate);
                return true;
            }
        }

        found = path;
        return false;
    }

    /// <summary>상대경로를 저장소 루트 기준 절대경로로. 루트를 못 찾으면 작업 폴더 기준이다.</summary>
    private static string ToRepoAbsolute(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        return FindRepoRoot() is { } root
            ? Path.GetFullPath(Path.Combine(root, path))
            : Path.GetFullPath(path);
    }

    /// <summary>
    /// 저장소 루트. 표식은 <c>masterdata/archetypes.json</c> 이다 —
    /// <see cref="ResolveMasterData"/> 가 이미 같은 파일을 표식으로 쓴다.
    /// </summary>
    private static string? FindRepoRoot()
    {
        foreach (string from in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "masterdata", "archetypes.json")))
                {
                    return dir.FullName;
                }
            }
        }

        return null;
    }

    /// <summary>인자를 파싱한다. 실패하면 <paramref name="error"/> 에 이유가 담긴다.</summary>
    public static bool TryParse(string[] args, out HostOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        var result = new HostOptions();
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "-h" or "--help":
                    result = result with { Help = true };
                    break;

                case "--loopback":
                    result = result with { Link = LinkKind.Loopback };
                    break;

                case "--no-llm":
                    result = result with { Tier = TierMode.None };
                    break;

                case "--tier":
                    if (!TryValue(args, ref i, arg, out string? tier, out error)
                        || !Enum.TryParse(tier, ignoreCase: true, out TierMode tierMode))
                    {
                        error ??= $"--tier 값이 잘못됐다: '{tier}'. none|t1|t2|all 중 하나다.";
                        options = result;
                        return false;
                    }

                    result = result with { Tier = tierMode };
                    break;

                case "--t1-engine":
                    if (!TryValue(args, ref i, arg, out string? t1Engine, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { T1Engine = t1Engine };
                    break;

                case "--t2-engine":
                    if (!TryValue(args, ref i, arg, out string? t2Engine, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { T2Engine = t2Engine };
                    break;

                case "--t1-workers":
                    if (!TryInt(args, ref i, arg, 1, 64, out int t1Workers, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { T1Workers = t1Workers };
                    break;

                case "--t2-workers":
                    if (!TryInt(args, ref i, arg, 1, 64, out int t2Workers, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { T2Workers = t2Workers };
                    break;

                case "--max-speed":
                    result = result with { MaxSpeed = true };
                    break;

                case "--no-dashboard":
                    result = result with { NoDashboard = true };
                    break;

                case "--link":
                    if (!TryValue(args, ref i, arg, out string? link, out error)
                        || !Enum.TryParse(link, ignoreCase: true, out LinkKind kind))
                    {
                        error ??= $"--link 값이 잘못됐다: '{link}'. null|record|replay|loopback|tcp 중 하나다.";
                        options = result;
                        return false;
                    }

                    result = result with { Link = kind };
                    break;

                case "--dev-control":
                    result = result with { DevControl = true };
                    break;

                case "--watch":
                    result = result with { Watch = true };
                    break;

                case "--on-link-fault":
                    if (!TryValue(args, ref i, arg, out string? fault, out error)
                        || !Enum.TryParse(fault, ignoreCase: true, out LinkFaultAction faultAction))
                    {
                        error ??= $"--on-link-fault 값이 잘못됐다: '{fault}'. exit|wait 중 하나다.";
                        options = result;
                        return false;
                    }

                    result = result with { OnLinkFault = faultAction };
                    break;

                case "--fault-grace-s":
                    if (!TryInt(args, ref i, arg, 0, 3_600, out int faultGrace, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { FaultGraceSeconds = faultGrace };
                    break;

                case "--live-stall-s":
                    if (!TryInt(args, ref i, arg, 1, 86_400, out int liveStall, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { LiveStallSeconds = liveStall };
                    break;

                case "--health-port":
                    if (!TryInt(args, ref i, arg, 1, 65_535, out int healthPort, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { HealthPort = healthPort };
                    break;

                case "--ready-tick-stall-s":
                    if (!TryInt(args, ref i, arg, 1, 86_400, out int readyStall, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { ReadyTickStallSeconds = readyStall };
                    break;

                case "--gs-host":
                    if (!TryValue(args, ref i, arg, out string? gsHost, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { GameServerHost = gsHost!, UsesGameServer = true };
                    break;

                case "--gs-port":
                    if (!TryInt(args, ref i, arg, 1, 65_535, out int gsPort, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { GameServerPort = gsPort, UsesGameServer = true };
                    break;

                case "--zone":
                    if (!TryValue(args, ref i, arg, out string? zones, out error))
                    {
                        options = result;
                        return false;
                    }

                    // 쉼표로 여러 존. 공백은 버린다 — "--zone a, b" 를 오타로 죽이지 않는다.
                    result = result with
                    {
                        Zones = [.. zones!.Split(',',
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                    };
                    break;

                case "--shard":
                    if (!TryInt(args, ref i, arg, 0, ushort.MaxValue, out int shard, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Shard = shard };
                    break;

                case "--shards":
                    if (!TryValue(args, ref i, arg, out string? shardsPath, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { ShardsPath = shardsPath! };
                    break;

                case "--trace":
                    if (!TryValue(args, ref i, arg, out string? trace, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { TracePath = trace };
                    break;

                case "--scenario":
                    if (!TryValue(args, ref i, arg, out string? scenario, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Scenario = scenario };
                    break;

                case "--masterdata":
                    if (!TryValue(args, ref i, arg, out string? masterData, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { MasterData = masterData! };
                    break;

                case "--planstore":
                    if (!TryValue(args, ref i, arg, out string? planStore, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { PlanStore = planStore! };
                    break;

                case "--npcs":
                    if (!TryInt(args, ref i, arg, 1, 1_000_000, out int npcs, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Npcs = npcs };
                    break;

                case "--time-scale":
                    if (!TryInt(args, ref i, arg, 1, 86_400, out int timeScale, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { TimeScale = timeScale };
                    break;

                case "--days":
                    if (!TryInt(args, ref i, arg, 0, 3_650, out int days, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Days = days, DaysSpecified = true };
                    break;

                case "--hostile-bots":
                    if (!TryInt(args, ref i, arg, 0, 100_000, out int hostileBots, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { HostileBots = hostileBots };
                    break;

                case "--player-bots":
                    if (!TryInt(args, ref i, arg, 0, 10_000, out int bots, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { PlayerBots = bots };
                    break;

                case "--port":
                    if (!TryInt(args, ref i, arg, 1, 65_535, out int port, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Port = port };
                    break;

                case "--bind":
                    if (!TryValue(args, ref i, arg, out string? bind, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Bind = bind! };
                    break;

                case "--config":
                    if (!TryValue(args, ref i, arg, out string? config, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { ConfigPath = config };
                    break;

                case "--snapshot-dir":
                    if (!TryValue(args, ref i, arg, out string? snapshotDir, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { SnapshotDir = snapshotDir! };
                    break;

                case "--snapshot-interval-s":
                    if (!TryInt(args, ref i, arg, 0, 86_400, out int snapshotInterval, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with
                    {
                        SnapshotIntervalSeconds = Math.Max(snapshotInterval, 1),
                        SnapshotEnabled = snapshotInterval > 0,
                        SnapshotSpecified = true,
                    };
                    break;

                case "--memory":
                    if (!TryValue(args, ref i, arg, out string? memoryDir, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { MemoryDir = memoryDir };
                    break;

                case "--memory-ttl-days":
                    if (!TryInt(args, ref i, arg, 0, 3_650, out int memoryTtl, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { MemoryTtlDays = memoryTtl };
                    break;

                case "--snapshot-keep":
                    if (!TryInt(args, ref i, arg, 1, 1_000, out int snapshotKeep, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { SnapshotKeep = snapshotKeep };
                    break;

                case "--restore":
                    if (!TryValue(args, ref i, arg, out string? restore, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = restore switch
                    {
                        "auto" => result with { Restore = RestoreMode.Auto, RestorePath = null },
                        "none" => result with { Restore = RestoreMode.None, RestorePath = null },
                        _ => result with { Restore = RestoreMode.File, RestorePath = restore },
                    };
                    break;

                case "--otlp-endpoint":
                    if (!TryValue(args, ref i, arg, out string? otlp, out error))
                    {
                        options = result;
                        return false;
                    }

                    if (!Uri.TryCreate(otlp, UriKind.Absolute, out _))
                    {
                        error = $"--otlp-endpoint 가 절대 URL 이 아니다: '{otlp}'";
                        options = result;
                        return false;
                    }

                    result = result with { OtlpEndpoint = otlp };
                    break;

                case "--prometheus":
                    result = result with { Prometheus = true, PrometheusSpecified = true };
                    break;

                case "--alarm-webhook":
                    if (!TryValue(args, ref i, arg, out string? webhook, out error))
                    {
                        options = result;
                        return false;
                    }

                    if (!Uri.TryCreate(webhook, UriKind.Absolute, out _))
                    {
                        error = $"--alarm-webhook 이 절대 URL 이 아니다: '{webhook}'";
                        options = result;
                        return false;
                    }

                    result = result with { AlarmWebhook = webhook };
                    break;

                case "--alarm-cooldown-s":
                    if (!TryInt(args, ref i, arg, 0, 86_400, out int cooldown, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { AlarmCooldownSeconds = cooldown };
                    break;

                case "--log-format":
                    if (!TryValue(args, ref i, arg, out string? logFormat, out error)
                        || !Enum.TryParse(logFormat, ignoreCase: true, out LogFormat parsedFormat))
                    {
                        error ??= $"--log-format 값이 잘못됐다: '{logFormat}'. text|json 중 하나다.";
                        options = result;
                        return false;
                    }

                    result = result with { LogFormat = parsedFormat, LogFormatSpecified = true };
                    break;

                case "--link-tls":
                    if (!TryValue(args, ref i, arg, out string? tls, out error)
                        || !Enum.TryParse(tls, ignoreCase: true, out LinkTlsMode tlsMode))
                    {
                        error ??= $"--link-tls 값이 잘못됐다: '{tls}'. off|tls|mtls 중 하나다.";
                        options = result;
                        return false;
                    }

                    result = result with { LinkTls = tlsMode };
                    break;

                case "--link-cert":
                    if (!TryValue(args, ref i, arg, out string? cert, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { LinkCertificate = cert };
                    break;

                case "--link-tls-host":
                    if (!TryValue(args, ref i, arg, out string? tlsHost, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { LinkTlsHost = tlsHost };
                    break;

                case "--billing-cap-usd":
                    if (!TryValue(args, ref i, arg, out string? cap, out error)
                        || !double.TryParse(cap, NumberStyles.Float, CultureInfo.InvariantCulture, out double capUsd)
                        || capUsd < 0)
                    {
                        error ??= $"--billing-cap-usd 값이 잘못됐다: '{cap}'. 0 이상의 실수다.";
                        options = result;
                        return false;
                    }

                    result = result with { BillingCapUsd = capUsd };
                    break;

                case "--budget-individual-share":
                    if (!TryRate(args, ref i, arg, out double share, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { BudgetIndividualShare = share };
                    break;

                case "--billing-reset-hour":
                    if (!TryInt(args, ref i, arg, 0, 23, out int resetHour, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { BillingResetHour = resetHour };
                    break;

                case "--require-link-auth":
                    result = result with { RequireLinkAuth = true };
                    break;

                case "--tick-sync-stall-s":
                    if (!TryInt(args, ref i, arg, 0, 86_400, out int tickStall, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { TickSyncStallSeconds = tickStall };
                    break;

                case "--shutdown-timeout-s":
                    if (!TryInt(args, ref i, arg, 1, 3_600, out int shutdownTimeout, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { ShutdownTimeoutSeconds = shutdownTimeout };
                    break;

                case "--profile":
                    if (!TryValue(args, ref i, arg, out string? profile, out error)
                        || !Enum.TryParse(profile, ignoreCase: true, out HostProfile hostProfile))
                    {
                        error ??= $"--profile 값이 잘못됐다: '{profile}'. dev|service 중 하나다.";
                        options = result;
                        return false;
                    }

                    result = result with { Profile = hostProfile };
                    break;

                case "--weights":
                    if (!TryValue(args, ref i, arg, out string? weights, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Weights = weights };
                    break;

                case "--dynamic-roster":
                    result = result with { DynamicRoster = true };
                    break;

                case "--query-max-streams":
                    if (!TryInt(args, ref i, arg, 0, 1_000, out int maxStreams, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { QueryMaxStreams = maxStreams };
                    break;

                case "--planstore-sha":
                    if (!TryValue(args, ref i, arg, out string? planStoreSha, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { PlanStoreSha = planStoreSha! };
                    break;

                case "--npc-capacity":
                    if (!TryInt(args, ref i, arg, 0, 1_000_000, out int capacity, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { NpcCapacity = capacity };
                    break;

                case "--scan-cap":
                    if (!TryInt(args, ref i, arg, 0, 1_000_000, out int scanCap, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { ScanCap = scanCap };
                    break;

                case "--seed":
                    if (!TryInt(args, ref i, arg, int.MinValue, int.MaxValue, out int seed, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Seed = seed };
                    break;

                case "--fail-rate":
                    if (!TryRate(args, ref i, arg, out double failRate, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { FailRate = failRate };
                    break;

                case "--drop-rate":
                    if (!TryRate(args, ref i, arg, out double dropRate, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { DropRate = dropRate };
                    break;

                default:
                    error = $"모르는 인자다: {arg}";
                    options = result;
                    return false;
            }
        }

        if (result.Link == LinkKind.Replay && result.TracePath is null)
        {
            error = "--link replay 에는 --trace <path> 가 필요하다.";
            options = result;
            return false;
        }

        if (result.LinkTls == LinkTlsMode.Mtls && result.LinkCertificate is null)
        {
            error = "--link-tls mtls 에는 --link-cert <pfx> 가 필요하다.";
            options = result;
            return false;
        }

        // 서비스 프로파일은 무제한 실행을 강제한다 (A-04). --days 1 기본값을 그대로 두면
        // 배속 60 에서 실시간 24분 뒤 exit 0 으로 조용히 사라진다.
        if (result.Profile == HostProfile.Service)
        {
            result = result with
            {
                Days = 0,
                SnapshotEnabled = result.SnapshotSpecified ? result.SnapshotEnabled : true,

                // 운영은 수집기가 읽고 사람은 안 읽는다 (A-05).
                Prometheus = result.PrometheusSpecified ? result.Prometheus : true,
                LogFormat = result.LogFormatSpecified ? result.LogFormat : LogFormat.Json,
            };
        }

        options = result;
        return true;
    }

    /// <summary>
    /// 세 소스를 합쳐 파싱한다 (A-04). 우선순위 <b>CLI &gt; 환경변수 &gt; 설정 파일 &gt; 기본값</b>.
    ///
    /// 합성 argv 를 앞에 붙이는 방식이다 — 파서는 하나뿐이고, 뒤에 온 값이 앞의 값을 덮는
    /// 성질만으로 우선순위가 성립한다.
    /// </summary>
    /// <param name="args">실제 명령줄 인자.</param>
    /// <param name="env">환경변수. null 이면 현재 프로세스의 것을 읽는다.</param>
    /// <param name="options">파싱 결과.</param>
    /// <param name="error">실패 사유.</param>
    public static bool TryParseLayered(
        string[] args,
        IReadOnlyDictionary<string, string?>? env,
        out HostOptions options,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        options = new HostOptions();
        env ??= HostOptionsSource.CurrentEnvironment();

        // 파일 경로와 프로파일은 CLI·환경변수로만 정한다. 설정 파일이 자기 경로를 정하는
        // 순환은 만들지 않는다.
        string[] envArgs = HostOptionsSource.FromEnv(env);

        if (!TryParse([.. envArgs, .. args], out HostOptions bootstrap, out error))
        {
            return false;
        }

        if (bootstrap.Help)
        {
            options = bootstrap;
            return true;
        }

        string? path = HostOptionsSource.Locate(
            bootstrap.ConfigPath, HostOptionsSource.DefaultFileName);

        if (bootstrap.ConfigPath is not null && path is null)
        {
            error = $"설정 파일을 찾지 못했다: {bootstrap.ConfigPath}";
            return false;
        }

        string[] fileArgs = [];

        if (path is not null && !HostOptionsSource.TryFromFile(
                path,
                bootstrap.Profile.ToString().ToLowerInvariant(),
                out fileArgs,
                out error))
        {
            return false;
        }

        if (!TryParse([.. fileArgs, .. envArgs, .. args], out options, out error))
        {
            return false;
        }

        options = options with { LoadedConfigPath = path };
        return true;
    }

    /// <summary>
    /// 바인드 주소가 허용되는지 본다 (A-04).
    ///
    /// 와일드카드 바인드는 관리 토큰이 있을 때만 연다. 토큰 없이 <c>0.0.0.0</c> 을 허용하면
    /// 킬스위치·리로드를 누구나 부를 수 있는 포트가 열린다.
    /// </summary>
    /// <param name="adminToken">관리 토큰. 없으면 null.</param>
    /// <param name="error">실패 사유.</param>
    public bool TryValidateBind(string? adminToken, out string? error)
    {
        error = null;

        bool wildcard = Bind is "0.0.0.0" or "*" or "[::]" or "::";

        if (wildcard && string.IsNullOrEmpty(adminToken))
        {
            error =
                $"--bind {Bind} 는 NPC_ADMIN_TOKEN 없이는 열지 않는다. "
                + "토큰을 주거나 --bind 127.0.0.1 로 둔다.";

            return false;
        }

        return true;
    }

    private static bool TryValue(string[] args, ref int i, string name, out string? value, out string? error)
    {
        if (i + 1 >= args.Length)
        {
            value = null;
            error = $"{name} 에 값이 없다.";
            return false;
        }

        value = args[++i];
        error = null;
        return true;
    }

    private static bool TryInt(string[] args, ref int i, string name, int min, int max, out int value, out string? error)
    {
        value = 0;

        if (!TryValue(args, ref i, name, out string? text, out error))
        {
            return false;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = $"{name} 값이 정수가 아니다: '{text}'";
            return false;
        }

        if (value < min || value > max)
        {
            error = $"{name} 값이 {min}~{max} 범위를 벗어난다: {value}";
            return false;
        }

        return true;
    }

    private static bool TryRate(string[] args, ref int i, string name, out double value, out string? error)
    {
        value = 0;

        if (!TryValue(args, ref i, name, out string? text, out error))
        {
            return false;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            error = $"{name} 값이 실수가 아니다: '{text}'";
            return false;
        }

        if (value is < 0 or > 1)
        {
            error = $"{name} 값이 0~1 범위를 벗어난다: {value}";
            return false;
        }

        return true;
    }
}
