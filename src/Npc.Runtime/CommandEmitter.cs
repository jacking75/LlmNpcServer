using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;

namespace Npc.Runtime;

/// <summary>
/// 명령 발행에 필요한 개체 정보. 값 타입이라 발행 경로에 할당이 없다.
/// </summary>
/// <param name="Npc">발행 대상.</param>
/// <param name="Archetype">아키타입.</param>
/// <param name="Now">현재 틱.</param>
/// <param name="Correlation">이 스텝의 상관 ID.</param>
/// <param name="Home">인스턴스의 home_poi.</param>
/// <param name="Workplace">인스턴스의 workplace_poi.</param>
/// <param name="Current">현재 POI.</param>
/// <param name="Zone">현재 존.</param>
/// <param name="Target">
/// 이벤트가 알려준 대상 NPC. 인터럽트가 <c>$threat</c> 를 쓸 때만 채워진다 —
/// 위협의 정체는 규칙이 아니라 이벤트에 있다. 평시 플랜에서는 default 다.
/// </param>
/// <param name="Instance">
/// 이 NPC 가 있는 채널·인스턴스 (B-02). <c>NpcSpawned</c> 가 알려준 값 그대로다 —
/// 발행기는 해석하지 않고 <b>모든 명령에 찍기만</b> 한다.
/// </param>
/// <param name="HostilePlayer">
/// 최근에 적대로 판정된 플레이어 (B-06). 0 = 없음.
///
/// <b>플랜에 플레이어 id 를 박을 수 없다</b> — 회차마다 다르다. 그래서
/// <c>nearest:hostile_player</c> 는 발행 시점에 이 값을 읽는다.
/// </param>
public readonly record struct EmitContext(
    NpcId Npc,
    ArchetypeId Archetype,
    Tick Now,
    CorrelationId Correlation,
    PoiId Home,
    PoiId Workplace,
    PoiId Current,
    ZoneId Zone,
    NpcId Target = default,
    InstanceId Instance = default,
    PlayerId HostilePlayer = default);

/// <summary>
/// 컴파일된 스텝을 <see cref="NpcCommand"/> 로 바꾼다. docs/03 §6 · docs/01 §2.1 <c>emits</c>.
///
/// <b>액션 → 명령 매핑은 actions.json 의 emits 가 결정한다.</b> 여기에는 액션 이름이 없다.
/// <b><c>IEnumerable</c> 을 반환하지 않는다</b> — 호출자가 준 <see cref="Span{T}"/> 에 쓴다 (docs/11 §9).
/// </summary>
public sealed class CommandEmitter
{
    /// <summary>한 스텝이 발행할 수 있는 최대 명령 수. 호출자의 버퍼 크기 기준이다.</summary>
    public const int MaxCommandsPerStep = 4;

    private readonly MasterDataSet _data;
    private readonly PoiBinder _binder;

    /// <summary>발행기를 만든다. 기동 시 1회.</summary>
    public CommandEmitter(MasterDataSet data, PoiBinder binder)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(binder);

