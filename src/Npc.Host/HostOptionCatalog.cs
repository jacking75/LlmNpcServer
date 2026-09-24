using System.Text;

namespace Npc.Host;

/// <summary>호스트 옵션의 도움말·마크다운 문서가 공유하는 단일 표.</summary>
public static class HostOptionCatalog
{
    /// <summary>도움말 그룹.</summary>
    public enum Group { Basic, Link, Llm, Persistence, Observe, Security, Development }

    /// <summary>옵션 한 줄.</summary>
    public sealed record Entry(Group Group, string Option, string Argument, string Description);

    /// <summary>표시 순서가 도움말 순서다.</summary>
    public static readonly Entry[] Options =
    [
        new(Group.Basic, "--loopback", "", "인프로세스 게임 월드에 직결한다 (기본)"),
        new(Group.Basic, "--npcs", "N", "NPC 수 (기본 500)"),
        new(Group.Basic, "--time-scale", "N", "시간 압축 (기본 60)"),
        new(Group.Basic, "--days", "N", "게임 일수. 0은 무제한 (기본 1)"),
        new(Group.Basic, "--masterdata", "<dir>", "마스터데이터 폴더 (기본 ./masterdata)"),
        new(Group.Basic, "--planstore", "<dir>", "플랜 스토어 (기본 ./planstore)"),
        new(Group.Basic, "--port", "N", "대시보드 포트 (기본 5080)"),
        new(Group.Basic, "--bind", "<addr>", "웹 바인드 주소 (기본 127.0.0.1)"),
        new(Group.Basic, "--profile", "dev|service", "실행 프로파일. service는 무제한 실행"),
        new(Group.Basic, "--scenario", "<jsonl>", "시나리오 이벤트 주입"),
        new(Group.Basic, "--config", "<path>", "설정 파일 (기본 npc.settings.json 자동 탐색)"),
        new(Group.Basic, "--no-dashboard", "", "대시보드 없이 실행한다"),

        new(Group.Link, "--link", "null|record|replay|loopback|tcp", "게임서버 링크 종류"),
        new(Group.Link, "--trace", "<path>", "기록 또는 재생 파일 (jsonl)"),
        new(Group.Link, "--gs-host", "<host>", "게임서버 주소 (기본 127.0.0.1)"),
        new(Group.Link, "--gs-port", "N", "게임서버 링크 포트 (기본 7010)"),
        new(Group.Link, "--zone", "<id>[,<id>]", "로스터 존 필터. 게임서버와 같아야 한다"),
        new(Group.Link, "--shard", "N", "담당 샤드. 0은 단일 샤드"),
        new(Group.Link, "--shards", "<path>", "샤드 정의 파일 (기본 deploy/shards.json)"),
        new(Group.Link, "--dynamic-roster", "", "실행 중 NPC 스폰·디스폰 허용"),
        new(Group.Link, "--npc-capacity", "N", "동적 로스터 슬롯 상한"),

        new(Group.Llm, "--tier", "none|t1|t2|all", "사용할 LLM 티어 (기본 none)"),
        new(Group.Llm, "--no-llm", "", "LLM을 호출하지 않는다"),
        new(Group.Llm, "--t1-engine", "<id>", "로컬 엔진 ID"),
        new(Group.Llm, "--t2-engine", "<id>", "외부 엔진 ID"),
        new(Group.Llm, "--t1-workers", "N", "로컬 워커 수 (기본 2)"),
        new(Group.Llm, "--t2-workers", "N", "외부 워커 수 (기본 8)"),
        new(Group.Llm, "--billing-cap-usd", "<n>", "하루 외부 비용 상한 USD. 0은 끔"),
        new(Group.Llm, "--billing-reset-hour", "N", "UTC 청구일 경계 시각 (기본 0)"),
        new(Group.Llm, "--budget-individual-share", "<0~1>", "개체 재계획의 T2 예산 몫"),

        new(Group.Persistence, "--snapshot-dir", "<dir>", "상태 스냅샷 폴더 (기본 ./state)"),
        new(Group.Persistence, "--snapshot-interval-s", "N", "스냅샷 주기 초. 0은 끔"),
        new(Group.Persistence, "--snapshot-keep", "N", "보존할 스냅샷 수 (기본 3)"),
        new(Group.Persistence, "--restore", "auto|none|<path>", "복원 정책 (기본 auto)"),
        new(Group.Persistence, "--memory", "<dir>", "NPC 기억·관계 저장소 폴더"),
        new(Group.Persistence, "--memory-ttl-days", "N", "기억 보존 게임 일. 0은 무제한"),
        new(Group.Persistence, "--planstore-sha", "<sha8>", "특정 프리픽스 회차 플랜을 선택"),

        new(Group.Observe, "--prometheus", "", "Prometheus 메트릭 라우트 활성화"),
        new(Group.Observe, "--otlp-endpoint", "<url>", "OpenTelemetry 수집기 주소"),
        new(Group.Observe, "--alarm-webhook", "<url>", "경보 웹훅 주소"),
        new(Group.Observe, "--alarm-cooldown-s", "N", "같은 경보의 재발화 간격 초"),
        new(Group.Observe, "--log-format", "text|json", "로그 형식 (기본 text)"),
        new(Group.Observe, "--health-port", "N", "헬스 프로브 전용 포트"),
        new(Group.Observe, "--live-stall-s", "N", "live 프로브의 루프 정지 상한"),
        new(Group.Observe, "--ready-tick-stall-s", "N", "ready 프로브의 틱 정지 상한"),
        new(Group.Observe, "--query-max-streams", "N", "동시 조회 스트림 수 (기본 8)"),

        new(Group.Security, "--link-tls", "off|tls|mtls", "게임서버 링크 암호화"),
        new(Group.Security, "--link-cert", "<pfx>", "mTLS 클라이언트 인증서"),
        new(Group.Security, "--link-tls-host", "<name>", "TLS SNI 이름"),
        new(Group.Security, "--require-link-auth", "", "인증 없는 게임서버 연결을 거절"),

        new(Group.Development, "--dev-control", "", "데모용 제어 API 활성화"),
        new(Group.Development, "--watch", "", "플랜·데이터 변경 시 자동 리로드"),
        new(Group.Development, "--fail-rate", "<0~1>", "게임 대역의 액션 실패 주입"),
        new(Group.Development, "--drop-rate", "<0~1>", "게임 대역의 명령 유실 주입"),
        new(Group.Development, "--player-bots", "N", "가상 플레이어 수 (기본 20)"),
        new(Group.Development, "--hostile-bots", "N", "적대 가상 플레이어 수"),
        new(Group.Development, "--seed", "N", "게임 대역 난수 시드"),
        new(Group.Development, "--weights", "A|B|C|D", "재계획 점수 가중치 세트"),
        new(Group.Development, "--scan-cap", "N", "틱당 인지 스캔 상한"),
        new(Group.Development, "--max-speed", "", "10Hz 페이싱 없이 측정"),
        new(Group.Development, "--on-link-fault", "exit|wait", "링크 고장 정책 (기본 exit)"),
        new(Group.Development, "--fault-grace-s", "N", "링크 고장 후 종료 유예 초"),
        new(Group.Development, "--shutdown-timeout-s", "N", "정상 종료 시간 상한"),
        new(Group.Development, "--tick-sync-stall-s", "N", "게임 틱 동기화 정지 상한"),
    ];

