namespace SodAudit;

/// <summary>业务系统</summary>
public enum SystemId
{
    ERP,          // 采购/订单
    Finance,      // 财务/付款
    VendorMaster, // 供应商主数据
    HR,           // 人事/薪资
    CRM           // 客户/销售
}

/// <summary>细粒度权限点，例如 "Vendor.Create"</summary>
public sealed record Permission(string Id, string Name, SystemId System);

/// <summary>角色：自带权限 + 继承父角色（DAG，可多父）</summary>
public sealed class Role
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public List<string> ParentRoleIds { get; init; } = new();
    public List<string> PermissionIds { get; init; } = new();
}

public sealed class Department
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}

public sealed class User
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string DeptId { get; init; }
    public string? ManagerId { get; set; }
    /// <summary>直接分配的角色</summary>
    public List<string> RoleIds { get; init; } = new();
}

/// <summary>部门级授权：部门内成员自动获得角色或权限</summary>
public sealed class DepartmentGrant
{
    public required string Id { get; init; }
    public required string DeptId { get; init; }
    public List<string> RoleIds { get; init; } = new();
    public List<string> PermissionIds { get; init; } = new();
    public required string Reason { get; init; }
}

/// <summary>临时代理：A 在 [Start,End] 期间把部分角色/权限委托给 B</summary>
public sealed class Delegation
{
    public required string Id { get; init; }
    public required string FromUserId { get; init; }
    public required string ToUserId { get; init; }
    public List<string> RoleIds { get; init; } = new();
    public List<string> PermissionIds { get; init; } = new();
    public required DateOnly Start { get; init; }
    public required DateOnly End { get; init; }
    /// <summary>代理造成的职责重叠是否经过业务审批（允许存在）</summary>
    public bool BusinessApproved { get; init; }
    public required string Reason { get; init; }

    public bool IsActive(DateOnly at) => Start <= at && at <= End;
}

/// <summary>
/// 审批流配置：某类单据满足条件时由指定人/部门审批，
/// 等价于向这些人动态授予对应审批权限。
/// </summary>
public sealed class ApprovalFlowRule
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string PermissionId { get; init; }   // 该节点等效的审批权限
    public List<string> UserIds { get; init; } = new();  // 指定到人
    public List<string> DeptIds { get; init; } = new();  // 或指定到部门（部门负责人）
    public required string Condition { get; init; }      // 例如 "付款金额 > 100,000"
}

/// <summary>人员关系边（用于串通风险分析）</summary>
public enum RelationKind { ManagerChain, SameTeam, CloseTie /* 亲属/利益关系，来自HR标记 */ }

public sealed record RelationEdge(string UserA, string UserB, RelationKind Kind, string Note);

/// <summary>
/// SoD 规则：一组权限若被同一人（或串通的多人）集齐，
/// 即可独立完成本该多人把关的完整业务流程。
/// </summary>
public sealed class SodRule
{
    public required string Id { get; init; }
    public required string Name { get; init; }        // 例如 "P2P-01 供应商到付款全流程"
    public required string Process { get; init; }     // 业务流程描述
    public List<string> PermissionIds { get; init; } = new();
    public required string Risk { get; init; }        // 风险描述
    public int Severity { get; init; } = 3;           // 1-5
}
