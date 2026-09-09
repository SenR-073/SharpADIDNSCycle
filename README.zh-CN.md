# SharpADIDNSCycle

**作者：SenRan · 版本：0.8.0**

[English](README.md)

一个通过 LDAP 批量操作 Active Directory 集成 DNS 的独立 C# 工具。支持直接批量添加、分批轮询添加，以及按输入列表删除记录。程序完整实现在一个 `.cs` 文件中。

## 编译

运行环境：**64 位 Windows、.NET Framework 4.x**，并能访问目标域控。默认使用运行进程的 Windows 身份进行 LDAP 身份验证。

| 文件 | 用途 |
| --- | --- |
| `SharpADIDNSCycle.cs` | 完整源码；直接使用 `csc.exe` 编译时，唯一必需的项目文件。 |
| `build.cmd` | 可选的 Windows 一键编译脚本。 |
| `SharpADIDNSCycle.csproj` | 可选，用于 Visual Studio 或兼容的 .NET SDK。 |

在 Windows 命令提示符中运行 `build.cmd`，生成 Windows amd64 程序 `dist\SharpADIDNSCycle.exe`。也可以仅复制 `.cs` 文件，直接编译：

```bat
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 /optimize+ /r:System.DirectoryServices.dll /out:SharpADIDNSCycle.exe SharpADIDNSCycle.cs
```

`System.DirectoryServices.dll` 由 .NET Framework 提供，不需要从项目中复制。SDK 编译方式：

```text
dotnet build SharpADIDNSCycle.csproj -c Release
```

SDK 首次编译可能下载项目声明的引用程序集包。生成的 EXE 仍需 Windows 和 .NET Framework。本项目运行时无需安装 SharpADIDNS；沿用的 DNS 编码及字节序辅助代码在源码对应位置保留了来源署名。

## 三种操作

将示例中的 zone、域 DN、DC 和记录数据替换成实际值。下方每个示例都是一条完整命令。

**直接添加：** 一次处理所有有效且尚不存在的名称，添加后保留记录。

```powershell
.\SharpADIDNSCycle.exe add --zone lab.example --dn DC=lab,DC=example --server dc.lab.example --names dns01,dns02,dns03 --data 192.0.2.10
```

**轮询添加：** 添加一批，等待留存时间，按指定方式清理，再进入下一批；输入列表只遍历一次。

```powershell
.\SharpADIDNSCycle.exe add --zone lab.example --dn DC=lab,DC=example --server dc.lab.example --file names.txt --data 192.0.2.10 --batch-size 6 --jitter 2 --last-time 30 --delete-mode tombstone-remove --tombstone-delay 15
```

**指定删除：** 支持名称列表或文件，默认硬删除；使用 `--delete-skip` 保护指定名称。

```powershell
.\SharpADIDNSCycle.exe delete --zone lab.example --dn DC=lab,DC=example --server dc.lab.example --names dns01,dns02,dns03 --delete-mode remove
```

**删除对象是整个同名 `dnsNode`，包括其中所有 DNS 值。** 独立删除命令没有历史信息来判断某个名称是哪一次运行创建的。添加时跳过的原有记录，之后用同一份输入执行删除时，仍可能被删除。需要保留的名称应由用户从删除输入中排除。需要时先只读查看：

```powershell
.\SharpADIDNSCycle.exe delete --zone lab.example --dn DC=lab,DC=example --server dc.lab.example --file names.txt --dry-run --show-baseline --delete-skip servicedesk
```

## 完整参数

语法：`SharpADIDNSCycle.exe [add|delete] [参数]`。省略操作名时默认为 `add`。支持 `--参数 值` 和 `--参数=值`。开关不接受值，重复参数会报错；参数名区分大小写。

### 连接与身份验证

| 参数 | 含义与默认值 |
| --- | --- |
| `--zone NAME` | 必需，目标 DNS 区域，例如 `lab.example`；区域必须已经存在。 |
| `--dn DOMAIN_DN` | 域命名上下文，例如 `DC=lab,DC=example`。除 `--plan` 外必需。 |
| `--server HOST` | DC 主机名或 IPv4；不能包含协议、端口或路径。除 `--plan` 外必需。 |
| `--partition NAME` | `DomainDnsZones`（默认）、`ForestDnsZones` 或 `System`，区分大小写；选择实际存放区域的容器。 |
| `--ldaps` | 使用 636 端口 TLS，由 Windows 验证证书。默认 LDAP 使用安全身份验证、签名与加密封装。 |
| `--username USER` | 显式身份，例如 `LAB\operator` 或 `operator@lab.example`；须搭配下方一种密码来源。默认使用当前 Windows 身份。 |
| `--password-env ENV` | 从 EXE 进程的指定环境变量中读取密码。 |
| `--password-stdin` | 从标准输入读取一行密码；与 `--password-env` 互斥。 |

日志不转储密码、整条命令行或异常堆栈。密码来源为空或不可用会明确报错。

### 输入与 DNS 数据

