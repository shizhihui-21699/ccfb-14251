namespace SodAudit;

/// <summary>
/// 模拟中型企业（约 180 人 / 12 部门 / 5 系统）的权限数据。
/// 数据确定性生成（固定随机种子），并刻意埋下审计中常见的几类冲突：
///   ① 角色继承叠加出的"自建自批"
///   ② 跨系统（ERP+财务+供应商主数据）凑齐的完整舞弊链
///   ③ 审批流配置悄悄补上的最后一块拼图
///   ④ 临时代理造成的限期冲突（一个经业务批准、一个未批准）
///   ⑤ 部门授权误伤（整部门获得不该有的角色）
///   ⑥ 关系密切两人权限互补的串通风险
/// </summary>
public static class DataSeeder
{
    public static OrgDirectory Build(DateOnly asOf)
    {
        var dir = new OrgDirectory();
        SeedPermissions(dir);
        SeedRoles(dir);
        SeedOrg(dir, asOf);
        SeedDelegations(dir, asOf);
        SeedApprovalFlows(dir);
        SeedRelations(dir);
        SeedSodRules(dir);
        return dir;
    }

    private static void SeedPermissions(OrgDirectory d)
    {
        void P(string id, string name, SystemId sys) => d.Permissions[id] = new Permission(id, name, sys);

        // 供应商主数据系统
        P("Vendor.Create", "新建供应商", SystemId.VendorMaster);
        P("Vendor.Modify", "修改供应商资料", SystemId.VendorMaster);
        P("Vendor.Approve", "供应商准入审批", SystemId.VendorMaster);
        P("Vendor.BankChange", "修改供应商收款账户", SystemId.VendorMaster);
        // ERP 采购
        P("PO.Create", "创建采购订单", SystemId.ERP);
        P("PO.Approve", "采购订单审批", SystemId.ERP);
        P("PO.Receive", "收货确认", SystemId.ERP);
        P("Invoice.Match", "发票三单匹配", SystemId.ERP);
        // 财务
        P("Payment.Create", "创建付款单", SystemId.Finance);
        P("Payment.Approve", "付款审批", SystemId.Finance);
        P("Payment.Release", "付款放行(网银)", SystemId.Finance);
        P("GL.Post", "总账过账", SystemId.Finance);
        // HR
        P("Employee.Create", "新建员工档案", SystemId.HR);
        P("Salary.Modify", "薪资调整录入", SystemId.HR);
        P("Salary.Approve", "薪资调整审批", SystemId.HR);
        // CRM
        P("Order.Create", "创建销售订单", SystemId.CRM);
        P("Discount.Approve", "折扣审批", SystemId.CRM);
        P("Customer.Create", "新建客户", SystemId.CRM);
    }

