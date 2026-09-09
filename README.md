# SharpADIDNSCycle

**Author: SenRan · Version: 0.8.0**

[中文说明](README.zh-CN.md)

A standalone C# tool for bulk management of Active Directory integrated DNS through LDAP. Add a whole input list, rotate through batches, or explicitly delete supplied names. The application is contained in one `.cs` file.

## Build

Runtime: **64-bit Windows with .NET Framework 4.x** and access to the target domain controller. LDAP authentication uses the current Windows identity unless explicit credentials are supplied.

| File | Purpose |
| --- | --- |
| `SharpADIDNSCycle.cs` | Complete application; the only project file required by `csc.exe`. |
| `build.cmd` | Optional Windows build shortcut. |
| `SharpADIDNSCycle.csproj` | Optional project for Visual Studio or a compatible .NET SDK. |

Run `build.cmd` in Windows Command Prompt. It creates `dist\SharpADIDNSCycle.exe` targeting Windows amd64. Alternatively, compile the source directly:

```bat
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 /optimize+ /r:System.DirectoryServices.dll /out:SharpADIDNSCycle.exe SharpADIDNSCycle.cs
```

`System.DirectoryServices.dll` is supplied by .NET Framework; it is not a project file to copy. SDK builds use the reference assembly package declared in the project:

```text
dotnet build SharpADIDNSCycle.csproj -c Release
```

The SDK may download build references on first use. The resulting EXE still requires Windows and .NET Framework. This repository has no runtime dependency on a SharpADIDNS installation. Attribution for the adapted DNS encoding and byte-order helpers is retained alongside those helpers in the source.

## Three operations

Replace the example zone, domain DN, DC and record data with your own values. Examples below are single commands.

**Add once:** add all valid, available names; leave the new records in place.

```powershell
.\SharpADIDNSCycle.exe add --zone lab.example --dn DC=lab,DC=example --server dc.lab.example --names dns01,dns02,dns03 --data 192.0.2.10
```

**Rotate:** add a batch, hold it, perform the selected cleanup, then advance. Process the list once.

```powershell
.\SharpADIDNSCycle.exe add --zone lab.example --dn DC=lab,DC=example --server dc.lab.example --file names.txt --data 192.0.2.10 --batch-size 6 --jitter 2 --last-time 30 --delete-mode tombstone-remove --tombstone-delay 15
```

**Delete explicitly:** supply names or a file. The default is hard deletion. Use `--delete-skip` to protect selected names.

```powershell
.\SharpADIDNSCycle.exe delete --zone lab.example --dn DC=lab,DC=example --server dc.lab.example --names dns01,dns02,dns03 --delete-mode remove
```

**Deletion applies to the entire named `dnsNode`, including all its DNS values.** A standalone delete command has no history proving which run created a name. An existing record skipped during add can still be deleted by a later command using the same input. Preserve it by removing that name from the deletion input. Inspect targets first when needed:

```powershell
.\SharpADIDNSCycle.exe delete --zone lab.example --dn DC=lab,DC=example --server dc.lab.example --file names.txt --dry-run --show-baseline --delete-skip servicedesk
```

## Every option

Syntax: `SharpADIDNSCycle.exe [add|delete] [options]`. Omitting the action selects `add`. Both `--option value` and `--option=value` work. Flags take no value; duplicate options are errors. Option names are case-sensitive.

### Connection and authentication

| Option | Meaning / default |
| --- | --- |
| `--zone NAME` | Required DNS zone, such as `lab.example`. The zone must already exist. |
| `--dn DOMAIN_DN` | Domain naming context, such as `DC=lab,DC=example`. Required except with `--plan`. |
| `--server HOST` | DC hostname or IPv4 address. No URL scheme, port or path. Required except with `--plan`. |
| `--partition NAME` | `DomainDnsZones` (default), `ForestDnsZones` or `System`; spelling is case-sensitive. Select the container holding the zone. |
| `--ldaps` | TLS on port 636 using Windows certificate validation. Default LDAP uses secure authentication, signing and sealing. |
| `--username USER` | Explicit identity, e.g. `LAB\operator` or `operator@lab.example`. Requires exactly one password source below. Default: current Windows identity. |
| `--password-env ENV` | Read the password from the named environment variable in the EXE process. |
| `--password-stdin` | Read the password from one stdin line. Mutually exclusive with `--password-env`. |

Password values, command-line arguments and stack traces are not dumped into logs. An empty/unavailable password source is an error.

