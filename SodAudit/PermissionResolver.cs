namespace SodAudit;

/// <summary>授权来源类别（每条都是可撤销的最小单元）</summary>
public enum GrantKind { DirectRole, DepartmentGrant, Delegation, ApprovalFlow }

/// <summary>
/// 一条授权来源记录：谁、通过哪条路径、获得了什么。
/// SourceId 是"可撤销单元"的标识（如某个部门授权、某条代理、某条审批流规则）。
/// </summary>
public sealed record GrantSource(
    GrantKind Kind,
    string SourceId,     // 可撤销单元 Id：RoleAssignment:{user}:{role} / DeptGrant:{id} / Delegation:{id} / Flow:{id}
    string Description); // 人类可读路径，如 "角色继承: 采购经理 → 供应商管理 → Vendor.Create"

public sealed record GrantRecord(string PermissionId, GrantSource Source);

/// <summary>某用户的有效权限全集 + 每个权限的全部来源（可能多条叠加）</summary>
public sealed class EffectivePermissions
{
    public required string UserId { get; init; }
    /// <summary>PermissionId → 授予它的所有来源</summary>
    public Dictionary<string, List<GrantSource>> ByPermission { get; } = new();

    public bool Has(string permissionId) => ByPermission.ContainsKey(permissionId);

    public void Add(string permissionId, GrantSource source)
    {
        if (!ByPermission.TryGetValue(permissionId, out var list))
            ByPermission[permissionId] = list = new();
        // 同一可撤销单元只记一次
        if (list.All(s => s.SourceId != source.SourceId))
            list.Add(source);
    }
}

/// <summary>
/// 权限解析器：把 角色继承 / 部门授权 / 临时代理 / 审批流配置 四层叠加
/// 解析成每个用户的有效权限集合，并保留每条权限的来源链。
/// </summary>
public sealed class PermissionResolver
{
    private readonly OrgDirectory _dir;
    private readonly DateOnly _asOf;

    public PermissionResolver(OrgDirectory dir, DateOnly asOf)
    {
        _dir = dir;
        _asOf = asOf;
    }

    /// <summary>展开角色继承 DAG，返回 (权限Id, 继承链描述) 列表</summary>
    public IEnumerable<(string PermissionId, string Chain)> ExpandRole(string roleId)
    {
        var visited = new HashSet<string>();
        var stack = new Stack<(string RoleId, string Path)>();
        stack.Push((roleId, _dir.Roles[roleId].Name));

        while (stack.Count > 0)
        {
            var (rid, path) = stack.Pop();
            if (!visited.Add(rid)) continue;
            var role = _dir.Roles[rid];
            foreach (var p in role.PermissionIds)
                yield return (p, $"角色继承: {path} → {p}");
            foreach (var parent in role.ParentRoleIds)
                stack.Push((parent, $"{path} → {_dir.Roles[parent].Name}"));
        }
    }

    public EffectivePermissions Resolve(User user)
    {
        var eff = new EffectivePermissions { UserId = user.Id };

        // 1) 直接角色分配
        foreach (var roleId in user.RoleIds)
        {
            var srcId = $"RoleAssignment:{user.Id}:{roleId}";
            foreach (var (perm, chain) in ExpandRole(roleId))
                eff.Add(perm, new GrantSource(GrantKind.DirectRole, srcId,
                    $"直接分配角色[{_dir.Roles[roleId].Name}]，{chain}"));
        }

        // 2) 部门授权
        foreach (var dg in _dir.DepartmentGrants.Where(g => g.DeptId == user.DeptId))
        {
            foreach (var perm in dg.PermissionIds)
                eff.Add(perm, new GrantSource(GrantKind.DepartmentGrant, $"DeptGrant:{dg.Id}",
                    $"部门授权[{dg.Id}:{dg.Reason}] → {perm}"));
            foreach (var roleId in dg.RoleIds)
                foreach (var (perm, chain) in ExpandRole(roleId))
                    eff.Add(perm, new GrantSource(GrantKind.DepartmentGrant, $"DeptGrant:{dg.Id}",
                        $"部门授权[{dg.Id}:{dg.Reason}] 授予角色[{_dir.Roles[roleId].Name}]，{chain}"));
        }

        // 3) 临时代理（只取检查日生效的）
        foreach (var d in _dir.Delegations.Where(d => d.ToUserId == user.Id && d.IsActive(_asOf)))
        {
            var tag = $"临时代理[{d.Id}: {_dir.Users[d.FromUserId].Name}→{user.Name} {d.Start}~{d.End}]";
            foreach (var perm in d.PermissionIds)
                eff.Add(perm, new GrantSource(GrantKind.Delegation, $"Delegation:{d.Id}", $"{tag} → {perm}"));
            foreach (var roleId in d.RoleIds)
                foreach (var (perm, chain) in ExpandRole(roleId))
                    eff.Add(perm, new GrantSource(GrantKind.Delegation, $"Delegation:{d.Id}",
                        $"{tag} 授予角色[{_dir.Roles[roleId].Name}]，{chain}"));
        }

        // 4) 审批流配置（指定到人，或指定到部门=部门负责人）
        foreach (var f in _dir.ApprovalFlows)
        {
            var applies = f.UserIds.Contains(user.Id)
                          || (f.DeptIds.Contains(user.DeptId) && _dir.IsDeptHead(user.Id));
            if (!applies) continue;
            eff.Add(f.PermissionId, new GrantSource(GrantKind.ApprovalFlow, $"Flow:{f.Id}",
                $"审批流[{f.Id}:{f.Name}]({f.Condition}) → {f.PermissionId}"));
        }

        return eff;
    }
}

/// <summary>企业目录：全部模拟数据的容器</summary>
public sealed class OrgDirectory
{
    public Dictionary<string, Permission> Permissions { get; } = new();
    public Dictionary<string, Role> Roles { get; } = new();
    public Dictionary<string, Department> Departments { get; } = new();
    public Dictionary<string, User> Users { get; } = new();
    public List<DepartmentGrant> DepartmentGrants { get; } = new();
    public List<Delegation> Delegations { get; } = new();
    public List<ApprovalFlowRule> ApprovalFlows { get; } = new();
    public List<RelationEdge> Relations { get; } = new();
    public List<SodRule> SodRules { get; } = new();

    public bool IsDeptHead(string userId) =>
        Departments.Values.Any(d => d.Id == Users[userId].DeptId) &&
        Users[userId].RoleIds.Contains("ROLE-DEPTHEAD");
}
