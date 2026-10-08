using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace CDriveCacheCleaner
{
    internal static class SafetyTests
    {
        private static CleanupTarget Target(string root, CleanupMode mode)
        {
            CleanupTarget t = new CleanupTarget { Name = "Test", Selected = true };
            t.Rules.Add(new CleanupRule(root, mode) { RetentionHours = 24 }); return t;
        }
        private static void Check(Dictionary<string, bool> checks, string name, bool condition)
        { checks[name] = condition; }
        private static string Old(string root, string name)
        {
            string path = Path.Combine(root, name); File.WriteAllText(path, "test");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3)); return path;
        }
        internal static bool Run(string outputPath)
        {
            string root = Path.Combine(Path.GetTempPath(), "CDriveCacheCleanerSelfTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Dictionary<string, bool> checks = new Dictionary<string, bool>();
            string error = "";
            List<string> junctions = new List<string>();
            try
            {
                SafetyPolicy policy = new SafetyPolicy(root);
                CleanerEngine engine = new CleanerEngine(policy);
                string old = Old(root, "old.tmp"); string recent = Path.Combine(root, "recent.tmp"); File.WriteAllText(recent, "recent");
                string excluded = Path.Combine(root, "keep"); Directory.CreateDirectory(excluded); Old(excluded, "excluded.tmp");
                CleanupTarget t = Target(root, CleanupMode.OlderFiles); t.Rules[0].ExcludedTopLevelName = "keep";
                engine.Scan(new[] { t }, null); CleanResult result = engine.Clean(new[] { t }, null);
                Check(checks, "old_deleted", !File.Exists(old) && result.DeletedFiles == 1 && result.DeletedBytes == 4);
                Check(checks, "recent_kept", File.Exists(recent));
                Check(checks, "excluded_kept", File.Exists(Path.Combine(excluded, "excluded.tmp")));
                Check(checks, "root_rejected", !policy.IsApprovedRoot(Path.GetPathRoot(root)));
                Check(checks, "sibling_rejected", !policy.IsSafeDescendant(root, root + "-sibling\\file.tmp"));
                Check(checks, "diagnostics_unselected", !CleanerCatalog.Build(24).First(x => x.Id == "crash").Selected);
                string originalTemp = Environment.GetEnvironmentVariable("TEMP");
                try
                {
                    Environment.SetEnvironmentVariable("TEMP", excluded);
                    Check(checks, "temp_override_not_trusted", CleanerCatalog.Build(24).First(x => x.Id == "user-temp").Rules[0].RootPath ==
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"));
                }
                finally { Environment.SetEnvironmentVariable("TEMP", originalTemp); }

                string updated = Old(root, "updated.tmp"); t = Target(root, CleanupMode.OlderFiles); t.Rules[0].ExcludedTopLevelName = "keep";
                engine.Scan(new[] { t }, null);
                File.SetLastWriteTimeUtc(updated, DateTime.UtcNow);
                result = engine.Clean(new[] { t }, null);
                Check(checks, "updated_after_scan_kept", File.Exists(updated) && result.SkippedItems == 1 && result.SummaryStatus == "部分完成");

                t = Target(root, CleanupMode.OlderFiles); t.Rules[0].ExcludedTopLevelName = "keep"; engine.Scan(new[] { t }, null);
                string added = Old(root, "added-after-scan.tmp"); engine.Clean(new[] { t }, null);
                Check(checks, "new_file_not_deleted", File.Exists(added));

                string replaced = Old(root, "replaced.tmp");
                t = Target(root, CleanupMode.OlderFiles); t.Rules[0].ExcludedTopLevelName = "keep"; engine.Scan(new[] { t }, null);
                DateTime originalTime = File.GetLastWriteTimeUtc(replaced);
                File.Move(replaced, Path.Combine(excluded, "original-replaced.tmp"));
                File.WriteAllText(replaced, "new!"); File.SetLastWriteTimeUtc(replaced, originalTime);
                result = engine.Clean(new[] { t }, null);
                Check(checks, "replaced_file_same_timestamp_kept", File.Exists(replaced) && result.Outcomes.Any(x => x.Path == replaced && x.Reason.Contains("被替换")));

                string movedRoot = Path.Combine(root, "swapped"); Directory.CreateDirectory(movedRoot); Old(movedRoot, "candidate.tmp");
                CleanupTarget swapped = Target(movedRoot, CleanupMode.AllFiles);
                CleanerEngine swappedEngine = new CleanerEngine(new SafetyPolicy(movedRoot)); swappedEngine.Scan(new[] { swapped }, null);
                string movedAside = Path.Combine(excluded, "swapped-original"); Directory.Move(movedRoot, movedAside);
                ProcessStartInfo swapPsi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + movedRoot + "\" \"" + movedAside + "\"");
                swapPsi.UseShellExecute = false; swapPsi.CreateNoWindow = true;
                using (Process p = Process.Start(swapPsi)) { p.WaitForExit(); if (p.ExitCode != 0) throw new IOException("Cannot create swapped fixture"); }
                junctions.Add(movedRoot);
                result = swappedEngine.Clean(new[] { swapped }, null);
                Check(checks, "root_swapped_after_scan_kept", File.Exists(Path.Combine(movedAside, "candidate.tmp")) && result.SkippedItems == 1);

                string locked = Old(root, "locked.tmp"); t = Target(root, CleanupMode.OlderFiles); t.Rules[0].ExcludedTopLevelName = "keep";
                engine.Scan(new[] { t }, null);
                using (FileStream stream = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    result = engine.Clean(new[] { t }, null);
                    Check(checks, "locked_kept", File.Exists(locked) && result.Outcomes.Any(x => x.Path == locked && x.Status == "已跳过"));
                }
                string readOnly = Old(root, "readonly.tmp"); File.SetAttributes(readOnly, FileAttributes.ReadOnly);
                t = Target(root, CleanupMode.OlderFiles); t.Rules[0].ExcludedTopLevelName = "keep"; engine.Scan(new[] { t }, null);
                result = engine.Clean(new[] { t }, null);
                Check(checks, "readonly_kept", File.Exists(readOnly)); File.SetAttributes(readOnly, FileAttributes.Normal);

                string denied = Old(root, "denied.tmp");
                SecurityIdentifier sid = WindowsIdentity.GetCurrent().User;
                FileSecurity savedAcl = File.GetAccessControl(denied);
                DirectorySecurity savedParentAcl = Directory.GetAccessControl(root);
                DirectorySecurity denyParentAcl = Directory.GetAccessControl(root);
                denyParentAcl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny));
                Directory.SetAccessControl(root, denyParentAcl);
                FileSecurity denyAcl = File.GetAccessControl(denied);
                denyAcl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Delete, AccessControlType.Deny));
                File.SetAccessControl(denied, denyAcl);
                try
                {
                    t = Target(root, CleanupMode.OlderFiles); t.Rules[0].ExcludedTopLevelName = "keep"; engine.Scan(new[] { t }, null);
                    result = engine.Clean(new[] { t }, null);
                    Check(checks, "delete_permission_denied_kept", File.Exists(denied) && result.Outcomes.Any(x => x.Path == denied && x.Status == "已跳过"));
                }
                finally { Directory.SetAccessControl(root, savedParentAcl); if (File.Exists(denied)) File.SetAccessControl(denied, savedAcl); }

                string cancelled = Old(root, "cancelled.tmp"); CancellationTokenSource cts = new CancellationTokenSource();
                t = Target(root, CleanupMode.OlderFiles); t.Rules[0].ExcludedTopLevelName = "keep"; engine.Scan(new[] { t }, null);
                cts.Cancel(); result = engine.Clean(new[] { t }, null, cts.Token);
                Check(checks, "cancelled_kept", result.Cancelled && File.Exists(cancelled) && result.DeletedFiles == 0);
                bool threw = false; try { engine.Scan(new[] { t }, null, cts.Token); } catch (OperationCanceledException) { threw = true; }
                Check(checks, "scan_cancellation", threw);

                string partialDir = Path.Combine(root, "partial"); Directory.CreateDirectory(partialDir);
                Old(partialDir, "one.tmp"); Old(partialDir, "two.tmp");
                CleanerEngine partialEngine = new CleanerEngine(new SafetyPolicy(partialDir));
                CleanupTarget partialTarget = Target(partialDir, CleanupMode.AllFiles); partialEngine.Scan(new[] { partialTarget }, null);
                using (CancellationTokenSource partialCts = new CancellationTokenSource())
                {
                    partialEngine.BeforeDelete = delegate { partialCts.Cancel(); };
                    CleanResult partialResult = partialEngine.Clean(new[] { partialTarget }, null, partialCts.Token);
                    Check(checks, "mid_cleanup_stops_remaining_files", partialResult.Cancelled && partialResult.DeletedFiles == 1 && Directory.GetFiles(partialDir).Length == 1);
                }

                engine.BeforeDelete = delegate { throw new IOException("simulated failure"); };
                result = engine.Clean(new[] { t }, null);
                Check(checks, "fatal_error_not_success", result.SummaryStatus == "失败" && result.ErrorMessage == "simulated failure");
                engine.BeforeDelete = null;

                // Directory junctions require no symlink privilege. All targets remain inside this sandbox.
                string outside = Path.Combine(root, "keep"); string link = Path.Combine(root, "junction");
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + outside + "\"");
                psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); if (p.ExitCode != 0) throw new IOException("Cannot create junction test fixture"); }
                junctions.Add(link);
                Check(checks, "parent_junction_rejected", !policy.HasSafeChain(Path.Combine(link, "excluded.tmp")));
                Check(checks, "root_junction_rejected", !policy.HasSafeChain(link));
                t = Target(root, CleanupMode.OlderFiles); t.Rules[0].ExcludedTopLevelName = "keep"; engine.Scan(new[] { t }, null); engine.Clean(new[] { t }, null);
                Check(checks, "junction_target_kept", File.Exists(Path.Combine(outside, "excluded.tmp")));
                CleanupTarget linkedRoot = Target(link, CleanupMode.AllFiles);
                CleanerEngine linkedEngine = new CleanerEngine(new SafetyPolicy(link)); linkedEngine.Scan(new[] { linkedRoot }, null);
                Check(checks, "whitelisted_junction_root_blocked", linkedRoot.CandidateCount == 0 && linkedRoot.ScanErrors > 0);

                string matchDir = Path.Combine(root, "matches"); Directory.CreateDirectory(matchDir);
                string match = Old(matchDir, "thumbcache_1.db"); string unmatched = Old(matchDir, "unrelated.db");
                CleanerEngine matchEngine = new CleanerEngine(new SafetyPolicy(matchDir)); t = Target(matchDir, CleanupMode.MatchingFiles); t.Rules[0].Pattern = "thumbcache_*";
                matchEngine.Scan(new[] { t }, null); matchEngine.Clean(new[] { t }, null);
                Check(checks, "matching_pattern_only", !File.Exists(match) && File.Exists(unmatched));
                result = new CleanResult { EstimatedBytes = 10, DeletedBytes = 4, FreeBefore = 100, FreeAfter = 98 };
                string report = Path.Combine(root, "report.csv"); Report.Export(result, report);
                Check(checks, "report_distinguishes_space_change", File.ReadAllText(report).Contains("C盘可用空间变化字节,-2"));
            }
            catch (Exception ex) { error = ex.ToString(); }
            finally
            {
                foreach (string link in junctions) try { Directory.Delete(link, false); } catch { }
                // Refuse recursive fixture removal unless it is a unique, validated temporary child.
                string temp = SafetyPolicy.Normalize(Path.GetTempPath());
                if (root.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("CDriveCacheCleanerSelfTest-"))
                    try { Directory.Delete(root, true); } catch { }
            }
            bool passed = error == "" && checks.Count >= 18 && checks.Values.All(x => x);
            string json = "{\n  \"passed\": " + passed.ToString().ToLowerInvariant() + ",\n  \"error\": \"" + FormatUtil.Json(error) + "\",\n  \"checks\": {\n" +
                String.Join(",\n", checks.Select(x => "    \"" + x.Key + "\": " + x.Value.ToString().ToLowerInvariant()).ToArray()) + "\n  }\n}";
            File.WriteAllText(outputPath, json, new UTF8Encoding(false)); return passed;
        }
    }
}
