using SodAudit;

// ==================== SoD 职责分离冲突检查工具 ====================
var asOf = args.Length > 0 && DateOnly.TryParse(args[0], out var d) ? d : DateOnly.FromDateTime(DateTime.Today);

var dir = DataSeeder.Build(asOf);
var resolver = new PermissionResolver(dir, asOf);
var detector = new ConflictDetector(dir, asOf);

// 1) 解析所有用户的有效权限（角色继承 + 部门授权 + 临时代理 + 审批流）
var effective = dir.Users.Values.ToDictionary(u => u.Id, u => resolver.Resolve(u));

// 2) 单人冲突 + 串通风险
var conflicts = detector.DetectSingleUser(effective);
var collusions = detector.DetectCollusion(effective, conflicts);

var structural = conflicts.Where(c => c.Status == ConflictStatus.Structural).ToList();
var dlgBad = conflicts.Where(c => c.Status == ConflictStatus.DelegationUnapproved).ToList();
var dlgOk = conflicts.Where(c => c.Status == ConflictStatus.DelegationApproved).ToList();

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║          职责分离 (SoD) 冲突审计报告                          ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
Console.WriteLine($"检查基准日: {asOf}    用户: {dir.Users.Count}  角色: {dir.Roles.Count}  " +
                  $"权限点: {dir.Permissions.Count}  SoD规则: {dir.SodRules.Count}");
Console.WriteLine($"生效中代理: {dir.Delegations.Count(x => x.IsActive(asOf))}  部门授权: {dir.DepartmentGrants.Count}  " +
                  $"审批流: {dir.ApprovalFlows.Count}");
Console.WriteLine();
Console.WriteLine($"发现 结构性冲突 {structural.Count} 项 | 代理期冲突(未批准) {dlgBad.Count} 项 | " +
                  $"代理期冲突(已批准·仅记录) {dlgOk.Count} 项 | 串通风险 {collusions.Count} 项");

var n = 0;
foreach (var group in new[]
         {
             ("一、结构性冲突（长期存在，须整改）", structural),
             ("二、临时代理期冲突（未经业务批准，限期整改）", dlgBad),
             ("三、临时代理期冲突（已经业务批准，仅备案）", dlgOk),
         })
{
    if (group.Item2.Count == 0) continue;
    Console.WriteLine();
    Console.WriteLine($"━━━━━━━━━━ {group.Item1} ━━━━━━━━━━");
    foreach (var c in group.Item2.OrderByDescending(c => c.Rule.Severity))
        PrintConflict(++n, c, dir);
}

if (collusions.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("━━━━━━━━━━ 四、串通风险（关系密切者权限互补） ━━━━━━━━━━");

    // 特定关系（亲属/利益、同组）→ 逐条详报，这是审计真正要查的
    var specific = collusions.Where(r => r.Relation.Kind != RelationKind.ManagerChain)
                             .OrderByDescending(r => r.Rule.Severity).ToList();
    // 上下级互补多属岗位设计性固有風险 → 按 规则+上级 汇总，不逐条刷屏
    var inherent = collusions.Where(r => r.Relation.Kind == RelationKind.ManagerChain)
                             .GroupBy(r => (r.Rule.Id, Manager: r.UserB))
                             .ToList();

    var i = 0;
    foreach (var r in specific)
    {
        var a = dir.Users[r.UserA]; var b = dir.Users[r.UserB];
        Console.WriteLine();
        Console.WriteLine($"[C{++i}] 规则 {r.Rule.Id} {r.Rule.Name}（严重度 {r.Rule.Severity}）— {r.Rule.Risk}");
        Console.WriteLine($"  关系: {a.Name}({a.Id}) ↔ {b.Name}({b.Id}) — {r.Relation.Note}");
        Console.WriteLine($"  {a.Name} 持有: {string.Join(", ", r.Contributions[r.UserA])}");
        Console.WriteLine($"  {b.Name} 持有: {string.Join(", ", r.Contributions[r.UserB])}");
        Console.WriteLine($"  两人合谋即可凑齐 [{string.Join(" + ", r.Rule.PermissionIds)}]，独立完成「{r.Rule.Process}」");
        Console.WriteLine($"  建议: 将其中一方调离关键岗位，或对该流程启用双人复核/提高审批层级");
    }

    if (inherent.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("  —— 岗位设计性固有风险（上下级权限互补，汇总提示）——");
        foreach (var g in inherent.OrderBy(g => g.Key.Id))
        {
            var rule = dir.SodRules.First(r => r.Id == g.Key.Id);
            var mgr = dir.Users[g.Key.Manager];
            Console.WriteLine($"  △ {rule.Id} {rule.Name}: 上级 {mgr.Name}({mgr.Id}) 与 {g.Count()} 名下属权限互补" +
                              $"（[{string.Join(" + ", rule.PermissionIds)}]），建议以流程控制（双人复核/轮岗）覆盖");
        }
    }
}

Console.WriteLine();
Console.WriteLine("报告结束。");

static void PrintConflict(int n, Conflict c, OrgDirectory dir)
{
    var u = dir.Users[c.UserId];
    var dept = dir.Departments[u.DeptId];
    Console.WriteLine();
    Console.WriteLine($"[{n}] 规则 {c.Rule.Id} {c.Rule.Name}（严重度 {c.Rule.Severity}）");
    Console.WriteLine($"  人员: {u.Name} ({u.Id}) — {dept.Name}");
    Console.WriteLine($"  风险: {c.Rule.Risk}");
    if (c.DelegationIds.Count > 0)
    {
        foreach (var id in c.DelegationIds)
        {
            var dlg = dir.Delegations.First(x => $"Delegation:{x.Id}" == id);
            Console.WriteLine($"  关联代理: {dlg.Id} {dlg.Start}~{dlg.End}「{dlg.Reason}」" +
                              $"（业务批准: {(dlg.BusinessApproved ? "是" : "否")}）");
        }
    }

    // 冲突证据链：每个权限是哪几条授权给的
    Console.WriteLine("  冲突证据链:");
    foreach (var (perm, sources) in c.Evidence)
    {
        Console.WriteLine($"    ● {perm} ({dir.Permissions[perm].System}) 来自 {sources.Count} 条授权:");
        foreach (var s in sources)
            Console.WriteLine($"        - [{KindName(s.Kind)}] {s.Description}");
    }

    // 最小修正
    var best = c.Fixes[0];
    Console.WriteLine($"  最小修正（撤销 {best.Sources.Count} 条授权即可打破冲突）:");
    Console.WriteLine($"    方案: 剥夺其「{best.StrippedPermission}」权限，需撤销:");
    foreach (var s in best.Sources)
        Console.WriteLine($"        ✂ [{KindName(s.Kind)}] {s.SourceId} — {s.Description}");
    Console.WriteLine($"    备注: {best.Note}");
    if (c.Fixes.Count > 1)
    {
        var alt = c.Fixes[1];
        Console.WriteLine($"    备选: 也可剥夺「{alt.StrippedPermission}」（需撤销 {alt.Sources.Count} 条授权）");
    }
}

static string KindName(GrantKind k) => k switch
{
    GrantKind.DirectRole => "角色分配",
    GrantKind.DepartmentGrant => "部门授权",
    GrantKind.Delegation => "临时代理",
    GrantKind.ApprovalFlow => "审批流",
    _ => k.ToString()
};
