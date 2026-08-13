# RazorDbManager

[English](README.md) | 简体中文

RazorDbManager 是面向 .NET 10 Blazor Web App 的自包含数据库管理组件。首个
provider 支持 MySQL 和 MariaDB，使用 Interactive Server 渲染，提供有边界的
数据访问、行编辑、结构化 Schema 变更、独立授权的 SQL 控制台、流式导入导出和
审计扩展点。

数据工作区包括快速文本搜索、逐字段结构化 `WHERE` 查询、排序与分页、醒目的新增
记录入口、选中行 CSV 导出，以及针对有限 InnoDB 行集合的
原子乐观并发批量删除。SQL 控制台可以为当前表生成带正确标识符引用的 `SELECT`、
`WHERE`、`INSERT`、`UPDATE` 和 `DELETE` 模板；生成的 `UPDATE`、`DELETE`
默认带有 `WHERE 1 = 0`，而且不会自动执行。

每次数据页查询都可以展开查看 provider 实际执行的参数化 SQL、受长度限制的参数值
预览和每条命令耗时。组件还会在内存中保留最近 50 次本次会话查询；SQL 和参数值
不会写入审计存储，并在组件 circuit 结束或切换数据库时消失。

浏览器永远不会收到数据库连接字符串。每项操作都会同时检查注册连接的 capability
上限、宿主应用授权策略、允许的 Schema 列表和数据库账号自身 grants。后台任务的
重新授权规则见下文。SQL 工作区内置 CodeMirror 6，宿主不需要安装前端包或添加
额外的脚本标签。

## 安装

只需安装 MySQL provider 包；UI 和 provider-neutral 契约会作为传递依赖安装：

```shell
dotnet add package RazorDbManager.MySql
```

在 `Program.cs` 中注册 Interactive Server、认证策略和逻辑数据库：

```csharp
using RazorDbManager;
using RazorDbManager.Core;
using RazorDbManager.MySql;

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(RazorDbManagerPolicies.Access,
        policy => policy.RequireRole("DatabaseAdmin"))
    .AddPolicy(RazorDbManagerPolicies.HighRisk,
        policy => policy.RequireRole("DatabaseAdmin"));

builder.Services
    .AddRazorDbManager(options => options.DefaultDatabaseId = "Main")
    .AddMySql("Main", options =>
    {
        options.ConnectionStringName = "MainDatabase";
        options.EnabledCapabilities = RazorDbCapabilitySets.DataEditor;
    });
```

映射受保护的支持端点并启用 RCL 内置页面：

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorDbManagerEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddRazorDbManagerPages();
```

中间件必须在端点映射之前注册。保留 Blazor 模板生成的宿主样式引用，例如
`RazorDbManager.Sample.styles.css`。ASP.NET Core 会把 RCL 的 CSS isolation bundle
合并到该样式中，不需要额外的 UI 框架。

在宿主 `Routes.razor` 中向 Router 提供 RCL assembly：

```razor
<Router AppAssembly="typeof(Program).Assembly"
        AdditionalAssemblies="RazorDbManager.RazorDbManagerRouting.Assemblies">
    ...
</Router>
```

内置页面随后可通过 `/db-manager` 访问。也可以把组件嵌入已有的授权页面：

```razor
@using RazorDbManager.Components
@rendermode InteractiveServer