| 参数 | 含义与默认值 |
| --- | --- |
| `--file PATH` | EXE 进程可以访问的输入文件，与 `--names` 二选一。 |
| `--names NAME1,NAME2` | 逗号分隔的名称；别名为 `--name`。必须选择一种输入来源。 |
| `--type TYPE` | 仅添加，默认 `A`；支持 `AAAA`、`CNAME`、`PTR`、`TXT`、`SRV`、`MX`，值不区分大小写。 |
| `--data VALUE` | 仅添加；只有名称的输入需要提供此值，别名为 `--ip`。三列文件行使用该行自己的类型与数据。 |
| `--ttl SECONDS` | 仅添加，DNS 缓存 TTL，范围 `1..604800` 秒，默认 `600`。 |
| `--mimic-aging` | 仅添加，在每条记录写入前设置当前 UTC aging 小时值；默认时间戳为静态值 `0`。不会修改服务器的老化或清理配置。 |

名称可以是区域内相对名称，也可以带上目标区域后缀。结尾的点表示绝对名称；区域外的绝对名称会跳过。标签支持 ASCII 字母、数字、下划线及中间位置的连字符，每段最多 63 字符，拼接后的完整名称最多 253 字符。区域根节点（`@` 或区域名称）、通配符及非法标签会跳过。名称统一小写；重复名称保留第一项，即使两项的记录类型不同。

推荐使用 UTF-8 文件，每行一个名称：

```text
# 忽略空行，以及去除行首空白后以 # 开头的整行注释。
dns01
dns02.lab.example
```

也支持 `名称<TAB>类型<TAB>数据`，分隔符必须是**真实制表符**：

```text
dns01	A	192.0.2.10
dns02	AAAA	2001:db8::10
alias01	CNAME	dns01.lab.example
```

两种行格式可以混合。删除模式只使用名称列，删除整个节点。注释应独占一行。

| 类型 | `--data` 示例 | 说明 |
| --- | --- | --- |
| A | `192.0.2.10` | IPv4。 |
| AAAA | `2001:db8::10` | IPv6。 |
| CNAME | `dns01.lab.example` | 目标主机名。 |
| PTR | `dns01.lab.example` | 目标主机名；应选择相应的反向查找区域。 |
| TXT | `"example text"` | ASCII，最多 255 字节。 |
| SRV | `"0 10 443 service.lab.example"` | 优先级、权重、端口、目标；数值字段范围 `0..65535`。 |
| MX | `"10 mail.lab.example"` | 优先级 `0..65535`、邮件服务器名称。 |

非法名称、重复名称和添加时已存在的节点记为 **SKIP**。行结构错误、不支持的类型、非法记录数据或文件读取失败仍然是**错误**。完整输入会先完成校验，再连接或写入 LDAP。全部非法或全部已存在时，以带跳过项的空操作结束；真正的空文件或仅有注释的文件属于输入错误。

### 轮询与删除方式

| 参数 | 含义与默认值 |
| --- | --- |
| `--batch-size N` | 仅添加，启用分批轮询，范围 `1..1000000`；必须指定 `--last-time`。不指定则直接添加。 |
| `--last-time SECONDS` | 每批最后一条成功添加后开始计算的留存时间，范围 `1..2147483647` 秒；依赖 `--batch-size`。 |
| `--jitter N` | **每批数量**的随机浮动，不是时间抖动；范围 `0..999999`，且必须小于 batch-size；默认 `0`，依赖 `--batch-size`。 |
| `--delete-mode MODE` | `tombstone`、`remove`、`tombstone-remove`；第三种也可写为 `tombstone+remove`。轮询默认 `tombstone`，独立删除默认 `remove`。 |
| `--delete-skip NAME1,NAME2` | 将这些规范化后的名称排除在清理之外。适用于独立删除和轮询添加；受保护名称不会被本次运行 tombstone 或硬删除。 |
| `--tombstone-delay SECONDS` | 只用于 `tombstone-remove`，范围 `0..2147483647` 秒。默认每批清理随机等待 `10..60` 秒；`0` 表示不额外等待。 |

直接添加不接受清理参数，需要另行执行删除命令。独立删除不接受轮询参数及添加记录专用参数。

| 删除方式 | 行为 |
| --- | --- |
| `tombstone` | 将 DNS 数据替换为 tombstone 并设置 `dNSTombstoned`；物理删除交给服务器维护，程序不等待它完成。 |
| `remove` | 通过 LDAP 硬删除整个 DNS 节点。 |
| `tombstone-remove` | 先 tombstone，整批等待一次，再通过 LDAP 硬删除成功 tombstone 化的目标。已经 tombstone 化的节点保留原 tombstone 时间戳。 |

例如 `--batch-size 6 --jitter 2 --last-time 30`：每批新增 4–8 个节点，最后一条添加完成后等待 30 秒，默认采用 tombstone 清理。最后一批可以少于 4 条；跳过项不占新增名额。上一批在程序中的清理步骤完成后，才开始下一批。

总耗时还包括 LDAP 操作和 tombstone-delay。每批先加入的记录，实际留存会比 last-time 更长。仅 tombstone 模式会留下前几批的 AD 对象，不会立即释放目录配额。域内复制、服务器维护和 DNS 客户端缓存各自有独立时间；tombstone 或硬删除都不保证所有服务器和缓存立刻不再返回记录。

