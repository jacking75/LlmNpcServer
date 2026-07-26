using Npc.Llm;

namespace Npc.Prebake;

/// <summary>
/// 예산 하드 캡. docs/13 §4 · 리스크 R8.
///
/// <b>버그로 재계획 루프가 도는 사고의 1차 방어선이다.</b> 캡을 넘으면 회차를 중단하고
/// manifest 를 <b>부분 상태</b>로 기록한다 — <c>--resume</c> 이 그 상태를 보고 이어서 돈다.
///
/// <para>
/// <b>캡을 우회하는 코드를 만들지 않는다</b> (CLAUDE.md §2.7). 초과 시 중단이고,
/// "조금 넘겨도 되겠지" 로 열어 두는 문을 만들지 않는다.
/// </para>
///
/// <para>
/// 판정은 <b>던지기 전에</b> 한다. 실비용은 응답을 받고서야 알 수 있으니 추정치로 예약하고,
/// 실측이 들어오면 추정치를 그것으로 갱신한다 — 회차가 진행될수록 추정이 정확해진다.
/// </para>
/// </summary>
public sealed class BudgetGuard
{
    private readonly Lock _gate = new();
    private readonly double _cap;
    private readonly double _seedEstimate;

    private double _spent;
    private double _estimate;
    private int _samples;
    private bool _exhausted;

    /// <summary>가드를 만든다.</summary>
    /// <param name="capUsd">하드 캡(USD). 0 이면 제한이 없다.</param>
    /// <param name="estimatePerCallUsd">
    /// 요청 하나의 초기 추정 비용. 실측이 들어오면 평균으로 갱신된다.
    /// <see cref="EstimateFor"/> 로 엔진 단가에서 뽑는 것이 좋다.
    /// </param>
    public BudgetGuard(double capUsd, double estimatePerCallUsd = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capUsd);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatePerCallUsd);

        _cap = capUsd;
        _seedEstimate = estimatePerCallUsd;
        _estimate = estimatePerCallUsd;
    }

    /// <summary>하드 캡(USD). 0 = 무제한.</summary>
    public double CapUsd => _cap;

    /// <summary>제한이 없는가.</summary>
    public bool Unlimited => _cap <= 0;

    /// <summary>지금까지 쓴 비용(USD).</summary>
    public double SpentUsd
    {
        get
        {
            lock (_gate)
            {
                return _spent;
            }
        }
    }

    /// <summary>남은 예산(USD). 무제한이면 <see cref="double.PositiveInfinity"/>.</summary>
    public double RemainingUsd
    {
        get
        {
            lock (_gate)
            {
                return Unlimited ? double.PositiveInfinity : Math.Max(0, _cap - _spent);
            }
        }
    }

    /// <summary>캡에 걸려 중단됐는가. manifest 의 <c>partial</c> 이 이 값이다.</summary>
    public bool Exhausted
    {
        get
        {
            lock (_gate)
            {
                return _exhausted;
            }
        }
    }

    /// <summary>요청 하나의 현재 추정 비용(USD). 실측이 쌓이면 평균으로 수렴한다.</summary>
    public double EstimatePerCallUsd
    {
        get
        {
            lock (_gate)
            {
                return _estimate;
            }
        }
    }

    /// <summary>
    /// 엔진 단가와 프리픽스 크기로 요청 하나의 비용을 추정한다.
    /// <b>캐시가 전부 적중한다고 보지 않는다</b> — 추정이 낙관적이면 캡을 넘긴 뒤에야 멈춘다.
    /// </summary>
    /// <param name="engine">엔진 설정.</param>
    /// <param name="prefixTokens">프리픽스 토큰 수.</param>
    /// <param name="suffixTokens">서픽스 토큰 수. 예산은 300 이다 (CLAUDE.md §2.5).</param>
    /// <param name="completionTokens">출력 토큰 추정. 플랜 하나는 300~700 이다.</param>
    /// <param name="assumedCacheHitRate">가정하는 캐시 적중률. 보수적으로 0 이 기본이다.</param>
    public static double EstimateFor(
        LlmEngineOptions engine,
        int prefixTokens,
        int suffixTokens = 300,
        int completionTokens = 700,
        double assumedCacheHitRate = 0)
    {
        ArgumentNullException.ThrowIfNull(engine);

        long prompt = prefixTokens + suffixTokens;
        long cached = (long)(prompt * Math.Clamp(assumedCacheHitRate, 0, 1));

        return engine.CostUsd(prompt, cached, completionTokens);
    }

    /// <summary>
    /// 요청 <paramref name="count"/> 개를 던져도 되는가. 거짓이면 <b>중단</b>이다.
    /// 한 번 거짓이 나오면 <see cref="Exhausted"/> 가 선다 — 회차는 여기서 끝난다.
    /// </summary>
    public bool TryReserve(int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        lock (_gate)
        {
            if (Unlimited)
            {
                return true;
            }

            if (_exhausted)
            {
                return false;
            }

            if (_spent + (_estimate * count) > _cap)
            {
                _exhausted = true;
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// 실비용을 반영한다. 추정치도 실측 평균으로 갱신한다.
    /// 여기서 캡을 넘었으면 다음 <see cref="TryReserve"/> 가 거짓이다.
    /// </summary>
    public void Record(double actualUsd)
    {
        if (actualUsd < 0)
        {
            return;
        }

        lock (_gate)
        {
            _spent += actualUsd;
            _samples++;

            // 실측 평균으로 추정을 갱신한다. 비용 0 인 로컬 엔진에서는 초기 추정을 유지한다 —
            // 0 으로 내려가면 캡이 사실상 사라진다.
            double average = _spent / _samples;

            _estimate = average > 0 ? average : _seedEstimate;

            if (!Unlimited && _spent >= _cap)
            {
                _exhausted = true;
            }
        }
    }

    /// <summary>중단 로그 한 줄. docs/13 §4 의 문구다.</summary>
    public string StopMessage(int done, int total) =>
        $"예산 초과. 생성 {done}/{total} 에서 중단. --resume 으로 이어서 실행 가능 "
        + $"(캡 ${_cap:F2} · 사용 ${SpentUsd:F4})";
}