    private static readonly (Group Id, string Title)[] s_groups =
    [
        (Group.Basic, "기본 실행"), (Group.Link, "게임서버 연결"), (Group.Llm, "LLM"),
        (Group.Persistence, "저장·복원"), (Group.Observe, "관측·경보"),
        (Group.Security, "보안"), (Group.Development, "측정·개발용"),
    ];

    /// <summary>같은 표로 짧은 도움말·전체 도움말·마크다운을 만든다.</summary>
    public static string Render(bool all, bool markdown = false)
    {
        var text = new StringBuilder(8_192);
        if (markdown) text.AppendLine("# Npc.Host 실행 옵션").AppendLine();
        else text.AppendLine("사용법: Npc.Host [doctor|validate|healthcheck|hints|schema] [옵션]").AppendLine();

        foreach ((Group group, string title) in s_groups)
        {
            if (!all && group > Group.Llm) break;
            text.AppendLine(markdown ? $"## {title}" : title);
            if (markdown) text.AppendLine().AppendLine("| 옵션 | 설명 |").AppendLine("|---|---|");
            foreach (Entry entry in Options.Where(option => option.Group == group))
            {
                string option = entry.Option + (entry.Argument.Length > 0 ? " " + entry.Argument : "");
                text.AppendLine(markdown
                    ? $"| `{option}` | {entry.Description} |"
                    : $"  {option,-37} {entry.Description}");
            }
            text.AppendLine();
        }

        if (!markdown) text.AppendLine(all ? "--help-all --markdown 으로 옵션 문서를 만든다."
            : "전체 옵션은 --help-all 로 본다.");
        return text.ToString().TrimEnd() + Environment.NewLine;
    }
}