### Input and DNS values

| Option | Meaning / default |
| --- | --- |
| `--file PATH` | Input file accessible to the running EXE; mutually exclusive with `--names`. |
| `--names NAME1,NAME2` | Comma-separated names; alias `--name`. Choose exactly one input source. |
| `--type TYPE` | Add only. `A` by default; also `AAAA`, `CNAME`, `PTR`, `TXT`, `SRV`, `MX`. Case-insensitive value. |
| `--data VALUE` | Add only. Required for name-only input; alias `--ip`. A three-column file row provides its own type/data. |
| `--ttl SECONDS` | Add only. DNS cache TTL, `1..604800`; default `600`. |
| `--mimic-aging` | Add only. Write the current UTC aging hour immediately before each record is added. Default: static timestamp `0`. Does not change server scavenging configuration. |

Names may be zone-relative or end in the selected zone. A trailing dot makes the name absolute; absolute names outside the zone are skipped. Accepted labels are ASCII letters, digits, underscores and interior hyphens, up to 63 characters per label and 253 for the resulting FQDN. Zone apex (`@` or the zone name), wildcards and invalid labels are skipped. Names are normalized to lowercase; duplicate names keep the first occurrence, even when the types differ.

A UTF-8 file can contain one name per line:

```text
# Blank lines and lines beginning with # after whitespace are ignored.
dns01
dns02.lab.example
```

Or `name<TAB>type<TAB>data` with **actual tab separators**:

```text
dns01	A	192.0.2.10
dns02	AAAA	2001:db8::10
alias01	CNAME	dns01.lab.example
```

The two row forms may be mixed. The delete operation uses the name column only and deletes the entire node. Comments must occupy their own lines.

| Type | Example `--data` | Notes |
| --- | --- | --- |
| A | `192.0.2.10` | IPv4. |
| AAAA | `2001:db8::10` | IPv6. |
| CNAME | `dns01.lab.example` | Target hostname. |
| PTR | `dns01.lab.example` | Target hostname; select the appropriate reverse lookup zone. |
| TXT | `"example text"` | ASCII, at most 255 bytes. |
| SRV | `"0 10 443 service.lab.example"` | Priority, weight, port, target; numeric fields `0..65535`. |
| MX | `"10 mail.lab.example"` | Preference `0..65535`, exchange hostname. |

Invalid names, duplicate names and existing add targets are **skips**. Invalid row structure, unsupported types, invalid record data and unreadable files are **errors**. The complete input is validated before connecting or writing. All-invalid or all-existing input completes as a no-op with skips. An empty/comment-only file is an input error.

### Rotation and cleanup

| Option | Meaning / default |
| --- | --- |
| `--batch-size N` | Add only; enables rotation. `1..1000000`. Requires `--last-time`. Omit for direct add. |
| `--last-time SECONDS` | Hold time after the last successful add in a batch, `1..2147483647`. Requires `--batch-size`. |
| `--jitter N` | Random variation in **batch count**, not time. `0..999999`, less than batch size; default `0`. Requires `--batch-size`. |
| `--delete-mode MODE` | `tombstone`, `remove`, `tombstone-remove`; alias `tombstone+remove` for the last value. Default: `tombstone` during rotation; `remove` for standalone delete. |
| `--delete-skip NAME1,NAME2` | Exclude these normalized names from cleanup. Applies to standalone delete and rotating add; a protected name is never tombstoned or hard-deleted by that run. |
| `--tombstone-delay SECONDS` | Only with `tombstone-remove`; `0..2147483647`. Default: one random delay of `10..60` seconds for each cleanup batch. `0` means no extra wait. |

Direct add does not accept cleanup options: use a separate delete command. Standalone delete does not accept rotation or add-record options.

| Cleanup mode | Behavior |
| --- | --- |
| `tombstone` | Replace DNS data with a tombstone and set `dNSTombstoned`. Leave physical removal to server maintenance; the program does not wait for it. |
| `remove` | Hard delete the DNS node through LDAP. |
| `tombstone-remove` | Tombstone targets, wait once for the cleanup batch, then hard delete successfully tombstoned targets through LDAP. An existing tombstone keeps its original timestamp. |

For `--batch-size 6 --jitter 2 --last-time 30`, each batch adds 4–8 new nodes, holds for 30 seconds after its last add, and defaults to tombstone cleanup. The last batch can be smaller. Skipped names do not occupy batch slots. Each new batch waits for the preceding program cleanup to finish.