        _data = data;
        _binder = binder;
    }

    /// <summary>
    /// 스텝 하나가 낼 명령을 <paramref name="destination"/> 에 쓴다.
    /// </summary>
    /// <returns>쓴 명령 수. 버퍼가 모자라면 거기까지만 쓴다.</returns>
    public int Emit(
        in CompiledStep step,
        in EmitContext ctx,
        ReadOnlySpan<int> inventory,
        Span<NpcCommand> destination)
    {
        ActionDef action = _data.Actions[step.Action];
        var bindContext = new PoiBindContext(ctx.Npc, ctx.Archetype, ctx.Home, ctx.Workplace, ctx.Current);

        PoiId boundPoi = default;
        if (step.Poi != PoiSymbol.None)
        {
            _binder.TryBind(step.Poi, bindContext, out boundPoi);
        }

        int written = 0;

        foreach (EmitDef emit in action.Emits)
        {
            if (written >= destination.Length)
            {
                break;
            }

            var command = new NpcCommand
            {
                Kind = emit.Command,
                Npc = ctx.Npc,
                IssuedAt = ctx.Now,
                Correlation = ctx.Correlation,
                Priority = emit.Priority,

                // B-02 — 인스턴스는 통과만 한다. Faction·ExtA·ExtB 는 아직 아무도 채우지
                // 않으므로 default(0) 로 둔다. 0 이 아니면 Ext_ZeroForUndefinedKinds 가 깨진다.
                Instance = ctx.Instance,
            };

            foreach (EmitMapping mapping in emit.Map)
            {
                command = Apply(command, mapping, step, ctx, boundPoi, inventory);
            }

            destination[written++] = command;
        }

        return written;
    }

    private NpcCommand Apply(
        in NpcCommand command,
        in EmitMapping mapping,
        in CompiledStep step,
        in EmitContext ctx,
        PoiId boundPoi,
        ReadOnlySpan<int> inventory)
    {
        int number = mapping.Source switch
        {
            EmitSource.LiteralNumber or EmitSource.LiteralSymbol => mapping.Number,
            EmitSource.StepPoi => boundPoi.Value,
            EmitSource.StepItem => step.Item.Value,
            EmitSource.StepCount => step.Count,
            EmitSource.StepArgFlags => mapping.ArgMap.IsEmpty || step.ArgFlags >= mapping.ArgMap.Length
                ? step.ArgFlags
                : mapping.ArgMap[step.ArgFlags],
            EmitSource.StepNpcRef => 0,
            EmitSource.InstanceHome => ctx.Home.Value,
            EmitSource.InstanceWorkplace => ctx.Workplace.Value,
            EmitSource.InstanceSelf => ctx.Npc.Value,
            EmitSource.InstanceZone => ctx.Zone.Value,
            EmitSource.FirstWithFlag => FirstItemWith(mapping.Flag, inventory),
            _ => 0,
        };

        return mapping.Field switch
        {
            CommandField.TargetPoi => command with { TargetPoi = new PoiId((ushort)number) },
            CommandField.TargetNpc => ApplyNpcRef(command, mapping, step, ctx, number),
            CommandField.TargetPlayer => command with { TargetPlayer = new PlayerId(number) },
            CommandField.Item => command with { Item = new ItemId((ushort)number) },
            CommandField.Amount => command with { Amount = number },
            CommandField.Animation => command with { Animation = new AnimationId((ushort)number) },
            CommandField.Dialogue => command with { Dialogue = new DialogueId((ushort)number) },
            CommandField.Visual => command with { Visual = (VisualState)number },
            CommandField.Archetype => command with { Archetype = new ArchetypeId((ushort)number) },
            CommandField.Zone => command with { Zone = new ZoneId((ushort)number) },
            CommandField.Flags => command with { Flags = (byte)number },
            CommandField.TargetPos => command with { TargetPos = PositionOf(new PoiId((ushort)number)) },
            _ => command,
        };
    }

    /// <summary>
    /// npc_ref 심볼을 명령에 싣는다.
    /// <c>nearest:&lt;archetype&gt;</c> 는 <b>대상을 여기서 고르지 않는다</b> — 근접 판정은 게임서버 소관이라
    /// 아키타입만 실어 보낸다 (docs/02 §0 의 관점).
    /// </summary>
    private static NpcCommand ApplyNpcRef(
        in NpcCommand command, in EmitMapping mapping, in CompiledStep step, in EmitContext ctx, int number)
    {
        if (mapping.Source != EmitSource.StepNpcRef)
        {
            return command with { TargetNpc = new NpcId(number) };
        }

        return NpcRefCodes.KindOf(step.NpcRef) switch
        {
            NpcRefKind.None => command with { TargetNpc = ctx.Target },
            NpcRefKind.Self => command with { TargetNpc = ctx.Npc },
            NpcRefKind.NearestArchetype => command with
            {
                Archetype = new ArchetypeId((ushort)NpcRefCodes.PayloadOf(step.NpcRef)),
            },
            // B-06 — 대상은 플랜이 아니라 런타임 상태에서 온다. 플레이어 id 는 회차마다
            // 다르므로 플랜에 박을 수 없다. TargetNpc 는 0 으로 둔다 — 둘 다 채우면
            // 게임서버가 어느 쪽을 공격할지 모른다.
            NpcRefKind.HostilePlayer => command with
            {
                TargetPlayer = ctx.HostilePlayer,
                TargetNpc = default,
            },
            NpcRefKind.PoiOwner => command with { TargetPoi = command.TargetPoi },
            _ => command,
        };
    }

    private WorldPos PositionOf(PoiId poi) => poi.Value == 0 ? default : _data.Pois[poi].Pos;

    /// <summary>
    /// 이 플래그를 세우는 인벤토리 아이템 중 첫 번째. <c>Eat</c>/<c>Drink</c> 가 쓴다.
    /// 코드가 "빵"을 아는 것이 아니라 items.json 의 grants 를 훑는다 (docs/01 §3).
    /// </summary>
    private int FirstItemWith(WorldFlags flag, ReadOnlySpan<int> inventory)
    {
        for (int code = 0; code < inventory.Length; code++)
        {
            if (inventory[code] > 0 && (_data.Items.GrantsOf(new ItemId((ushort)code)) & flag) != 0)
            {
                return code;
            }
        }

        return 0;
    }
}