    private static void SeedRoles(OrgDirectory d)
    {
        Role R(string id, string name, string[] parents, string[] perms)
        {
            var r = new Role { Id = id, Name = name };
            r.ParentRoleIds.AddRange(parents);
            r.PermissionIds.AddRange(perms);
            d.Roles[id] = r;
            return r;
        }

        R("ROLE-EMPLOYEE", "全体员工", Array.Empty<string>(), Array.Empty<string>());
        R("ROLE-DEPTHEAD", "部门负责人", new[] { "ROLE-EMPLOYEE" }, Array.Empty<string>());

        // 采购线（含继承：高级采购员 ← 采购员）
        R("ROLE-BUYER", "采购员", new[] { "ROLE-EMPLOYEE" }, new[] { "PO.Create" });
        R("ROLE-SR-BUYER", "高级采购员", new[] { "ROLE-BUYER" }, new[] { "Invoice.Match" });
        R("ROLE-PURCHASE-MGR", "采购经理", new[] { "ROLE-DEPTHEAD" }, new[] { "PO.Approve" });
        R("ROLE-WAREHOUSE", "仓库管理员", new[] { "ROLE-EMPLOYEE" }, new[] { "PO.Receive" });

        // 供应商主数据线
        R("ROLE-VENDOR-ADMIN", "供应商管理员", new[] { "ROLE-EMPLOYEE" }, new[] { "Vendor.Create", "Vendor.Modify" });
        R("ROLE-VENDOR-AUDITOR", "供应商审核员", new[] { "ROLE-EMPLOYEE" }, new[] { "Vendor.Approve" });
        R("ROLE-VENDOR-MASTER-MGR", "主数据经理", new[] { "ROLE-VENDOR-ADMIN" }, new[] { "Vendor.BankChange" });

        // 财务线（含继承：财务经理 ← 财务主管 ← 应付会计）
        R("ROLE-AP-ACCOUNTANT", "应付会计", new[] { "ROLE-EMPLOYEE" }, new[] { "Invoice.Match", "Payment.Create" });
        R("ROLE-FIN-SUPERVISOR", "财务主管", new[] { "ROLE-AP-ACCOUNTANT" }, new[] { "Payment.Approve" });
        R("ROLE-FIN-MGR", "财务经理", new[] { "ROLE-FIN-SUPERVISOR" }, new[] { "GL.Post" });
        R("ROLE-CASHIER", "出纳", new[] { "ROLE-EMPLOYEE" }, new[] { "Payment.Release" });

        // HR 线
        R("ROLE-HR-SPECIALIST", "人事专员", new[] { "ROLE-EMPLOYEE" }, new[] { "Employee.Create" });
        R("ROLE-PAYROLL-SPECIALIST", "薪酬专员", new[] { "ROLE-EMPLOYEE" }, new[] { "Salary.Modify" });
        R("ROLE-PAYROLL-MGR", "薪酬经理", new[] { "ROLE-DEPTHEAD" }, new[] { "Salary.Approve" });

        // 销售线
        R("ROLE-SALES", "销售代表", new[] { "ROLE-EMPLOYEE" }, new[] { "Order.Create", "Customer.Create" });
        R("ROLE-SALES-MGR", "销售经理", new[] { "ROLE-DEPTHEAD" }, new[] { "Discount.Approve" });
    }