<DatabaseManager DatabaseId="Main" />
```

组件边界只接受逻辑 `DatabaseId`，不接受连接字符串。`ReadOnly="true"` 只能收紧
当前组件实例的能力；`Class` 和 unmatched attributes 可用于定制根元素。组件参数
永远不能增加 capability。

导入表单和导出下载表单会携带由 ASP.NET Core Data Protection 签发的短期 scope，
并绑定当前用户、数据库 id 和 `ReadOnly` 状态。HTTP 端点会拒绝缺失、篡改、过期
或来自只读组件的 scope。活动中的 Interactive Server circuit 会在 scope 过期前
刷新它，但不会延长任何已签 token 的有效期。

`ReadOnly` 只作用于该组件入口。若宿主只嵌入只读管理器，应省略
`AddRazorDbManagerPages()` 和 Router 中的 RCL assembly；如果仍暴露内置路由，
设置 `options.BuiltInPageReadOnly = true`。

## 连接与密钥

开发环境不要把真实连接字符串写进 `appsettings.Development.json`。示例项目已经配置
固定 User Secrets id，可使用：

```shell
dotnet user-secrets set "ConnectionStrings:MainDatabase" "Server=localhost;Database=app;User ID=razordb_reader;Password=...;SslMode=VerifyFull;PersistSecurityInfo=false;AllowLoadLocalInfile=false" --project samples/RazorDbManager.Sample
```

示例默认仅启用元数据浏览、读取行和受保护的二进制下载。
`RazorDbManagerSample` 配置段提供行编辑、导入导出、结构化 DDL 和 SQL 控制台的
显式开发开关。打开开关不会自动授予数据库权限，仍需配置对应的最小权限凭据。

示例使用固定演示身份，因此只允许在 `Development` 环境和本机回环地址启动，不能
作为生产登录系统。生产环境必须把 NuGet 包安装到具备真实认证的宿主应用中。

生产连接必须验证 TLS 主机名，并关闭凭据持久化和本地文件加载：

```json
{
  "ConnectionStrings": {
    "MainDatabase": "Server=db.example.com;Database=app;User ID=razordb_reader;Password=...;SslMode=VerifyFull;PersistSecurityInfo=false;AllowLoadLocalInfile=false"
  }
}
```

`DataEditor` 固定包含元数据浏览、读取、新增、更新和删除行。二进制及 geometry 下载
需要显式加入 `RazorDbCapability.DownloadBinary`，还要求当前行拥有主键或安全的
非空唯一键。下载使用绑定用户的一次性链接，单值默认最大 25 MiB。

Schema 变更、破坏性 Schema 变更、导入、导出和任意 SQL 都不会被隐式启用。建议
分别通过 `WriterConnectionStringName`、`SchemaConnectionStringName` 和
`SqlConsoleConnectionStringName` 配置最小权限账号。DDL 或 SQL 若要共享读写凭据，
必须显式开启 `AllowSharedHighRiskCredential`。

## 认证、授权与高风险操作

RazorDbManager 不提供登录 UI，宿主必须实现认证并配置
`RazorDbManagerPolicies.Access`。启用任意 SQL 或 Schema capability 时，还必须：

- 配置 `RazorDbManagerPolicies.HighRisk`；
- 替换默认的 `IRazorDbSessionValidator`，否则宿主启动会 fail closed；
- 使用可重新验证的 Server authentication-state provider，检查账号状态、近期认证或
  MFA 状态。

启用导入或导出时，宿主还必须注册能够重新解析当前用户状态的
`IRazorDbBackgroundAuthorizer`。后台任务执行前会再次授权，不能只信任排队时捕获的
claims。

数据库账号 grants 才是任意 SQL 的最终权限边界。SQL 解析、按钮隐藏和 UI policy
都不能替代专用最小权限账号。

## 功能与包结构

- `RazorDbManager.Core`：provider-neutral 模型、capability 和扩展契约。
- `RazorDbManager`：Razor Class Library、授权层、受保护 HTTP 端点、本地单实例
  store、CSS、图标和中英文资源。
- `RazorDbManager.MySql`：MySQL/MariaDB 元数据、查询、CRUD、DDL、SQL 和传输实现。

默认本地数据位于 `App_Data/RazorDbManager`，不在 `wwwroot` 下。SQLite 保存审计、
后台任务、一次性 token nonce 和用户偏好。任务终态默认保留 30 天，审计保持追加式。
多实例部署必须替换任务、产物、审计、偏好和 operation-token store，并共享 Data
Protection keys。

授权后的 `/_razor-db-manager/status` 会执行实时只读连接检查，并保守解析
`SHOW GRANTS` 作为诊断信息。它可以报告未解析角色、缺少读取权限或权限过大的 reader
账号，但诊断结果绝不会授予操作权限；MySQL/MariaDB 始终是 grants 的最终裁决者。

## 构建与测试

仓库通过 `global.json` 固定稳定 .NET SDK：

```shell
dotnet restore RazorDbManager.slnx --configfile NuGet.Config
dotnet build RazorDbManager.slnx -c Release --no-restore
dotnet test RazorDbManager.slnx -c Release --no-build
```

设置 `RAZORDB_TEST_CONNECTION` 后可运行 MySQL 8.4 或 MariaDB 11.8 的真实 provider
集成测试。请勿使用生产数据库和生产凭据执行测试。Pull Request 会运行这两个当前
基线，定时工作流还会覆盖 MySQL 9.7 与 MariaDB 10.11/11.4。

在任何环境暴露数据库管理器前，请阅读 [SECURITY.md](SECURITY.md)。捆绑的浏览器
依赖记录在 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