### 输出与检查

| 参数 | 输出或行为 |
| --- | --- |
| 不指定输出模式 | 显示 banner、分类后的输入跳过项、明确错误、每轮是否有跳过项，以及最终统计。不在每一轮重复显示用户已经输入的 server、zone、batch-size、jitter 或 last-time。 |
| `--show-baseline` | 在任何写入前查询并显示本次输入的名称。表格包括现有 DNS 值、不存在的名称和由 `--delete-skip` 指定的名称。也可与 `--dry-run` 一起使用，不能与 `--plan` 一起使用。 |
| `--plan` | 仅校验本地输入，不访问 LDAP、不写入、不等待；不会检查名称是否已经存在。 |
| `--dry-run` | 只读访问 LDAP，不写入、不等待；不能据此证明具有创建／删除权限或足够配额。 |
| `--help`、`-h` | 分组帮助与简短 banner。 |

`--plan` 与 `--dry-run` 互斥。程序启动时只显示一次 banner。所有输出使用 native English，并通过固定字段和换行保持对齐；不会在每轮重复显示连接和轮询参数。

直接添加最后只显示紧凑摘要：

```text
SharpADIDNSCycle
Bulk LDAP DNS operations
Version 0.8.0 | Author: SenRan

Summary
  Result            : Completed with skips
  Added             : 48 records
  Input skips       : 2
  Errors            : 0
  Elapsed           : 3.4 seconds
```

轮询每轮只显示是否有跳过项，不重复连接参数和时间参数，最后显示总统计：

```text
  Batch 1           : No records skipped.
  Batch 2           : 1 operation(s) skipped.
  Already exists    : dns019

Summary
  Result            : Completed with skips
  Batches           : 2 completed
  Added             : 47 records
  Tombstoned        : 47 nodes
  Deleted           : 47 nodes
  Input skips       : 1
  Errors            : 0
```

错误会集中显示并说明原因；成功记录只在摘要中计数，不再逐条刷屏。

基线只覆盖**本次输入涉及的名称**，不会创建基线文件。表格支持展示 A、AAAA、CNAME、PTR、TXT、SRV、MX、NS、SOA 和 tombstone。不认识或损坏的数据会明确标注，不转储原始载荷或任意目录属性。

最终结果区分全部完成、带跳过项完成、失败和取消。只有每个输入项都新增成功、没有跳过且整个操作成功结束，才显示 `all added: yes`。统计按动作计数，同一个输入可以同时计入新增、tombstone 和硬删除。新增已提交但后续回读失败时，仍计入已新增，整个任务标为失败。错误数是失败操作数，不一定等于不同名称数；已不存在的目标与已确认硬删除分开统计。

正常日志输出到 stdout，提示和错误输出到 stderr。在 PowerShell 命令后追加下列内容，可将两者一起保存：

```powershell
# 追加到你的完整命令后：
--show-baseline > cycle.log 2>&1
```

`--file` 由**运行 EXE 的机器**打开。通过 PSRemoting 执行时，应先把文件复制到远端、提供远端可访问的路径，或者改用 `--names`。操作者 Mac 上的文件路径不会自动映射到远端。

## 错误与中断

权限和配额由 AD 判定，不通过“普通用户、机器账户、管理员”推断固定额度。程序不会假设统一的每用户可添加条数，不会自动缩小 batch-size，也不会盲目重试失败写入。错误会写明步骤、目标、原因、耗时以及可用的服务器诊断；ACL、身份验证、配额及连接错误都不会被当作正常跳过。

轮询新增失败后，停止后续新增，并按选定方式尝试清理当前批次已尝试写入的节点。清理错误逐个报告，仍尝试其他目标，但不进入下一批。直接新增失败时，之前成功添加的节点保留。添加时已存在的节点不会被纳入轮询自动清理。

清理前会把 GUID、DN、DNS 数据和 tombstone 状态与内存快照比较，发现变化就拒绝修改。这一检查与之后的 LDAP 写入是分开的操作，不是原子锁。独立删除模式会读取用户明确指定目标的快照，不校验历史创建者。

Ctrl+C 停止后续操作，不执行自动清理；已经发出的 LDAP 请求可能在取消生效前完成。强制结束、崩溃或连接中断可能留下记录。用户应查询留存名称，再明确指定目标删除；程序没有持久化运行日志或自动恢复机制。

| 退出码 | 含义 |
| --- | --- |
| `0` | 完成；结果中仍可能包含跳过项或已不存在的目标。 |
| `1` | 参数或记录输入错误。 |
| `2` | 其他错误，包括文件／运行环境错误或结果不确定。 |
| `3` | 状态冲突，或非预期的目标存在／不存在。 |
| `4` | 权限不足或身份验证失败。 |
| `5` | 汇总错误／清理未完成，须阅读每一项错误。 |
| `6` | 目录配额或服务器限制。 |
| `7` | 域控不可用或超时。 |
| `130` | 用户取消。 |