    private static void SeedOrg(OrgDirectory d, DateOnly asOf)
    {
        string[] deptNames = { "采购部", "财务部", "仓储物流部", "人力资源部", "销售部", "IT部",
                               "行政部", "生产部", "质量部", "研发部", "市场部", "法务部" };
        foreach (var (name, i) in deptNames.Select((n, i) => (n, i)))
            d.Departments[$"D{i + 1:00}"] = new Department { Id = $"D{i + 1:00}", Name = name };

        // 确定性生成 180 名员工
        var rng = new Random(42);
        string[] surnames = { "王", "李", "张", "刘", "陈", "杨", "赵", "黄", "周", "吴", "徐", "孙", "胡", "朱", "高", "林", "何", "郭", "马", "罗" };
        string[] given = { "伟", "芳", "娜", "敏", "静", "磊", "军", "洋", "勇", "艳", "杰", "娟", "涛", "明", "超", "秀英", "雪", "晨", "宇", "欣", "子涵", "雨泽", "梓萱", "浩然", "思远", "嘉怡", "俊杰", "雅静", "志强", "晓燕" };

        var users = new List<User>();
        for (var i = 0; i < 180; i++)
        {
            var deptId = $"D{rng.Next(1, 13):00}";
            var u = new User
            {
                Id = $"U{i + 1:000}",
                Name = surnames[rng.Next(surnames.Length)] + given[rng.Next(given.Length)],
                DeptId = deptId
            };
            u.RoleIds.Add("ROLE-EMPLOYEE");
            users.Add(u);
            d.Users[u.Id] = u;
        }

        // 每部门设一名负责人
        foreach (var dept in d.Departments.Values)
        {
            var head = users.First(u => u.DeptId == dept.Id && !u.RoleIds.Contains("ROLE-DEPTHEAD"));
            head.RoleIds.Add("ROLE-DEPTHEAD");
        }

        // 上下级关系：部门负责人为经理
        foreach (var u in users)
        {
            var head = users.FirstOrDefault(x => x.DeptId == u.DeptId && x.Id != u.Id && x.RoleIds.Contains("ROLE-DEPTHEAD"));
            if (head != null) u.ManagerId = head.Id;
        }

        // 按部门批量分配业务角色（大部分是无冲突的正常授权）
        void Assign(string deptId, string roleId, int count)
        {
            var candidates = users.Where(u => u.DeptId == deptId && !u.RoleIds.Contains(roleId)).ToList();
            foreach (var u in candidates.OrderBy(_ => rng.Next()).Take(count))
                u.RoleIds.Add(roleId);
        }

        Assign("D01", "ROLE-BUYER", 10);        // 采购部：采购员
        Assign("D01", "ROLE-SR-BUYER", 3);
        Assign("D02", "ROLE-AP-ACCOUNTANT", 6); // 财务部：应付会计
        Assign("D02", "ROLE-CASHIER", 2);
        Assign("D03", "ROLE-WAREHOUSE", 8);     // 仓储部
        Assign("D04", "ROLE-HR-SPECIALIST", 3);
        Assign("D04", "ROLE-PAYROLL-SPECIALIST", 2);
        Assign("D05", "ROLE-SALES", 12);
        Assign("D09", "ROLE-VENDOR-AUDITOR", 2); // 质量部兼供应商审核
        Assign("D07", "ROLE-VENDOR-ADMIN", 2);   // 行政部下设主数据组

        // 部门负责人配管理角色
        Head("D01").RoleIds.Add("ROLE-PURCHASE-MGR");
        Head("D02").RoleIds.Add("ROLE-FIN-MGR");
        Head("D04").RoleIds.Add("ROLE-PAYROLL-MGR");
        Head("D05").RoleIds.Add("ROLE-SALES-MGR");

        User Head(string deptId) =>
            users.First(u => u.DeptId == deptId && u.RoleIds.Contains("ROLE-DEPTHEAD"));

        // ============ 埋点：刻意构造的冲突场景 ============

        // 场景① 财务经理（继承链自带 Payment.Approve）又兼任供应商管理员
        //       → 集齐 新建供应商 + 付款审批（跨 VendorMaster/Finance 两系统）
        var finMgr = Head("D02");
        finMgr.RoleIds.Add("ROLE-VENDOR-ADMIN");

        // 场景② 主数据经理（Vendor.Create + Vendor.BankChange）被部门授权误伤见 SeedOrg 末尾

        // 场景③ 一名高级采购员被拉进"大额采购审批流"获得 PO.Approve（见 SeedApprovalFlows）
        //       → 自建自批采购单，冲突来自审批流配置而非角色

        // 场景④ 部门授权：仓储物流部整部门获得"采购员"角色（历史遗留，搬家时图方便）
        //       → 仓库人员同时持有 PO.Create + PO.Receive（自购自收）
        d.DepartmentGrants.Add(new DepartmentGrant
        {
            Id = "DG-001", DeptId = "D03",
            RoleIds = { "ROLE-BUYER" },
            Reason = "2023年部门合并时批量开通，至今未回收"
        });

        // 场景⑤ 部门授权：财务部整部门获得供应商账户维护权（审计重点）
        d.DepartmentGrants.Add(new DepartmentGrant
        {
            Id = "DG-002", DeptId = "D02",
            PermissionIds = { "Vendor.BankChange" },
            Reason = "应付组代维护供应商银行账户"
        });
        // → 出纳因此集齐 Vendor.BankChange + Payment.Release（改账号+放款）

        // 场景⑥ HR：薪酬专员同时被直接授予薪酬经理角色（编制混乱）
        var payroll = users.First(u => u.RoleIds.Contains("ROLE-PAYROLL-SPECIALIST"));
        payroll.RoleIds.Add("ROLE-PAYROLL-MGR"); // Salary.Modify + Salary.Approve 自调自批
    }

