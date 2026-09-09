// SharpADIDNSCycle v0.8.0
// Author: SenRan
// Encoding component attribution is retained beside DnsRecord below.
// Build: csc /platform:x64 /r:System.DirectoryServices.dll /out:SharpADIDNSCycle.exe SharpADIDNSCycle.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.DirectoryServices;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SharpADIDNSCycle
{
    internal static class Program
    {
        public const string Version = "0.8.0";
        public static int Main(string[] args)
        {
            Log log = new Log();
            Stopwatch elapsed = Stopwatch.StartNew();
            string action = "startup";
            int exitCode = 0;
            bool summarize = true;
            CultureInfo previousCulture = Thread.CurrentThread.CurrentCulture;
            CultureInfo previousUiCulture = Thread.CurrentThread.CurrentUICulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
                Console.WriteLine("SharpADIDNSCycle");
                Console.WriteLine("Bulk LDAP DNS operations");
                Console.WriteLine("Version " + Version + " | Author: SenRan");
                Console.WriteLine();
                Settings s = Settings.Parse(args);
                if (s.Help) { summarize = false; Help(); return 0; }
                action = s.Plan ? "plan" : s.DryRun ? "check" : s.Action == "delete" ? "delete" : s.BatchSize > 0 ? "cycle" : "add";
                List<RecordSpec> records = RecordSpec.Load(s, log);
                log.Validated = records.Count;
                if (records.Count == 0)
                {
                    if (s.ShowBaseline) log.Baseline(new List<BaselineEntry>(), s);
                    return 0;
                }
                if (s.Plan)
                {
                    if (s.Action == "delete") foreach (RecordSpec r in records)
                        if (s.DeleteSkip.Contains(r.Name)) log.Skip("delete_skip", r.Line, r.Name, "Excluded by --delete-skip");
                    return 0;
                }
                if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                    throw new PlatformNotSupportedException("LDAP execution requires Windows. Local --plan is available on other platforms.");
                if (s.Action == "delete")
                    log.Warning("Delete targets the entire named DNS node, including pre-existing records. Use --delete-skip to preserve selected names; --dry-run --show-baseline is read-only.");
                string password = null;
                if (s.PasswordEnv != null) password = Environment.GetEnvironmentVariable(s.PasswordEnv);
                if (s.PasswordStdin) password = Console.ReadLine();
                if (s.Username != null && string.IsNullOrEmpty(password))
                    throw new ArgumentException("The selected password source is empty or unavailable.");
                using (ManualResetEvent cancel = new ManualResetEvent(false))
                {
                    ConsoleCancelEventHandler handler = delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; cancel.Set(); };
                    Console.CancelKeyPress += handler;
                    try
                    {
                        using (LdapRecordStore store = new LdapRecordStore(s, password, log))
                        {
                            Action check = delegate { if (cancel.WaitOne(0)) throw new OperationCanceledException(); };
                            store.CheckCancellation = check;
                            Action<int> wait = delegate(int seconds)
                            {
                                Stopwatch timer = Stopwatch.StartNew();
                                while (timer.Elapsed.TotalSeconds < seconds)
                                {
                                    check();
                                    int ms = (int)Math.Min(250, Math.Ceiling((seconds - timer.Elapsed.TotalSeconds) * 1000));
                                    if (ms > 0 && cancel.WaitOne(ms)) check();
                                }
                                check();
                            };
                            BatchRunner runner = new BatchRunner(s, store, log, new Random(), check, wait);
                            if (s.ShowBaseline) runner.ShowBaseline(records);
                            if (s.DryRun) runner.CheckOnly(records);
                            else if (s.Action == "delete") runner.Delete(records);
                            else runner.Add(records);
                        }
                    }
                    finally { Console.CancelKeyPress -= handler; }
                }
                return 0;
            }
            catch (OperationCanceledException)
            {
                log.Warning("Stopped on cancellation. No automatic cleanup is performed; an in-flight LDAP request may have completed. Query remaining names before deleting them.");
                exitCode = 130; return exitCode;
            }
            catch (AggregateException ex)
            {
                foreach (Exception inner in ex.Flatten().InnerExceptions) log.Failure(inner);
                exitCode = 5; return exitCode;
            }
            catch (OperationFailure ex) { log.Failure(ex); exitCode = ex.ExitCode; return exitCode; }
            catch (ArgumentException ex) { log.Failure(ex); exitCode = 1; return exitCode; }
            catch (Exception ex) { log.Failure(ex); exitCode = 2; return exitCode; }
            finally
            {
                try { if (summarize) log.Summary(action, exitCode, elapsed.Elapsed); }
                finally
                {
                    Thread.CurrentThread.CurrentCulture = previousCulture;
                    Thread.CurrentThread.CurrentUICulture = previousUiCulture;
                }
            }
        }

        private static void Help()
        {
            Console.WriteLine(@"
USAGE
  SharpADIDNSCycle.exe add|delete [connection] [input] [options]

MODES
  add                         Add all valid, available names once.
                              Omit the verb to use add.

  add --batch-size N           Add, hold, clean up, then take the next batch.
                              Requires --last-time. Process input once.

  delete                      Clean up explicitly supplied names.
                              Deletes the entire DNS node, not one value.

CONNECTION
  --zone NAME                 DNS zone, e.g. lab.example. Required.
  --dn DOMAIN_DN              Domain DN, e.g. DC=lab,DC=example.
  --server HOST               DC hostname or IPv4; no URL or port.
                              dn/server are optional only with --plan.
  --partition NAME            DomainDnsZones (default), ForestDnsZones,
                              or System. The zone must already exist.
  --ldaps                     Use TLS on port 636 with certificate checks.
                              Default LDAP uses signing and sealing.
  --username USER             Optional explicit Windows identity.
  --password-env ENV          Read its password from an environment variable.
  --password-stdin            Read its password from one stdin line.
                              Choose one password source with username.
                              Otherwise use the current Windows identity.

INPUT  (choose exactly one)
  --file PATH                 UTF-8 file accessible to the EXE process.
                              One name or name<TAB>type<TAB>data per line.
                              Ignore blank lines and # comments.
  --names NAME1,NAME2          Comma-separated names. Alias: --name.
                              Invalid names and duplicates are skipped.
                              Existing nodes are skipped by add.

RECORDS  (add only)
  --type TYPE                 A (default), AAAA, CNAME, PTR, TXT, SRV or MX.
  --data VALUE                Record value; alias: --ip.
                              Required unless the TSV row supplies data.
                              SRV: priority weight port target
                              MX:  preference exchange
  --ttl SECONDS               1..604800; default 600. DNS cache lifetime.
  --mimic-aging               Use the current aging hour on each add.
                              Default: static DNS timestamp (0).

ROTATION  (add only)
  --batch-size N              1..1000000; new records per batch.
  --last-time SECONDS         1..2147483647; required with batch-size.
                              Hold starts after the last add in that batch.
  --jitter N                  0 by default; must be less than batch-size.
                              Varies count by +/-N, not time. Skips do not
                              consume slots. Last batch may be smaller.

CLEANUP  (rotation or delete)
  --delete-mode MODE          tombstone | remove | tombstone-remove
                              Rotation default: tombstone.
                              Standalone delete default: remove.

    tombstone                 Mark DNS tombstones; leave final removal
                              to server maintenance. No fixed cleanup SLA.
    remove                    LDAP hard delete of the named DNS node.
    tombstone-remove          Tombstone, wait once per batch, then remove.
                              Alias: tombstone+remove.

  --delete-skip NAME1,NAME2    Exclude these names from all cleanup methods.
                              Applies to delete and rotating add. These names
                              may still be added during rotation and kept.
                              Matches normalized names, without wildcards.

  --tombstone-delay SECONDS    Only for tombstone-remove; 0..2147483647.
                              Default: one random delay of 10..60 seconds.
                              0 means no additional delay.

OUTPUT
  --show-baseline              Query and display supplied names before writes.
                              Includes existing DNS values and absent names.
                              Available with --dry-run, but not --plan.
                              No baseline file is written.
  Default                     Banner, grouped skips, errors and final counts.
                              Each rotation batch reports only its skips.

CHECKS
  --plan                      Local input validation only; no LDAP.
  --dry-run                   LDAP reads only; no writes or waiting.
                              Cannot be combined with --plan.
  --help, -h                  Show this help.

EXAMPLES  (replace the zone, domain DN and DC)
  Add names:
    SharpADIDNSCycle.exe add --zone lab.example
      --dn DC=lab,DC=example --server dc.lab.example
      --names dns01,dns02 --data 192.0.2.10

  Rotate a file:
    SharpADIDNSCycle.exe add --zone lab.example
      --dn DC=lab,DC=example --server dc.lab.example --file names.txt
      --data 192.0.2.10 --batch-size 6 --jitter 2 --last-time 30
      --delete-mode tombstone-remove --tombstone-delay 15

  Inspect before deleting:
    SharpADIDNSCycle.exe delete --zone lab.example
      --dn DC=lab,DC=example --server dc.lab.example
      --names dns01,dns02 --dry-run --show-baseline

  The wrapped examples above are single commands; join their lines.

NOTES
  --show-baseline covers supplied names, not the entire DNS zone.
  No baseline file or persistent creation history is written.
  Use --delete-skip to preserve original names during standalone delete.
  Ctrl+C stops subsequent work without automatic cleanup.
  Errors include reasons; skipped items are not successful additions.

EXIT CODES
  0 completed (possibly with skips); 
  1 invalid input; 
  2 other failure;
  3 state conflict; 
  4 access/authentication; 
  5 incomplete cleanup;
  6 quota/limit; 
  7 unavailable/timeout; 
  130 cancelled.
");
        }
    }

    internal sealed class BaselineEntry
    {
        public RecordSpec Record;
        public NodeState State;
        public string Error;
    }

    internal sealed class Log
    {
        private readonly HashSet<Exception> reportedErrors = new HashSet<Exception>();
        private readonly List<string> skipGroups = new List<string>();
        private readonly Dictionary<string, List<string>> pendingSkips = new Dictionary<string, List<string>>();
        public int InputCount, Validated, Checked, Added, Tombstoned, Removed, Absent, AlreadyTombstoned;
        public int Round, CompletedBatches, DeletionSkipped;
        public int Skipped { get; private set; }
        public int ExistingSkipped { get; private set; }
        internal static string Clean(string value)
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in value ?? "")
            {
                if (c == '\n') b.Append("\\n");
                else if (c == '\r') b.Append("\\r");
                else if (c == '\t') b.Append("\\t");
                else if (char.IsControl(c)) b.Append("\\u" + ((int)c).ToString("X4"));
                else b.Append(c);
            }
            return b.ToString();
        }
        private static List<string> Wrap(string value, int width)
        {
            string text = Clean(value);
            List<string> lines = new List<string>();
            while (text.Length > width)
            {
                int cut = text.LastIndexOf(' ', width, width + 1);
                if (cut <= 0) cut = width;
                lines.Add(text.Substring(0, cut));
                text = text.Substring(cut).TrimStart();
            }
            lines.Add(text);
            return lines;
        }
        private static void Field(TextWriter writer, string label, string value)
        {
            List<string> lines = Wrap(value, 58);
            for (int i = 0; i < lines.Count; i++)
                writer.WriteLine((i == 0 ? "  " + label.PadRight(18) + ": " : new string(' ', 22)) + lines[i]);
        }
        public static void Field(string label, string value) { Field(Console.Out, label, value); }
        public void Warning(string message) { Field(Console.Error, "Notice", message); }
        public void Failure(Exception ex)
        {
            AggregateException aggregate = ex as AggregateException;
            if (aggregate != null)
            {
                foreach (Exception inner in aggregate.Flatten().InnerExceptions) Failure(inner);
                return;
            }
            if (!reportedErrors.Add(ex)) return;
            if (Round > 0) Field(Console.Error, "Batch", Round.ToString(CultureInfo.InvariantCulture));
            Field(Console.Error, "Error", ex.Message);
            OperationFailure failure = ex as OperationFailure;
            if (failure != null && !string.IsNullOrEmpty(failure.ServerDetail))
                Field(Console.Error, "Server detail", failure.ServerDetail);
        }
        public void Skip(string reason, int line, string name, string detail)
        {
            string group;
            if (reason == "delete_skip") { DeletionSkipped++; group = "Deletion skipped"; }
            else
            {
                Skipped++;
                if (reason == "already_exists") { ExistingSkipped++; group = "Already exists"; }
                else group = reason == "duplicate_input" ? "Duplicate names" : "Invalid names";
            }
            if (!pendingSkips.ContainsKey(group)) { skipGroups.Add(group); pendingSkips[group] = new List<string>(); }
            pendingSkips[group].Add(reason == "invalid_name" ? name + " (input " + line + ": " + detail + ")" : name);
        }
        public void FlushSkips(bool batch)
        {
            if (batch)
            {
                int count = 0;
                foreach (List<string> names in pendingSkips.Values) count += names.Count;
                Field("Batch " + Round, count == 0 ? "No records skipped." : count + " operation(s) skipped.");
            }
            else if (skipGroups.Count > 0) Console.WriteLine("Skipped records");
            foreach (string group in skipGroups)
                Field(group, string.Join(", ", pendingSkips[group].ToArray()));
            pendingSkips.Clear(); skipGroups.Clear();
        }
        public void Baseline(List<BaselineEntry> entries, Settings settings)
        {
            int existing = 0, absent = 0, failed = 0;
            foreach (BaselineEntry entry in entries)
                if (entry.Error != null) failed++; else if (entry.State == null) absent++; else existing++;
            Console.WriteLine("Baseline");
            Field("Queried at", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
            Field("Names queried", entries.Count.ToString(CultureInfo.InvariantCulture));
            Field("Existing / absent", existing + " / " + absent);
            if (failed > 0) Field("Query failures", failed.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine();
            TableRow("Name", "State", "DNS records");
            TableRow(new string('-', 22), new string('-', 11), new string('-', 39));
            List<string> protectedNames = new List<string>();
            foreach (BaselineEntry entry in entries)
            {
                if (settings.DeleteSkip.Contains(entry.Record.Name)) protectedNames.Add(entry.Record.Name);
                if (entry.Error != null) TableRow(entry.Record.Name, "Unreadable", entry.Error);
                else if (entry.State == null) TableRow(entry.Record.Name, "Absent", "-");
                else
                {
                    string state = entry.State.Tombstoned ? "Tombstoned" : "Active";
                    if (entry.State.Records.Count == 0) TableRow(entry.Record.Name, state, "No DNS values returned");
                    for (int i = 0; i < entry.State.Records.Count; i++)
                        TableRow(i == 0 ? entry.Record.Name : "", i == 0 ? state : "", RecordView.Brief(entry.State.Records[i]));
                }
            }
            if (protectedNames.Count > 0) Field("Delete skip", string.Join(", ", protectedNames.ToArray()));
            Console.WriteLine();
        }
        private static void TableRow(string name, string state, string data)
        {
            List<string> a = Wrap(name, 22), b = Wrap(state, 11), c = Wrap(data, 39);
            int count = Math.Max(a.Count, Math.Max(b.Count, c.Count));
            for (int i = 0; i < count; i++)
                Console.WriteLine("  " + (i < a.Count ? a[i] : "").PadRight(22) + "  " +
                    (i < b.Count ? b[i] : "").PadRight(11) + "  " + (i < c.Count ? c[i] : ""));
        }
        public void Summary(string action, int code, TimeSpan duration)
        {
            FlushSkips(false);
            Round = 0;
            Console.WriteLine();
            Console.WriteLine("Summary");
            Field("Result", code == 130 ? "Cancelled" : code != 0 ? "Incomplete" :
                Skipped + DeletionSkipped > 0 ? "Completed with skips" : "Completed");
            if (action == "plan") Field("Validated", Validated + " names; no LDAP access or changes");
            if (action == "check") Field("Checked", Checked + " names; no changes made");
            if (action == "cycle") Field("Batches", CompletedBatches + " completed");
            if (action == "add" || action == "cycle")
            {
                Field("Added", Added + " records");
                Field("All added", code == 0 && InputCount > 0 && Added == InputCount && Skipped == 0 ? "Yes" : "No");
            }
            if (action == "delete" || action == "cycle")
            {
                Field("Tombstoned", Tombstoned + " nodes");
                Field("Deleted", Removed + " nodes");
            }
            if (AlreadyTombstoned > 0) Field("Already marked", AlreadyTombstoned + " tombstoned nodes");
            if (Absent > 0) Field("Already absent", Absent + " nodes");
            if (Skipped > 0) Field("Input skips", Skipped.ToString(CultureInfo.InvariantCulture));
            if (DeletionSkipped > 0) Field("Deletion skips", DeletionSkipped.ToString(CultureInfo.InvariantCulture));
            Field("Errors", reportedErrors.Count.ToString(CultureInfo.InvariantCulture));
            Field("Elapsed", duration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " seconds");
            if (code != 0) Field("Exit code", code.ToString(CultureInfo.InvariantCulture));
        }
    }

    internal sealed class Settings
    {
        public string Action = "add", File, Names, Zone, DomainDn, Server, Username, PasswordEnv;
        public string Partition = "DomainDnsZones", Type = "A", Data, DeleteMode, DeleteSkipInput;
        public readonly HashSet<string> DeleteSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public int Ttl = 600, BatchSize, Jitter, LastTime, TombstoneDelay = -1;
        public bool PasswordStdin, Ldaps, Plan, DryRun, Help, MimicAging, ShowBaseline;

        public static Settings Parse(string[] args)
        {
            Settings s = new Settings();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> options = new HashSet<string>(new string[] {
                "--file", "--names", "--zone", "--dn", "--server", "--partition", "--type", "--data", "--ttl",
                "--batch-size", "--jitter", "--last-time", "--delete-mode", "--tombstone-delay", "--delete-skip",
                "--username", "--password-env", "--password-stdin", "--ldaps", "--plan", "--dry-run",
                "--mimic-aging", "--show-baseline", "--help"
            }, StringComparer.Ordinal);
            int start = 0;
            if (args.Length > 0 && !args[0].StartsWith("-"))
            {
                s.Action = args[0].ToLowerInvariant(); start = 1;
                if (s.Action != "add" && s.Action != "delete") throw new ArgumentException("Action must be add or delete");
            }
            if (args.Length == 0) { s.Help = true; return s; }
            for (int i = start; i < args.Length; i++)
            {
                string key = args[i], value = null;
                int eq = key.IndexOf('=');
                if (eq >= 0) { value = key.Substring(eq + 1); key = key.Substring(0, eq); }
                if (key == "-h") key = "--help";
                if (key == "--ip") key = "--data";
                if (key == "--name") key = "--names";
                if (!options.Contains(key)) throw new ArgumentException("Unknown option: " + key);
                if (!seen.Add(key)) throw new ArgumentException("Duplicate option: " + key);
                bool flag = key == "--help" || key == "--plan" || key == "--dry-run" || key == "--ldaps" ||
                    key == "--password-stdin" || key == "--mimic-aging" || key == "--show-baseline";
                if (flag && value != null) throw new ArgumentException(key + " does not take a value");
                if (!flag && value == null)
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--")) throw new ArgumentException("Missing value for " + key);
                    value = args[++i];
                }
                switch (key)
                {
                    case "--file": s.File = value; break;
                    case "--names": s.Names = value; break;
                    case "--zone": s.Zone = value; break;
                    case "--dn": s.DomainDn = value; break;
                    case "--server": s.Server = value; break;
                    case "--partition": s.Partition = value; break;
                    case "--type": s.Type = value.ToUpperInvariant(); break;
                    case "--data": s.Data = value; break;
                    case "--ttl": s.Ttl = Number(key, value, 1, 604800); break;
                    case "--batch-size": s.BatchSize = Number(key, value, 1, 1000000); break;
                    case "--jitter": s.Jitter = Number(key, value, 0, 999999); break;
                    case "--last-time": s.LastTime = Number(key, value, 1, int.MaxValue); break;
                    case "--delete-mode": s.DeleteMode = value.ToLowerInvariant(); break;
                    case "--tombstone-delay": s.TombstoneDelay = Number(key, value, 0, int.MaxValue); break;
                    case "--username": s.Username = value; break;
                    case "--password-env": s.PasswordEnv = value; break;
                    case "--password-stdin": s.PasswordStdin = true; break;
                    case "--ldaps": s.Ldaps = true; break;
                    case "--plan": s.Plan = true; break;
                    case "--dry-run": s.DryRun = true; break;
                    case "--mimic-aging": s.MimicAging = true; break;
                    case "--show-baseline": s.ShowBaseline = true; break;
                    case "--delete-skip": s.DeleteSkipInput = value; break;
                    case "--help": s.Help = true; break;
                    default: throw new ArgumentException("Unknown option: " + key);
                }
            }
            if (s.Help) return s;
            if ((s.File == null) == (s.Names == null)) throw new ArgumentException("Specify exactly one of --file or --names");
            if ((s.File != null && string.IsNullOrWhiteSpace(s.File)) || (s.Names != null && string.IsNullOrWhiteSpace(s.Names)))
                throw new ArgumentException("Input cannot be empty");
            if (string.IsNullOrWhiteSpace(s.Zone)) throw new ArgumentException("--zone is required");
            s.Zone = RecordSpec.WithoutRootDot(s.Zone).ToLowerInvariant();
            RecordSpec.ValidateName(s.Zone);
            if (!s.Plan && (string.IsNullOrWhiteSpace(s.DomainDn) || string.IsNullOrWhiteSpace(s.Server)))
                throw new ArgumentException("--dn and --server are required (except with --plan)");
            if (s.DomainDn != null && !Regex.IsMatch(s.DomainDn, @"^(?i:DC)=[A-Za-z0-9_-]+(?:,(?i:DC)=[A-Za-z0-9_-]+)*$"))
                throw new ArgumentException("--dn must be a domain DN, for example DC=example,DC=local");
            if (s.Server != null && Uri.CheckHostName(s.Server) != UriHostNameType.Dns && Uri.CheckHostName(s.Server) != UriHostNameType.IPv4)
                throw new ArgumentException("--server must be a hostname or IPv4 address without scheme, path or port");
            if (s.Partition != "DomainDnsZones" && s.Partition != "ForestDnsZones" && s.Partition != "System")
                throw new ArgumentException("--partition must be DomainDnsZones, ForestDnsZones or System");
            if (s.BatchSize == 0 && (seen.Contains("--last-time") || seen.Contains("--jitter")))
                throw new ArgumentException("--last-time and --jitter require --batch-size");
            if (s.BatchSize > 0 && !seen.Contains("--last-time")) throw new ArgumentException("--batch-size requires --last-time in seconds");
            if (s.BatchSize > 0 && s.Jitter >= s.BatchSize) throw new ArgumentException("--jitter must be smaller than --batch-size");
            if (s.Action == "delete")
                foreach (string k in new string[] { "--batch-size", "--jitter", "--last-time", "--ttl", "--mimic-aging", "--type", "--data" })
                    if (seen.Contains(k)) throw new ArgumentException(k + " applies only to add");
            if (s.Action == "add" && s.BatchSize == 0 && (seen.Contains("--delete-mode") || seen.Contains("--tombstone-delay")))
                throw new ArgumentException("Direct add leaves records in place; use a separate delete command for cleanup");
            if (s.DeleteMode == null) s.DeleteMode = s.Action == "delete" ? "remove" : "tombstone";
            if (s.DeleteMode == "tombstone+remove") s.DeleteMode = "tombstone-remove";
            if (s.DeleteMode != "remove" && s.DeleteMode != "tombstone" && s.DeleteMode != "tombstone-remove")
                throw new ArgumentException("--delete-mode must be tombstone, remove, or tombstone-remove");
            if (seen.Contains("--tombstone-delay") && s.DeleteMode != "tombstone-remove")
                throw new ArgumentException("--tombstone-delay requires --delete-mode tombstone-remove");
            if (s.Plan && s.DryRun) throw new ArgumentException("Choose --plan or --dry-run");
            if (s.Plan && s.ShowBaseline) throw new ArgumentException("--show-baseline requires LDAP queries; use --dry-run instead of --plan");
            if (s.DeleteSkipInput != null)
            {
                if (s.Action == "add" && s.BatchSize == 0)
                    throw new ArgumentException("--delete-skip applies only to delete or add with --batch-size");
                foreach (string name in s.DeleteSkipInput.Split(','))
                {
                    try { s.DeleteSkip.Add(RecordSpec.NormalizeName(name.Trim(), s.Zone)); }
                    catch (ArgumentException ex) { throw new ArgumentException("Invalid --delete-skip name: " + ex.Message); }
                }
            }
            if (s.PasswordStdin && s.PasswordEnv != null) throw new ArgumentException("Choose one password source");
            if ((s.PasswordStdin || s.PasswordEnv != null) && string.IsNullOrWhiteSpace(s.Username))
                throw new ArgumentException("Password options require --username");
            if (s.Username != null && (string.IsNullOrWhiteSpace(s.Username) || (!s.PasswordStdin && s.PasswordEnv == null)))
                throw new ArgumentException("--username requires a nonempty name and --password-env or --password-stdin");
            if (s.PasswordEnv != null && string.IsNullOrWhiteSpace(s.PasswordEnv)) throw new ArgumentException("--password-env cannot be empty");
            return s;
        }
        internal static int Number(string key, string value, int min, int max)
        {
            int n;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < min || n > max)
                throw new ArgumentException(key + " must be in " + min + ".." + max);
            return n;
        }
    }

    internal interface IRecordStore
    {
        NodeState Inspect(RecordSpec record);
        void CheckAbsent(RecordSpec record);
        Lease Read(RecordSpec record);
        void Create(Lease lease);
        void Tombstone(Lease lease);
        void Remove(Lease lease);
    }

    internal sealed class Lease
    {
        public RecordSpec Record;
        public bool Attempted, Created, Absent;
        public NodeState Expected;
        public Lease(RecordSpec record) { Record = record; }
    }

    internal sealed class NodeState
    {
        public Guid Id;
        public string Dn, CreatedUtc, ChangedUtc;
        public bool Tombstoned;
        public readonly List<byte[]> Records = new List<byte[]>();
        public bool Same(NodeState other)
        {
            if (other == null || Id == Guid.Empty || Id != other.Id || !string.Equals(Dn, other.Dn, StringComparison.OrdinalIgnoreCase) ||
                Tombstoned != other.Tombstoned || Records.Count != other.Records.Count) return false;
            List<string> a = new List<string>(), b = new List<string>();
            foreach (byte[] value in Records) a.Add(Convert.ToBase64String(value));
            foreach (byte[] value in other.Records) b.Add(Convert.ToBase64String(value));
            a.Sort(StringComparer.Ordinal); b.Sort(StringComparer.Ordinal);
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }

    internal sealed class BatchRunner
    {
        private readonly Settings settings;
        private readonly IRecordStore store;
        private readonly Log log;
        private readonly Random random;
        private readonly Action check;
        private readonly Action<int> wait;
        public BatchRunner(Settings s, IRecordStore db, Log output, Random rng, Action checkCancel, Action<int> waitSeconds)
        { settings = s; store = db; log = output; random = rng; check = checkCancel; wait = waitSeconds; }

        public void ShowBaseline(List<RecordSpec> records)
        {
            List<BaselineEntry> entries = new List<BaselineEntry>();
            List<Exception> errors = new List<Exception>();
            foreach (RecordSpec r in records)
            {
                check();
                BaselineEntry entry = new BaselineEntry { Record = r };
                try { entry.State = store.Inspect(r); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { entry.Error = ex.Message; errors.Add(ex); }
                entries.Add(entry);
            }
            // Complete and print the requested snapshot before permitting any writes.
            log.Baseline(entries, settings);
            if (errors.Count > 0) throw new AggregateException("Baseline query failed; no changes were made", errors);
        }
        private bool SkipDeletion(RecordSpec r)
        {
            if (!settings.DeleteSkip.Contains(r.Name)) return false;
            log.Skip("delete_skip", r.Line, r.Name, "Excluded by --delete-skip");
            return true;
        }
        public void CheckOnly(List<RecordSpec> records)
        {
            foreach (RecordSpec r in records)
            {
                check();
                if (settings.Action == "delete")
                {
                    if (SkipDeletion(r)) continue;
                    store.Read(r);
                }
                else
                {
                    try { store.CheckAbsent(r); }
                    catch (OperationFailure ex)
                    {
                        if (ex.Kind != "already_exists") throw;
                        log.Skip("already_exists", r.Line, r.Name, "Existing node kept");
                    }
                }
                log.Checked++;
            }
        }
        public void Add(List<RecordSpec> records)
        {
            List<RecordSpec> eligible = new List<RecordSpec>();
            foreach (RecordSpec r in records)
            {
                check();
                try { store.CheckAbsent(r); eligible.Add(r); }
                catch (OperationFailure ex)
                {
                    if (ex.Kind != "already_exists") throw;
                    log.Skip("already_exists", r.Line, r.Name, "Existing node kept");
                }
            }
            log.FlushSkips(false);
            int offset = 0, round = 0;
            while (offset < eligible.Count)
            {
                check(); round++;
                log.Round = settings.BatchSize > 0 ? round : 0;
                try
                {
                    int count = settings.BatchSize == 0 ? eligible.Count : Math.Min(eligible.Count - offset,
                        random.Next(settings.BatchSize - settings.Jitter, settings.BatchSize + settings.Jitter + 1));
                    List<Lease> active = new List<Lease>();
                    int created = 0;
                    Exception failure = null;
                    try
                    {
                        while (created < count && offset < eligible.Count)
                        {
                            check();
                            Lease lease = new Lease(eligible[offset++]); active.Add(lease);
                            try { store.Create(lease); }
                            catch (OperationFailure ex)
                            {
                                // Never adopt a same-named node after an uncertain create response.
                                if (ex.Kind != "already_exists" || lease.Attempted || lease.Created) throw;
                                active.Remove(lease);
                                log.Skip("already_exists", lease.Record.Line, lease.Record.Name, "Name became occupied before add");
                            }
                            finally { if (lease.Created) created++; }
                        }
                        if (settings.BatchSize > 0 && created > 0) { check(); wait(settings.LastTime); }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failure = ex; log.Failure(ex); }
                    check();
                    if (settings.BatchSize > 0)
                    {
                        List<Lease> pending = active.FindAll(delegate(Lease x) { return x.Attempted; });
                        try { Cleanup(pending); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            if (failure != null) throw new AggregateException("Add and cleanup failed", failure, ex);
                            throw;
                        }
                    }
                    if (failure != null) throw failure;
                    if (settings.BatchSize > 0) log.CompletedBatches++;
                }
                finally
                {
                    if (settings.BatchSize > 0) log.FlushSkips(true);
                    log.Round = 0;
                }
            }
        }
        public void Delete(List<RecordSpec> records)
        {
            List<Lease> targets = new List<Lease>();
            List<Exception> errors = new List<Exception>();
            foreach (RecordSpec r in records)
            {
                check();
                if (SkipDeletion(r)) continue;
                try { Lease target = store.Read(r); if (target != null) targets.Add(target); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add(ex); log.Failure(ex); }
            }
            try { Cleanup(targets); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add(ex); }
            if (errors.Count > 0) throw new AggregateException("Delete incomplete", errors);
        }
        private void Cleanup(List<Lease> targets)
        {
            List<Exception> errors = new List<Exception>();
            List<Lease> ready = new List<Lease>();
            foreach (Lease lease in targets)
            {
                check();
                // Exclude before both tombstoning and hard deletion, including error cleanup.
                if (SkipDeletion(lease.Record)) continue;
                if (settings.DeleteMode == "remove") { ready.Add(lease); continue; }
                try { store.Tombstone(lease); if (!lease.Absent) ready.Add(lease); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add(ex); log.Failure(ex); }
            }
            if (settings.DeleteMode == "tombstone-remove" && ready.Count > 0)
            {
                check();
                int delay = settings.TombstoneDelay >= 0 ? settings.TombstoneDelay : random.Next(10, 61);
                wait(delay);
            }
            if (settings.DeleteMode != "tombstone") foreach (Lease lease in ready)
            {
                check();
                try { store.Remove(lease); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add(ex); log.Failure(ex); }
            }
            if (errors.Count > 0) throw new AggregateException("Cleanup incomplete; inspect the listed names before retrying", errors);
        }
    }

    internal sealed class LdapRecordStore : IRecordStore, IDisposable
    {
        private readonly Settings settings;
        private readonly string password, zoneDn;
        private readonly DirectoryEntry zone;
        private readonly Log log;
        public Action CheckCancellation = delegate { };
        public LdapRecordStore(Settings s, string pwd, Log output)
        {
            settings = s; password = pwd; log = output;
            string container = s.Partition == "System" ? "CN=MicrosoftDNS,CN=System," : "CN=MicrosoftDNS,DC=" + s.Partition + ",";
            zoneDn = "DC=" + s.Zone + "," + container + s.DomainDn;
            zone = Open(zoneDn);
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                object bound = zone.NativeObject;
                if (!string.Equals(zone.SchemaClassName, "dnsZone", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Target is not a dnsZone");
            }
            catch (Exception ex) { zone.Dispose(); throw OperationFailure.Wrap(ex, "bind_zone", s.Server, zoneDn, 0, false, timer.Elapsed.TotalMilliseconds); }
        }
        private DirectoryEntry Open(string dn)
        {
            AuthenticationTypes auth = AuthenticationTypes.Secure | AuthenticationTypes.ServerBind;
            auth |= settings.Ldaps ? AuthenticationTypes.SecureSocketsLayer : AuthenticationTypes.Signing | AuthenticationTypes.Sealing;
            return new DirectoryEntry("LDAP://" + settings.Server + (settings.Ldaps ? ":636/" : "/") + dn, settings.Username, password, auth);
        }
        // Validated ASCII labels exclude LDAP RDN metacharacters.
        private string Dn(RecordSpec r) { return "DC=" + r.Name + "," + zoneDn; }
        private static bool Missing(COMException ex) { return OperationFailure.Classify(ex) == "not_found"; }
        public NodeState Inspect(RecordSpec r)
        {
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                using (DirectoryEntry node = Open(Dn(r)))
                {
                    try { return ReadState(node); }
                    catch (COMException ex) { if (Missing(ex)) return null; throw; }
                }
            }
            catch (Exception ex) { throw OperationFailure.Wrap(ex, "read_node", settings.Server, Dn(r), r.Line, false, timer.Elapsed.TotalMilliseconds); }
        }
        public void CheckAbsent(RecordSpec r)
        {
            if (Inspect(r) != null)
                throw OperationFailure.Wrap(new OperationFailure("already_exists", "Name already exists; left unchanged", null),
                    "check_name", settings.Server, Dn(r), r.Line, false);
        }
        public Lease Read(RecordSpec r)
        {
            NodeState state = Inspect(r);
            if (state == null) { log.Absent++; return null; }
            return new Lease(r) { Expected = state };
        }
        public void Create(Lease lease)
        {
            RecordSpec r = lease.Record;
            string phase = "check_name";
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                CheckAbsent(r);
                r.PrepareForWrite(settings.MimicAging);
                phase = "prepare_add";
                using (DirectoryEntry node = zone.Children.Add("DC=" + r.Name, "dnsNode"))
                {
                    node.Properties["dnsRecord"].Add(r.Blob);
                    node.Properties["dNSTombstoned"].Value = false;
                    CheckCancellation();
                    phase = "commit_add"; lease.Attempted = true;
                    try { node.CommitChanges(); }
                    catch (Exception ex)
                    {
                        if (OperationFailure.DefiniteAddRejection(ex)) lease.Attempted = false;
                        throw;
                    }
                    lease.Created = true; log.Added++;
                    phase = "read_created_guid";
                    NodeState expected = new NodeState { Id = node.Guid, Dn = Dn(r), Tombstoned = false };
                    if (expected.Id == Guid.Empty) throw new InvalidOperationException("Add committed, but the server did not return object GUID");
                    expected.Records.Add((byte[])r.Blob.Clone()); lease.Expected = expected;
                    // Keep exactly the data we wrote as the cleanup guard; a later reader cannot adopt changes.
                    phase = "read_created_node";
                    NodeState observed = ReadState(node);
                    if (!expected.Same(observed))
                        throw new OperationFailure("conflict", "Add committed, but the node changed before read-back; automatic cleanup must not adopt its new state", null);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw OperationFailure.Wrap(ex, phase, settings.Server, Dn(r), r.Line, lease.Attempted && !lease.Created, timer.Elapsed.TotalMilliseconds);
            }
        }
        public void Tombstone(Lease lease) { Change(lease, false); }
        public void Remove(Lease lease) { Change(lease, true); }
        private void MarkAbsent(Lease lease)
        {
            lease.Absent = true; log.Absent++;
        }
        private void Change(Lease lease, bool remove)
        {
            string phase = "read_cleanup_state";
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                if (lease.Absent) return;
                if (lease.Expected == null || lease.Expected.Id == Guid.Empty)
                    throw new OperationFailure("uncertain", "No confirmed object GUID; query this name before explicitly deleting it", null);
                using (DirectoryEntry node = Open(Dn(lease.Record)))
                {
                    NodeState actual;
                    try { actual = ReadState(node); }
                    catch (COMException ex)
                    {
                        if (!Missing(ex)) throw;
                        MarkAbsent(lease); return;
                    }
                    if (!lease.Expected.Same(actual))
                        throw new OperationFailure("conflict", "GUID, DNS data, tombstone state or DN changed; refusing this modification", null);
                    if (!remove && actual.Tombstoned)
                    {
                        log.AlreadyTombstoned++; return;
                    }
                    NodeState next = null;
                    if (!remove)
                    {
                        byte[] tombstone = DnsRecord.BuildTombstone();
                        next = new NodeState { Id = actual.Id, Dn = actual.Dn, Tombstoned = true,
                            CreatedUtc = actual.CreatedUtc, ChangedUtc = "not read back after write" };
                        next.Records.Add(tombstone);
                        node.Properties["dnsRecord"].Clear();
                        node.Properties["dnsRecord"].Add(tombstone);
                        node.Properties["dNSTombstoned"].Value = true;
                    }
                    CheckCancellation();
                    phase = remove ? "commit_remove" : "commit_tombstone";
                    try
                    {
                        if (remove) zone.Children.Remove(node); else node.CommitChanges();
                    }
                    catch (COMException)
                    {
                        // Confirm a lost reply through a fresh read; never replay the write blindly.
                        bool confirmed = false, absent = false;
                        try
                        {
                            using (DirectoryEntry verify = Open(Dn(lease.Record)))
                            {
                                try
                                {
                                    NodeState observed = ReadState(verify);
                                    confirmed = !remove && next.Same(observed);
                                    if (confirmed) next = observed;
                                }
                                catch (COMException ex) { if (!Missing(ex)) throw; confirmed = true; absent = true; }
                            }
                        }
                        catch (Exception) { /* Preserve the original server error. */ }
                        if (!confirmed) throw;
                        if (absent)
                        {
                            MarkAbsent(lease); return;
                        }
                    }
                    if (remove) { lease.Absent = true; log.Removed++; }
                    else { lease.Expected = next; log.Tombstoned++; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw OperationFailure.Wrap(ex, phase, settings.Server, Dn(lease.Record), lease.Record.Line,
                    phase == "commit_remove" || phase == "commit_tombstone", timer.Elapsed.TotalMilliseconds);
            }
        }
        private static string DirectoryDate(object value)
        {
            if (value == null) return "not returned";
            if (value is DateTime) return ((DateTime)value).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        private static NodeState ReadState(DirectoryEntry node)
        {
            node.RefreshCache(new string[] { "objectGUID", "dnsRecord", "dNSTombstoned", "distinguishedName", "whenCreated", "whenChanged" });
            if (!string.Equals(node.SchemaClassName, "dnsNode", StringComparison.OrdinalIgnoreCase))
                throw new OperationFailure("conflict", "Target is not a dnsNode", null);
            byte[] guid = node.Properties["objectGUID"].Value as byte[];
            NodeState state = new NodeState
            {
                Id = guid == null ? Guid.Empty : new Guid(guid),
                Dn = Convert.ToString(node.Properties["distinguishedName"].Value),
                Tombstoned = string.Equals(Convert.ToString(node.Properties["dNSTombstoned"].Value), "True", StringComparison.OrdinalIgnoreCase),
                CreatedUtc = DirectoryDate(node.Properties["whenCreated"].Value),
                ChangedUtc = DirectoryDate(node.Properties["whenChanged"].Value)
            };
            if (state.Id == Guid.Empty || string.IsNullOrEmpty(state.Dn)) throw new InvalidOperationException("Missing node identity");
            foreach (object value in node.Properties["dnsRecord"])
            {
                byte[] blob = value as byte[];
                if (blob == null) throw new InvalidOperationException("dnsRecord has an unexpected value type");
                state.Records.Add((byte[])blob.Clone());
            }
            return state;
        }
        public void Dispose() { zone.Dispose(); }
    }

    internal static class RecordView
    {
        private static void Require(byte[] b, int offset, int count)
        {
            if (b == null || offset < 0 || count < 0 || offset > b.Length - count)
                throw new FormatException("truncated record");
        }
        private static ushort Header(byte[] b)
        {
            Require(b, 0, 24);
            if (Bin.ReadU16Le(b, 0) != b.Length - 24) throw new FormatException("payload length does not match header");
            return Bin.ReadU16Le(b, 2);
        }
        private static string Date(DateTime time) { return time.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture); }
        private static string Aging(uint hour)
        {
            if (hour == 0) return "0 (static)";
            try { return hour + " (" + Date(new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(hour)) + ")"; }
            catch (ArgumentOutOfRangeException) { return hour + " (outside supported date range)"; }
        }
        private static string Text(byte[] b, int offset, int count)
        {
            Require(b, offset, count);
            StringBuilder text = new StringBuilder();
            for (int i = offset; i < offset + count; i++)
            {
                byte c = b[i];
                if (c == 34 || c == 92) text.Append('\\').Append((char)c);
                else if (c >= 32 && c <= 126) text.Append((char)c);
                else text.Append("\\x").Append(c.ToString("X2"));
            }
            return text.ToString();
        }
        private static string Name(byte[] b, ref int offset)
        {
            Require(b, offset, 2);
            int length = b[offset++], count = b[offset++];
            Require(b, offset, length);
            int end = offset + length;
            if (length == 0 && count == 0) return ".";
            List<string> labels = new List<string>();
            for (int i = 0; i < count; i++)
            {
                if (offset >= end) throw new FormatException("truncated DNS name");
                int n = b[offset++];
                if (n == 0 || n > 63 || n > end - offset) throw new FormatException("invalid DNS label length");
                labels.Add(Text(b, offset, n)); offset += n;
            }
            if (offset != end - 1 || b[offset] != 0) throw new FormatException("invalid DNS name terminator");
            offset = end;
            return string.Join(".", labels.ToArray()) + ".";
        }
        private static string Payload(byte[] b, ushort type)
        {
            int p = 24;
            string result;
            switch (type)
            {
                case DnsRecord.TypeZero:
                    Require(b, p, 8);
                    ulong fileTime = Bin.ReadU64Le(b, p); p += 8;
                    try
                    {
                        result = fileTime > long.MaxValue ? "invalid tombstone time" : "entombed " + Date(DateTime.FromFileTimeUtc((long)fileTime));
                    }
                    catch (ArgumentOutOfRangeException) { result = "invalid tombstone time"; }
                    break;
                case DnsRecord.TypeA:
                case DnsRecord.TypeAaaa:
                    int n = type == DnsRecord.TypeA ? 4 : 16;
                    Require(b, p, n);
                    byte[] ip = new byte[n]; Buffer.BlockCopy(b, p, ip, 0, n); p += n;
                    result = new IPAddress(ip).ToString(); break;
                case DnsRecord.TypeNs:
                case DnsRecord.TypeCname:
                case DnsRecord.TypePtr:
                    result = Name(b, ref p); break;
                case DnsRecord.TypeMx:
                    Require(b, p, 2);
                    result = "preference " + Bin.ReadU16Be(b, p); p += 2;
                    result += " | exchange " + Name(b, ref p); break;
                case DnsRecord.TypeSrv:
                    Require(b, p, 6);
                    result = "priority " + Bin.ReadU16Be(b, p) + " | weight " + Bin.ReadU16Be(b, p + 2) + " | port " + Bin.ReadU16Be(b, p + 4); p += 6;
                    result += " | target " + Name(b, ref p); break;
                case DnsRecord.TypeSoa:
                    Require(b, p, 20);
                    result = "serial " + Bin.ReadU32Be(b, p) + " | refresh " + Bin.ReadU32Be(b, p + 4) +
                        "s | retry " + Bin.ReadU32Be(b, p + 8) + "s | expire " + Bin.ReadU32Be(b, p + 12) +
                        "s | minimum " + Bin.ReadU32Be(b, p + 16) + "s"; p += 20;
                    result += " | primary " + Name(b, ref p) + " | administrator " + Name(b, ref p); break;
                case DnsRecord.TypeTxt:
                    List<string> segments = new List<string>();
                    while (p < b.Length)
                    {
                        int size = b[p++]; segments.Add("\"" + Text(b, p, size) + "\""); p += size;
                    }
                    result = string.Join(" ", segments.ToArray()); break;
                default:
                    return (b.Length - 24) + " payload bytes (this record type is not decoded)";
            }
            if (p != b.Length) throw new FormatException("unexpected trailing payload");
            return result;
        }
        public static string Brief(byte[] b)
        {
            try
            {
                ushort type = Header(b);
                return DnsRecord.TypeName(type) + " | " + Payload(b, type) + " | TTL " + Bin.ReadU32Be(b, 12) +
                    "s | aging " + Aging(Bin.ReadU32Le(b, 20));
            }
            catch (FormatException ex) { return "Unreadable DNS record (" + (b == null ? 0 : b.Length) + " bytes): " + ex.Message; }
        }
        public static string Fields(byte[] b)
        {
            try
            {
                ushort type = Header(b);
                return DnsRecord.TypeName(type) + " (" + type + ") | version " + b[4] + " | rank " + b[5] +
                    " | flags " + Bin.ReadU16Le(b, 6) + " | serial " + Bin.ReadU32Le(b, 8) +
                    " | reserved " + Bin.ReadU32Le(b, 16) + " | payload " + (b.Length - 24) + " bytes";
            }
            catch (FormatException ex) { return "Metadata unavailable: " + ex.Message; }
        }
    }

    internal sealed class RecordSpec
    {
        public int Line;
        public string Name, Type, Data;
        public byte[] Blob;

        internal void PrepareForWrite(bool mimicAging)
        {
            Bin.WriteU32Le(Blob, 20, mimicAging ? DnsRecord.AgingTimestampNow() : 0u);
        }

        public static List<RecordSpec> Load(Settings s, Log log)
        {
            List<RecordSpec> records = new List<RecordSpec>();
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (s.File != null)
            {
                using (StreamReader reader = new StreamReader(s.File, new UTF8Encoding(false, true), true))
                {
                    string line; int lineNo = 0;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lineNo++;
                        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;
                        AddInput(records, names, s, log, line, lineNo, true);
                    }
                }
            }
            else
            {
                string[] input = s.Names.Split(',');
                for (int i = 0; i < input.Length; i++) AddInput(records, names, s, log, input[i], i + 1, false);
            }
            if (records.Count == 0 && log.Skipped == 0) throw new ArgumentException("The input contains no records");
            return records;
        }
        private static void AddInput(List<RecordSpec> records, HashSet<string> names, Settings s, Log log, string line, int lineNo, bool file)
        {
            log.InputCount++;
            try
            {
                string[] columns = file ? line.Split(new char[] { '\t' }, 3) : new string[] { line };
                if (columns.Length != 1 && columns.Length != 3)
                    throw new ArgumentException("Expected name or name<TAB>type<TAB>data");
                RecordSpec r = new RecordSpec();
                r.Line = lineNo;
                string inputName = columns[0].Trim();
                try { r.Name = NormalizeName(inputName, s.Zone); }
                catch (ArgumentException ex)
                {
                    log.Skip("invalid_name", lineNo, inputName, ex.Message);
                    return;
                }
                if (!names.Add(r.Name))
                {
                    log.Skip("duplicate_input", lineNo, r.Name, "Keeping the first occurrence");
                    return;
                }
                if (s.Action == "add")
                {
                    r.Type = columns.Length == 1 ? s.Type : columns[1].Trim().ToUpperInvariant();
                    r.Data = columns.Length == 1 ? s.Data : columns[2];
                    r.Blob = Encode(r.Type, r.Data, s.Ttl);
                }
                records.Add(r);
            }
            catch (ArgumentException ex) { throw new ArgumentException("Input line/item " + lineNo + ": " + ex.Message); }
        }

        internal static string NormalizeName(string name, string zone)
        {
            string trimmed = WithoutRootDot(name);
            if (trimmed.Equals(zone, StringComparison.OrdinalIgnoreCase) || trimmed == "@")
                throw new ArgumentException("Zone apex is an existing node; use a new record name");
            string suffix = "." + zone;
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, trimmed.Length - suffix.Length);
            else if (name.EndsWith("."))
                throw new ArgumentException("Absolute name is outside --zone: " + name);
            ValidateName(trimmed);
            ValidateName(trimmed + suffix);
            return trimmed.ToLowerInvariant();
        }

        internal static void ValidateName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 253)
                throw new ArgumentException("DNS name must contain 1..253 ASCII characters");
            foreach (string label in name.Split('.'))
                if (!Regex.IsMatch(label, @"^[A-Za-z0-9_](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?$"))
                    throw new ArgumentException("Invalid DNS label: " + label);
        }

        internal static string WithoutRootDot(string name)
        {
            return name.EndsWith(".", StringComparison.Ordinal) ? name.Substring(0, name.Length - 1) : name;
        }

        internal static byte[] Encode(string type, string data, int ttl)
        {
            if (data == null) throw new ArgumentException("Missing --data (or TSV data column)");
            if (type == "TXT")
            {
                foreach (char c in data) if (c > 127) throw new ArgumentException("TXT supports ASCII only");
                return DnsRecord.BuildTxt(data, ttl);
            }
            data = data.Trim();
            IPAddress ip;
            if (type == "A" || type == "AAAA")
            {
                if (!IPAddress.TryParse(data, out ip)) throw new ArgumentException("Invalid IP address: " + data);
                return type == "A" ? DnsRecord.BuildA(ip, ttl) : DnsRecord.BuildAaaa(ip, ttl);
            }
            string[] p = data.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (type == "SRV")
            {
                if (p.Length != 4) throw new ArgumentException("SRV data: priority weight port target");
                ValidateName(WithoutRootDot(p[3]));
                return DnsRecord.BuildSrv(U16(p[0]), U16(p[1]), U16(p[2]), p[3], ttl);
            }
            if (type == "MX")
            {
                if (p.Length != 2) throw new ArgumentException("MX data: preference exchange");
                ValidateName(WithoutRootDot(p[1]));
                return DnsRecord.BuildMx(U16(p[0]), p[1], ttl);
            }
            ValidateName(WithoutRootDot(data));
            if (type == "CNAME") return DnsRecord.BuildCname(data, ttl);
            if (type == "PTR") return DnsRecord.BuildPtr(data, ttl);
            throw new ArgumentException("Unsupported type: " + type);
        }

        private static ushort U16(string value) { return (ushort)Settings.Number("DNS field", value, 0, 65535); }
    }

    internal sealed class OperationFailure : Exception
    {
        public readonly string Kind;
        public string ServerDetail;
        private bool contextual;
        public int ExitCode
        {
            get
            {
                if (Kind == "access_denied" || Kind == "authentication") return 4;
                if (Kind == "quota_or_limit") return 6;
                if (Kind == "unavailable_or_timeout") return 7;
                if (Kind == "conflict" || Kind == "already_exists" || Kind == "not_found") return 3;
                return 2;
            }
        }

        public OperationFailure(string kind, string message, Exception inner) : base(message, inner) { Kind = kind; }

        public static OperationFailure Wrap(Exception ex, string phase, string server, string dn, int line, bool uncertain, double elapsedMs = -1)
        {
            OperationFailure existing = ex as OperationFailure;
            if (existing != null && existing.contextual) return existing;
            string kind = Classify(ex);
            COMException com = ex as COMException;
            DirectoryServicesCOMException ds = ex as DirectoryServicesCOMException;
            string diagnostic = com == null ? "" : "HRESULT 0x" + unchecked((uint)com.ErrorCode).ToString("X8");
            if (ds != null) diagnostic += " | extended 0x" + unchecked((uint)ds.ExtendedError).ToString("X8") +
                " | " + ds.ExtendedErrorMessage;
            string hint = kind == "quota_or_limit" ? " Check directory quota/server limits. There is no fixed per-round allowance inferred from account type; tombstones still occupy AD objects." :
                kind == "access_denied" ? " Check the effective identity and target ACL. Create, modify and delete permissions are separate." :
                kind == "authentication" ? " Check credentials and authentication policy." :
                kind == "unavailable_or_timeout" ? " Check DC availability, connectivity and request limits." : "";
            OperationFailure failure = new OperationFailure(kind, phase.Replace('_', ' ') + " | " + kind.Replace('_', ' ') +
                " | server: " + server + " | input: " + line + " | DN: " + dn +
                (elapsedMs < 0 ? "" : " | elapsed: " + elapsedMs.ToString("F1", CultureInfo.InvariantCulture) + " ms") +
                " | " + ex.Message + hint + (uncertain ? " Write outcome is uncertain; query this name before retrying." : ""), ex);
            failure.contextual = true;
            failure.ServerDetail = diagnostic;
            return failure;
        }

        // ADSI can report a generic HRESULT with the useful Win32 code at the start of its server diagnostic.
        internal static string ClassifyCodes(int hresult, string extended)
        {
            Match match = Regex.Match(extended ?? "", @"^\s*([0-9a-fA-F]{8}):");
            uint detail;
            if (match.Success && uint.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out detail))
            {
                string detailed = ClassifyNative((int)(detail & 0xFFFF));
                if (detailed != "operation_failed") return detailed;
            }
            uint value = unchecked((uint)hresult);
            return ClassifyNative((value & 0xFFFF0000u) == 0x80070000u || value <= 65535 ? (int)(value & 0xFFFF) : -1);
        }

        private static string ClassifyNative(int code)
        {
            switch (code)
            {
                case 5: case 8344: return "access_denied";
                case 1326: case 1330: case 1331: case 1909: return "authentication";
                case 1816: case 8227: case 8228: case 8304: return "quota_or_limit";
                case 8206: case 8207: case 8226: case 8250: case 1460: case 1722: case 121: case 64:
                    return "unavailable_or_timeout";
                case 2: case 8240: return "not_found";
                case 5010: case 183: case 8305: return "already_exists";
                case 8205: return "conflict";
                case 87: case 123: case 8203: case 8212: case 8239: case 8242: case 8247:
                    return "constraint_or_invalid_data";
                default: return "operation_failed";
            }
        }

        public static string Classify(Exception ex)
        {
            OperationFailure failure = ex as OperationFailure;
            if (failure != null) return failure.Kind;
            if (ex is UnauthorizedAccessException) return "access_denied";
            COMException com = ex as COMException;
            DirectoryServicesCOMException ds = ex as DirectoryServicesCOMException;
            return com == null ? "operation_failed" : ClassifyCodes(com.ErrorCode, ds == null ? null : ds.ExtendedErrorMessage);
        }

        internal static bool DefiniteAddRejection(Exception ex)
        {
            string kind = Classify(ex);
            // Do not include quota_or_limit: some ADSI providers map timeouts to ERROR_NOT_ENOUGH_QUOTA.
            return kind == "access_denied" || kind == "authentication" || kind == "conflict" || kind == "already_exists" ||
                kind == "constraint_or_invalid_data" || kind == "not_found" ||
                (ex is COMException && ((COMException)ex).ErrorCode == unchecked((int)0x80072024));
        }
    }

    internal static class DnsRecord
    {
        public const ushort TypeZero  = 0x0000; // tombstone
        public const ushort TypeA     = 0x0001;
        public const ushort TypeNs    = 0x0002;
        public const ushort TypeCname = 0x0005;
        public const ushort TypeSoa   = 0x0006;
        public const ushort TypePtr   = 0x000C;
        public const ushort TypeMx    = 0x000F;
        public const ushort TypeTxt   = 0x0010;
        public const ushort TypeAaaa  = 0x001C;
        public const ushort TypeSrv   = 0x0021;

        public static string TypeName(ushort t)
        {
            switch (t)
            {
                case TypeZero:  return "TS";
                case TypeA:     return "A";
                case TypeNs:    return "NS";
                case TypeCname: return "CNAME";
                case TypeSoa:   return "SOA";
                case TypePtr:   return "PTR";
                case TypeMx:    return "MX";
                case TypeTxt:   return "TXT";
                case TypeAaaa:  return "AAAA";
                case TypeSrv:   return "SRV";
                default:        return "Type" + t;
            }
        }

        public static ushort GetType(byte[] data)
        {
            if (data == null || data.Length < 4) return 0xFFFF;
            return Bin.ReadU16Le(data, 2);
        }

        public static byte[] BuildA(IPAddress ip, int ttl, uint timestamp = 0)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("BuildA requires an IPv4 address");
            byte[] data = ip.GetAddressBytes();
            return BuildHeader(TypeA, data, ttl, timestamp);
        }

        public static byte[] BuildAaaa(IPAddress ip, int ttl, uint timestamp = 0)
        {
            if (ip.AddressFamily != AddressFamily.InterNetworkV6)
                throw new ArgumentException("BuildAaaa requires an IPv6 address");
            byte[] data = ip.GetAddressBytes();
            return BuildHeader(TypeAaaa, data, ttl, timestamp);
        }

        public static byte[] BuildCname(string target, int ttl, uint timestamp = 0)
        {
            if (string.IsNullOrWhiteSpace(target))
                throw new ArgumentException("CNAME target cannot be empty");
            byte[] data = EncodeCountName(target);
            return BuildHeader(TypeCname, data, ttl, timestamp);
        }

        public static byte[] BuildTxt(string text, int ttl, uint timestamp = 0)
        {
            if (text == null) text = "";
            byte[] raw = Encoding.ASCII.GetBytes(text);
            if (raw.Length > 255)
                throw new ArgumentException(
                    "TXT data exceeds the supported 255 bytes");
            byte[] data = new byte[1 + raw.Length];
            data[0] = (byte)raw.Length;
            Buffer.BlockCopy(raw, 0, data, 1, raw.Length);
            return BuildHeader(TypeTxt, data, ttl, timestamp);
        }

        public static byte[] BuildPtr(string target, int ttl, uint timestamp = 0)
        {
            if (string.IsNullOrWhiteSpace(target))
                throw new ArgumentException("PTR target cannot be empty");
            byte[] data = EncodeCountName(target);
            return BuildHeader(TypePtr, data, ttl, timestamp);
        }

        public static byte[] BuildSrv(ushort priority, ushort weight, ushort port,
                                      string target, int ttl, uint timestamp = 0)
        {
            if (string.IsNullOrWhiteSpace(target))
                throw new ArgumentException("SRV target cannot be empty");
            byte[] name = EncodeCountName(target);
            byte[] data = new byte[6 + name.Length];
            Bin.WriteU16Be(data, 0, priority);
            Bin.WriteU16Be(data, 2, weight);
            Bin.WriteU16Be(data, 4, port);
            Buffer.BlockCopy(name, 0, data, 6, name.Length);
            return BuildHeader(TypeSrv, data, ttl, timestamp);
        }

        public static byte[] BuildMx(ushort preference, string exchange, int ttl, uint timestamp = 0)
        {
            if (string.IsNullOrWhiteSpace(exchange))
                throw new ArgumentException("MX exchange cannot be empty");
            byte[] name = EncodeCountName(exchange);
            byte[] data = new byte[2 + name.Length];
            Bin.WriteU16Be(data, 0, preference);
            Buffer.BlockCopy(name, 0, data, 2, name.Length);
            return BuildHeader(TypeMx, data, ttl, timestamp);
        }

        public static uint AgingTimestampNow()
        {
            // Hours since 1601-01-01 00:00:00 UTC (the AD "aging timestamp" base).
            // Matches the value a real DDNS update would write.
            DateTime epoch = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            double hours = (DateTime.UtcNow - epoch).TotalHours;
            return (uint)hours;
        }

        public static byte[] BuildTombstone()
        {
            // DNS_RPC_RECORD_TS per MS-DNSP: type=0, datalen=8, data = EntombedTime FILETIME LE
            long ft = DateTime.UtcNow.ToFileTimeUtc();
            byte[] data = BitConverter.GetBytes(ft);
            if (!BitConverter.IsLittleEndian) Array.Reverse(data);
            return BuildHeader(TypeZero, data, 0);
        }

        private static byte[] BuildHeader(ushort type, byte[] data, int ttl, uint timestamp = 0)
        {
            byte[] record = new byte[24 + data.Length];
            Bin.WriteU16Le(record, 0, (ushort)data.Length);   // DataLength
            Bin.WriteU16Le(record, 2, type);                  // Type
            record[4] = 0x05;                                 // Version
            record[5] = 0xF0;                                 // Rank = DNS_RANK_ZONE
            Bin.WriteU16Le(record, 6, 0);                     // Flags
            Bin.WriteU32Le(record, 8, 1);                     // Serial
            Bin.WriteU32Be(record, 12, (uint)ttl);            // TTL (big-endian)
            Bin.WriteU32Le(record, 16, 0);                    // Reserved
            Bin.WriteU32Le(record, 20, timestamp);            // Timestamp: 0=static, else hours-since-1601
            Buffer.BlockCopy(data, 0, record, 24, data.Length);
            return record;
        }

        // DNS_COUNT_NAME per MS-DNSP 2.2.2.2.2 (matches Powermad / krbrelayx)
        private static byte[] EncodeCountName(string name)
        {
            if (name.EndsWith("."))
                name = name.Substring(0, name.Length - 1);
            string[] labels = name.Split('.');

            using (MemoryStream ms = new MemoryStream())
            {
                foreach (string label in labels)
                {
                    byte[] lbl = Encoding.ASCII.GetBytes(label);
                    if (lbl.Length == 0)
                        throw new ArgumentException("Empty DNS label in: " + name);
                    if (lbl.Length > 63)
                        throw new ArgumentException("DNS label exceeds 63 bytes: " + label);
                    ms.WriteByte((byte)lbl.Length);
                    ms.Write(lbl, 0, lbl.Length);
                }
                ms.WriteByte(0);

                byte[] body = ms.ToArray();
                if (body.Length > 255)
                    throw new ArgumentException("Encoded DNS name exceeds 255 bytes: " + name);

                byte[] result = new byte[2 + body.Length];
                result[0] = (byte)body.Length;     // cchNameLength
                result[1] = (byte)labels.Length;   // bLabelCount
                Buffer.BlockCopy(body, 0, result, 2, body.Length);
                return result;
            }
        }

    }

    internal static class Bin
    {
        public static void WriteU16Le(byte[] b, int o, ushort v)
        {
            b[o]     = (byte)(v & 0xff);
            b[o + 1] = (byte)((v >> 8) & 0xff);
        }

        public static void WriteU16Be(byte[] b, int o, ushort v)
        {
            b[o]     = (byte)((v >> 8) & 0xff);
            b[o + 1] = (byte)(v & 0xff);
        }

        public static void WriteU32Le(byte[] b, int o, uint v)
        {
            b[o]     = (byte)(v & 0xff);
            b[o + 1] = (byte)((v >> 8) & 0xff);
            b[o + 2] = (byte)((v >> 16) & 0xff);
            b[o + 3] = (byte)((v >> 24) & 0xff);
        }

        public static void WriteU32Be(byte[] b, int o, uint v)
        {
            b[o]     = (byte)((v >> 24) & 0xff);
            b[o + 1] = (byte)((v >> 16) & 0xff);
            b[o + 2] = (byte)((v >> 8) & 0xff);
            b[o + 3] = (byte)(v & 0xff);
        }

        public static ushort ReadU16Le(byte[] b, int o)
        {
            return (ushort)(b[o] | (b[o + 1] << 8));
        }

        public static ushort ReadU16Be(byte[] b, int o)
        {
            return (ushort)((b[o] << 8) | b[o + 1]);
        }

        public static uint ReadU32Le(byte[] b, int o)
        {
            return (uint)(b[o]
                       | (b[o + 1] << 8)
                       | (b[o + 2] << 16)
                       | (b[o + 3] << 24));
        }

        public static uint ReadU32Be(byte[] b, int o)
        {
            return (uint)((b[o] << 24)
                       | (b[o + 1] << 16)
                       | (b[o + 2] << 8)
                       |  b[o + 3]);
        }

        public static ulong ReadU64Le(byte[] b, int o)
        {
            ulong lo = ReadU32Le(b, o);
            ulong hi = ReadU32Le(b, o + 4);
            return lo | (hi << 32);
        }
    }
}
