namespace SodAudit;

public enum ConflictStatus
{
    Structural,        // 结构性冲突：与代理无关，长期存在
    DelegationApproved, // 仅因业务已批准的临时代理产生 —— 记录但不告警
    DelegationUnapproved // 仅因未批准的临时代理产生 —— 限期告警
}

public sealed class Conflict
{
    public required SodRule Rule { get; init; }
    public required string UserId { get; init; }
    public required ConflictStatus Status { get; set; }
    /// <summary>规则中每个权限 → 授予它的来源列表（冲突证据链）</summary>
    public required Dictionary<string, List<GrantSource>> Evidence { get; init; }
    /// <summary>仅当代理由来时，相关的代理 Id</summary>
    public List<string> DelegationIds { get; } = new();
    public List<Remediation> Fixes { get; } = new();
}

public sealed class CollusionRisk
{
    public required SodRule Rule { get; init; }
    public required string UserA { get; init; }
    public required string UserB { get; init; }
    public required RelationEdge Relation { get; init; }
    /// <summary>各方贡献的权限</summary>
    public required Dictionary<string, List<string>> Contributions { get; init; } // userId -> perms
}

/// <summary>一条最小修正建议</summary>
public sealed record Remediation(
    string StrippedPermission,          // 建议剥夺规则中的哪个权限
    List<GrantSource> Sources,              // 需要撤销的授权来源（全部撤销才能剥夺该权限）
    string Note);

public sealed class ConflictDetector
{
    private readonly OrgDirectory _dir;
    private readonly DateOnly _asOf;

    public ConflictDetector(OrgDirectory dir, DateOnly asOf)
    {
        _dir = dir;
        _asOf = asOf;
    }

    /// <summary>单人冲突检测</summary>
    public List<Conflict> DetectSingleUser(IReadOnlyDictionary<string, EffectivePermissions> all)
    {
        var conflicts = new List<Conflict>();
        foreach (var (userId, eff) in all)
        {
            foreach (var rule in _dir.SodRules)
            {
                if (!rule.PermissionIds.All(eff.Has)) continue;

                var evidence = rule.PermissionIds.ToDictionary(
                    p => p, p => eff.ByPermission[p].ToList());

                var c = new Conflict
                {
                    Rule = rule, UserId = userId,
                    Status = ConflictStatus.Structural,
                    Evidence = evidence
                };
                Classify(c);
                c.Fixes.AddRange(ComputeMinimalFixes(eff, rule));
                conflicts.Add(c);
            }
        }
        return conflicts;
    }

    /// <summary>
    /// 冲突分类：若把所有"代理来源"的授权都拿掉后冲突消失，
    /// 说明冲突仅由临时代理造成；再按代理是否经业务批准分级。
    /// </summary>
    private void Classify(Conflict c)
    {
        var delegationIds = c.Evidence.Values
            .SelectMany(s => s)
            .Where(s => s.Kind == GrantKind.Delegation)
            .Select(s => s.SourceId)
            .Distinct()
            .ToList();

        if (delegationIds.Count == 0) return; // 与代理无关 → 结构性

        // 检查：去掉所有代理来源后，规则中是否每个权限仍至少有一条非代理来源
        var stillCovered = c.Rule.PermissionIds.All(p =>
            c.Evidence[p].Any(s => s.Kind != GrantKind.Delegation));
        if (stillCovered) return; // 代理只是叠加，冲突本身结构性存在

        c.Status = delegationIds
            .Select(id => _dir.Delegations.First(d => $"Delegation:{d.Id}" == id))
            .All(d => d.BusinessApproved)
            ? ConflictStatus.DelegationApproved
            : ConflictStatus.DelegationUnapproved;
        c.DelegationIds.AddRange(delegationIds);
    }

    /// <summary>
    /// 最小修正：要打破冲突，只需剥夺规则中【任意一个】权限。
    /// 剥夺某权限的代价 = 撤销授予它的全部可撤销单元（同一单元可能同时授予多个权限）。
    /// 对每个权限计算代价，取最小者为主建议，其余为备选。
    /// </summary>
    private List<Remediation> ComputeMinimalFixes(EffectivePermissions eff, SodRule rule)
    {
        var options = new List<Remediation>();
        foreach (var perm in rule.PermissionIds)
        {
            var sources = eff.ByPermission[perm];
            // 代理来源会在到期后自动消失，单独提示
            var hasDelegation = sources.Any(s => s.Kind == GrantKind.Delegation);
            var note = hasDelegation
                ? "含临时代理来源：可选择立即终止代理，或待代理到期后自动消除"
                : "需撤销以下全部授权来源才能剥夺该权限";
            options.Add(new Remediation(perm, sources.ToList(), note));
        }
        // 按"需要撤销的授权条数"升序，最少的就是最小修正
        return options.OrderBy(o => o.Sources.Count)
                      .ThenBy(o => o.StrippedPermission)
                      .ToList();
    }

    /// <summary>
    /// 串通风险：只检查存在关系边（上下级/同组/亲属利益）的用户对。
    /// 两人权限并集凑齐规则、且各自单独都不构成该冲突时，记为串通风险。
    /// </summary>
    public List<CollusionRisk> DetectCollusion(
        IReadOnlyDictionary<string, EffectivePermissions> all,
        IReadOnlyCollection<Conflict> singleConflicts)
    {
        var alreadyConflicted = singleConflicts
            .Where(c => c.Status != ConflictStatus.DelegationApproved)
            .Select(c => (c.UserId, c.Rule.Id))
            .ToHashSet();

        var risks = new List<CollusionRisk>();
        foreach (var edge in _dir.Relations)
        {
            var a = all[edge.UserA];
            var b = all[edge.UserB];
            foreach (var rule in _dir.SodRules)
            {
                // 任一方单独已构成冲突的，由单人报告覆盖，不重复记串通
                if (alreadyConflicted.Contains((edge.UserA, rule.Id)) ||
                    alreadyConflicted.Contains((edge.UserB, rule.Id)))
                    continue;

                var union = new HashSet<string>(a.ByPermission.Keys);
                union.UnionWith(b.ByPermission.Keys);
                if (!rule.PermissionIds.All(union.Contains)) continue;

                risks.Add(new CollusionRisk
                {
                    Rule = rule,
                    UserA = edge.UserA,
                    UserB = edge.UserB,
                    Relation = edge,
                    Contributions = new Dictionary<string, List<string>>
                    {
                        [edge.UserA] = rule.PermissionIds.Where(a.Has).ToList(),
                        [edge.UserB] = rule.PermissionIds.Where(b.Has).ToList()
                    }
                });
            }
        }
        return risks;
    }
}