    private static void SeedDelegations(OrgDirectory d, DateOnly asOf)
    {
        // 场景⑦ 应付会计临时代理财务主管 → 获得 Payment.Approve，
        //        与其自身 Payment.Create 冲突（自制自批付款）。一个经批准、一个未批准。
        //        代理对象必须是不带任何财务审批角色的普通应付会计（且不是被代理人本人）。
        var finMgr = d.Users.Values.First(u => u.RoleIds.Contains("ROLE-FIN-MGR"));
        var accountants = d.Users.Values
            .Where(u => u.RoleIds.Contains("ROLE-AP-ACCOUNTANT")
                        && !u.RoleIds.Contains("ROLE-FIN-SUPERVISOR")
                        && !u.RoleIds.Contains("ROLE-FIN-MGR")
                        && u.Id != finMgr.Id)
            .Take(2).ToList();
        d.Delegations.Add(new Delegation
        {
            Id = "DLG-101",
            FromUserId = finMgr.Id,
            ToUserId = accountants[0].Id,
            RoleIds = { "ROLE-FIN-SUPERVISOR" },
            Start = asOf.AddDays(-10), End = asOf.AddDays(20),
            BusinessApproved = true,   // 休假顶岗，走了审批
            Reason = "财务主管休假顶岗（已经分管副总审批）"
        });
        d.Delegations.Add(new Delegation
        {
            Id = "DLG-102",
            FromUserId = finMgr.Id,
            ToUserId = accountants[1].Id,
            RoleIds = { "ROLE-FIN-SUPERVISOR" },
            Start = asOf.AddDays(-3), End = asOf.AddDays(30),
            BusinessApproved = false,  // 口头交代，未走审批
            Reason = "口头安排的临时顶岗，未提交审批单"
        });

        // 场景⑧ 出纳病假，把 Payment.Release 代理给财务部一名普通文员
        //        → 对方经部门授权 DG-002 持有 Vendor.BankChange，
        //          叠加后集齐 改账户+放款（FIN-03，未批准）
        var cashier = d.Users.Values.First(u => u.RoleIds.Contains("ROLE-CASHIER"));
        var clerk = d.Users.Values.First(u =>
            u.DeptId == "D02"
            && !u.RoleIds.Any(r => r is "ROLE-AP-ACCOUNTANT" or "ROLE-CASHIER"
                                    or "ROLE-FIN-SUPERVISOR" or "ROLE-FIN-MGR" or "ROLE-DEPTHEAD"));
        d.Delegations.Add(new Delegation
        {
            Id = "DLG-103",
            FromUserId = cashier.Id,
            ToUserId = clerk.Id,
            PermissionIds = { "Payment.Release" },
            Start = asOf.AddDays(-1), End = asOf.AddDays(14),
            BusinessApproved = false,
            Reason = "出纳病假，临时让同事代付"
        });

        // 一条已过期代理（不应产生冲突，验证时间窗过滤）
        d.Delegations.Add(new Delegation
        {
            Id = "DLG-099",
            FromUserId = cashier.Id,
            ToUserId = accountants[0].Id,
            PermissionIds = { "Payment.Release" },
            Start = asOf.AddDays(-90), End = asOf.AddDays(-60),
            BusinessApproved = false,
            Reason = "去年年结期间临时代理（已过期）"
        });
    }

    private static void SeedApprovalFlows(OrgDirectory d)
    {
        // 场景③：大额采购审批流把 PO.Approve 动态授予一名高级采购员
        var srBuyer = d.Users.Values.First(u => u.RoleIds.Contains("ROLE-SR-BUYER"));
        d.ApprovalFlows.Add(new ApprovalFlowRule
        {
            Id = "FLOW-01", Name = "大额采购订单审批",
            PermissionId = "PO.Approve",
            UserIds = { srBuyer.Id },
            Condition = "采购金额 > 500,000"
        });

        // 正常配置：超 10 万付款由财务负责人终审（财务经理本就有 Payment.Approve，无新增冲突）
        var finHead = d.Users.Values.First(u => u.RoleIds.Contains("ROLE-FIN-MGR"));
        d.ApprovalFlows.Add(new ApprovalFlowRule
        {
            Id = "FLOW-02", Name = "大额付款终审",
            PermissionId = "Payment.Approve",
            UserIds = { finHead.Id },
            Condition = "付款金额 > 100,000"
        });

        // 正常配置：薪资调整由人力资源部负责人审批
        d.ApprovalFlows.Add(new ApprovalFlowRule
        {
            Id = "FLOW-03", Name = "薪资调整审批",
            PermissionId = "Salary.Approve",
            DeptIds = { "D04" },
            Condition = "所有薪资调整单"
        });
    }