Actual runtime includes LDAP work and any tombstone delay. Records added early in a batch remain longer than `last-time`. Tombstone-only mode may leave objects from prior batches on the DC; it does not immediately release directory quota. DNS replication, server maintenance and resolver cache expiry have independent timing. Neither tombstoning nor hard deletion promises immediate disappearance from every server/cache.

### Output and checks

| Option | Meaning |
| --- | --- |
| `--show-baseline` | Query and display the supplied names before any write. The table includes existing DNS values, absent names and selected `--delete-skip` names. It is also valid with `--dry-run`; it cannot be combined with `--plan`. |
| No output mode flag | Shows the banner, grouped input skips, clear errors, each rotation batch's skip result, and a final count summary. User-supplied connection values are not repeated in every batch. |
| `--plan` | Validate local inputs only. No LDAP access, writes or waits; does not check whether names already exist. |
| `--dry-run` | Read through LDAP without writes or waits. Does not prove create/delete permissions or remaining quota. |
| `--help`, `-h` | Grouped command help and a short banner. |

`--plan` and `--dry-run` are mutually exclusive. The banner is printed once at startup. Output is native English and uses aligned, wrapped fields; it does not repeat `server`, `zone`, `batch-size`, `jitter` or `last-time` for every batch.

A direct add ends with a compact summary:

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

Rotation keeps each batch compact. It does not repeat connection or timing parameters; it reports only whether that batch had skips, followed by the final totals:

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

Errors remain grouped and actionable. A failed operation includes its target and reason; successful records are counted separately in the summary.

Baseline output covers **only the supplied names**. Nothing is written to a baseline file. It is a readable table for supported decoders (A, AAAA, CNAME, PTR, TXT, SRV, MX, NS, SOA and tombstones). Unknown or malformed values are labeled explicitly; raw values and arbitrary directory attributes are not dumped.

The final result distinguishes successful completion, completion with skips, failure and cancellation. `all added: yes` requires every input entry to have been added without skips and the operation to complete successfully. Counts describe actions, so one input can count as added, tombstoned and removed. A committed add remains counted if a later read-back fails; the overall result is then failure. Error count means failed operations, not necessarily distinct names. An already-absent target is reported separately from a confirmed hard deletion.

Normal output goes to stdout; notices and errors go to stderr. To save a readable log from PowerShell, append:

```powershell
# Append this to your command:
--show-baseline > cycle.log 2>&1
```

`--file` is opened by the EXE on **the machine running that process**. With PSRemoting, copy the file to the remote machine, use an accessible path, or pass `--names`. A path on the operator's Mac is not automatically a remote file.

## Failure and interruption behavior

Permissions and quotas are evaluated by AD, not inferred from whether the caller is a user, machine account or administrator. The program does not assume a universal per-user record limit, automatically reduce batch size, or blindly replay failed writes. Errors identify the phase, target, cause, elapsed time and available server diagnostic; ACL, authentication, quota and connectivity errors remain failures.

A rotation add failure stops new additions and attempts cleanup of the current partial batch using the selected mode. Cleanup errors are reported per target; remaining targets are attempted, and the next batch is not started. Direct-add failure leaves already-created nodes in place. Existing add targets are never adopted into automatic rotation cleanup.

Before cleanup, the program compares node GUID, DN, DNS values and tombstone state with its in-memory snapshot. Changed nodes are refused. This check and the subsequent LDAP write are separate operations, not an atomic lock. Standalone delete deliberately snapshots the explicitly supplied targets; it does not verify historical ownership.

Ctrl+C stops further operations without automatic cleanup. An in-flight LDAP operation may finish before cancellation is observed. A forced exit, crash or disconnect can leave records behind. Query the remaining names and explicitly delete the intended targets; there is no persistent run journal or automatic recovery.

| Exit code | Meaning |
| --- | --- |
| `0` | Completed; skips and already-absent targets may still exist in the results. |
| `1` | Invalid arguments or record input. |
| `2` | Other failure, including file/runtime errors or uncertain outcomes. |
| `3` | State conflict or unexpected target existence/absence. |
| `4` | Access denied or authentication failure. |
| `5` | Aggregated failure / incomplete cleanup. Read every reported error. |
| `6` | Directory quota or server limit. |
| `7` | DC unavailable or timeout. |
| `130` | Cancellation. |