    private static void SeedRelations(OrgDirectory d)
    {
        // 上下级关系边
        foreach (var u in d.Users.Values.Where(u => u.ManagerId != null))
            d.Relations.Add(new RelationEdge(u.Id, u.ManagerId!, RelationKind.ManagerChain, "直接上下级"));

        // 场景⑥串通对：供应商管理员（Vendor.Create）与财务主管（Payment.Approve）是夫妻
        var vendorAdmin = d.Users.Values.First(u => u.RoleIds.Contains("ROLE-VENDOR-ADMIN") && !u.RoleIds.Contains("ROLE-FIN-MGR"));
        var finSupervisor = d.Users.Values.First(u =>
            u.RoleIds.Contains("ROLE-AP-ACCOUNTANT") && u.DeptId == "D02"
            && !u.RoleIds.Any(r => r is "ROLE-FIN-SUPERVISOR" or "ROLE-FIN-MGR" or "ROLE-DEPTHEAD" or "ROLE-CASHIER")
            && !d.Delegations.Any(x => x.ToUserId == u.Id));
        // 给这位会计配上财务主管角色，使其持有 Payment.Approve
        finSupervisor.RoleIds.Add("ROLE-FIN-SUPERVISOR");
        d.Relations.Add(new RelationEdge(vendorAdmin.Id, finSupervisor.Id, RelationKind.CloseTie, "HR备案：夫妻关系"));

        // 同组关系示例
        var buyers = d.Users.Values.Where(u => u.RoleIds.Contains("ROLE-BUYER")).Take(2).ToList();
        if (buyers.Count == 2)
            d.Relations.Add(new RelationEdge(buyers[0].Id, buyers[1].Id, RelationKind.SameTeam, "同采购组"));
    }

    private static void SeedSodRules(OrgDirectory d)
    {
        d.SodRules.AddRange(new[]
        {
            new SodRule
            {
                Id = "P2P-01", Name = "供应商自建自审",
                Process = "供应商准入流程",
                PermissionIds = { "Vendor.Create", "Vendor.Approve" },
                Risk = "可虚构供应商并通过准入审批",
                Severity = 4
            },
            new SodRule
            {
                Id = "P2P-02", Name = "虚构供应商并付款",
                Process = "采购到付款(P2P)全流程",
                PermissionIds = { "Vendor.Create", "Payment.Approve" },
                Risk = "可新建供应商后直接批准向其付款，独立完成舞弊闭环",
                Severity = 5
            },
            new SodRule
            {
                Id = "P2P-03", Name = "采购单自建自批",
                Process = "采购订单流程",
                PermissionIds = { "PO.Create", "PO.Approve" },
                Risk = "可自行创建并批准采购订单",
                Severity = 4
            },
            new SodRule
            {
                Id = "P2P-04", Name = "自购自收",
                Process = "采购收货流程",
                PermissionIds = { "PO.Create", "PO.Receive" },
                Risk = "可虚构采购并确认收货，套取付款",
                Severity = 4
            },
            new SodRule
            {
                Id = "FIN-01", Name = "付款自制自批",
                Process = "付款流程",
                PermissionIds = { "Payment.Create", "Payment.Approve" },
                Risk = "可制作付款单并自行审批",
                Severity = 5
            },
            new SodRule
            {
                Id = "FIN-02", Name = "审批放行一手包办",
                Process = "付款流程",
                PermissionIds = { "Payment.Approve", "Payment.Release" },
                Risk = "可审批付款并直接在网银放行",
                Severity = 4
            },
            new SodRule
            {
                Id = "FIN-03", Name = "改账户+放款",
                Process = "供应商付款流程",
                PermissionIds = { "Vendor.BankChange", "Payment.Release" },
                Risk = "可将供应商收款账户改为自用账户并放行付款",
                Severity = 5
            },
            new SodRule
            {
                Id = "HR-01", Name = "幽灵员工",
                Process = "人事薪酬流程",
                PermissionIds = { "Employee.Create", "Salary.Modify" },
                Risk = "可虚构员工并为其定薪，冒领工资",
                Severity = 4
            },
            new SodRule
            {
                Id = "HR-02", Name = "薪资自调自批",
                Process = "薪酬调整流程",
                PermissionIds = { "Salary.Modify", "Salary.Approve" },
                Risk = "可自行调整薪资并自行审批",
                Severity = 4
            },
            new SodRule
            {
                Id = "CRM-01", Name = "自售自折",
                Process = "销售折扣流程",
                PermissionIds = { "Order.Create", "Discount.Approve" },
                Risk = "可创建订单并自行批准超额折扣，输送利益",
                Severity = 3
            },
        });
    }
}
