using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CDriveCacheCleaner
{
    internal sealed class FileEntry
    {
        public string Path;
        public long Bytes;
        public string Decision;
        public CleanupRule Rule;
        public DateTime CutoffUtc;
        public DateTime ObservedWriteUtc;
        public uint Volume, IndexHigh, IndexLow;
        public bool Eligible { get { return Decision == "待清理"; } }
    }

    internal sealed class Outcome
    {
        public string Path;
        public long Bytes;
        public string Status;
        public string Reason;
    }

    internal sealed class CleanerEngine
    {
        private readonly SafetyPolicy policy;
        internal Action<FileEntry> BeforeDelete;
        internal CleanerEngine(SafetyPolicy policy) { this.policy = policy; }
        public CleanerEngine() : this(new SafetyPolicy()) { }

        public void Scan(IList<CleanupTarget> targets, Action<int, CleanupTarget> progress)
        { Scan(targets, progress, CancellationToken.None); }

        public void Scan(IList<CleanupTarget> targets, Action<int, CleanupTarget> progress, CancellationToken token)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                CleanupTarget target = targets[i];
                target.Entries.Clear(); target.SizeBytes = 0; target.CandidateCount = 0;
                target.ProtectedCount = 0; target.ScanErrors = 0;
                foreach (CleanupRule rule in target.Rules) ScanRule(target, rule, token);
                target.Status = target.ScanErrors > 0 ? "部分扫描" : (target.SizeBytes > 0 ? "可清理" : "无需清理");
                if (progress != null) progress(i + 1, target);
            }
        }

        private void Add(CleanupTarget target, CleanupRule rule, string path, long bytes,
            string decision, DateTime cutoff, DateTime write)
        {
            target.Entries.Add(new FileEntry { Path = path, Bytes = bytes, Decision = decision,
                Rule = rule, CutoffUtc = cutoff, ObservedWriteUtc = write });
            if (decision == "待清理") { target.SizeBytes += bytes; target.CandidateCount++; }
            else target.ProtectedCount++;
        }

        private void ScanRule(CleanupTarget target, CleanupRule rule, CancellationToken token)
        {
            DateTime cutoff = DateTime.UtcNow.AddHours(-Math.Max(1, rule.RetentionHours));
            if (!policy.IsApprovedRoot(rule.RootPath))
            {
                target.ScanErrors++;
                Add(target, rule, rule.RootPath, 0, "拒绝：不在白名单", cutoff, DateTime.MinValue); return;
            }
            if (!Directory.Exists(rule.RootPath)) return;
            if (!policy.HasSafeChain(rule.RootPath))
            {
                target.ScanErrors++;
                Add(target, rule, rule.RootPath, 0, "保留：路径包含链接或不可访问", cutoff, DateTime.MinValue); return;
            }
            Stack<string> dirs = new Stack<string>(); dirs.Push(rule.RootPath);
            while (dirs.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string directory = dirs.Pop();
                if (!policy.HasSafeChain(directory))
                { target.ScanErrors++; Add(target, rule, directory, 0, "保留：路径发生变化", cutoff, DateTime.MinValue); continue; }
                try
                {
                    DirectoryInfo info = new DirectoryInfo(directory);
                    foreach (FileInfo file in info.EnumerateFiles(rule.Mode == CleanupMode.MatchingFiles ? rule.Pattern : "*"))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            string decision = policy.IsReparsePoint(file.FullName) ? "保留：链接文件" :
                                (rule.Mode == CleanupMode.OlderFiles && file.LastWriteTimeUtc >= cutoff ? "保留：近期文件" : "待清理");
                            Add(target, rule, file.FullName, file.Length, decision, cutoff, file.LastWriteTimeUtc);
                            if (decision == "待清理")
                            {
                                FileEntry entry = target.Entries[target.Entries.Count - 1];
                                Native.Information observed;
                                using (SafeFileHandle handle = Native.CreateFile(file.FullName, 0x80, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
                                {
                                    if (handle.IsInvalid || !Native.GetFileInformationByHandle(handle, out observed))
                                    {
                                        entry.Decision = "保留：无法验证文件身份";
                                        target.SizeBytes -= entry.Bytes; target.CandidateCount--; target.ProtectedCount++; target.ScanErrors++;
                                        continue;
                                    }
                                    entry.Volume = observed.Volume; entry.IndexHigh = observed.IndexHigh; entry.IndexLow = observed.IndexLow;
                                    entry.ObservedWriteUtc = DateTime.FromFileTimeUtc(((long)observed.WriteHigh << 32) | observed.WriteLow);
                                }
                            }
                        }
                        catch (IOException) { target.ScanErrors++; }
                        catch (UnauthorizedAccessException) { target.ScanErrors++; }
                    }
                    if (rule.Mode == CleanupMode.MatchingFiles) continue;
                    foreach (DirectoryInfo child in info.EnumerateDirectories())
                    {
                        token.ThrowIfCancellationRequested();
                        bool excluded = String.Equals(SafetyPolicy.Normalize(directory), SafetyPolicy.Normalize(rule.RootPath), StringComparison.OrdinalIgnoreCase)
                            && String.Equals(child.Name, rule.ExcludedTopLevelName, StringComparison.OrdinalIgnoreCase);
                        if (excluded || policy.IsReparsePoint(child.FullName))
                        { Add(target, rule, child.FullName, 0, excluded ? "保留：独立分类目录" : "保留：链接目录", cutoff, DateTime.MinValue); continue; }
                        dirs.Push(child.FullName);
                    }
                }
                catch (IOException) { target.ScanErrors++; Add(target, rule, directory, 0, "保留：读取失败", cutoff, DateTime.MinValue); }
                catch (UnauthorizedAccessException) { target.ScanErrors++; Add(target, rule, directory, 0, "保留：无访问权限", cutoff, DateTime.MinValue); }
            }
        }

        public CleanResult Clean(IList<CleanupTarget> targets, Action<string> progress)
        { return Clean(targets, progress, CancellationToken.None); }

        public CleanResult Clean(IList<CleanupTarget> targets, Action<string> progress, CancellationToken token)
        {
            CleanResult result = new CleanResult(); result.FreeBefore = Free();
            result.EstimatedBytes = targets.Sum(t => t.SizeBytes);
            try
            {
                foreach (CleanupTarget target in targets)
                {
                    if (token.IsCancellationRequested) { result.Cancelled = true; break; }
                    if (progress != null) progress("正在处理：" + target.Name);
                    foreach (FileEntry entry in target.Entries.Where(e => e.Eligible))
                    {
                        if (token.IsCancellationRequested) { result.Cancelled = true; break; }
                        if (BeforeDelete != null) BeforeDelete(entry);
                        Outcome outcome = Delete(entry); result.Outcomes.Add(outcome);
                        if (outcome.Status == "已删除") { result.DeletedFiles++; result.DeletedBytes += outcome.Bytes; }
                        else result.SkippedItems++;
                    }
                }
            }
            catch (Exception ex) { result.ErrorMessage = ex.Message; AppLog.Write("清理异常：" + ex); }
            result.FreeAfter = Free();
            AppLog.Write(result.SummaryStatus + "，删除 " + result.DeletedFiles + " 个文件，跳过 " + result.SkippedItems);
            return result;
        }

        private Outcome Delete(FileEntry entry)
        {
            Outcome outcome = new Outcome { Path = entry.Path, Bytes = 0, Status = "已跳过", Reason = "" };
            if (!policy.IsSafeDescendant(entry.Rule.RootPath, entry.Path) || !policy.HasSafeChain(entry.Path))
            { outcome.Reason = "路径不再符合规则或包含链接"; return outcome; }
            using (SafeFileHandle handle = Native.CreateFile(entry.Path, 0x10000 | 0x80, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
            {
                if (handle.IsInvalid) { outcome.Reason = Native.Reason(Marshal.GetLastWin32Error()); return outcome; }
                Native.Information info;
                if (!Native.GetFileInformationByHandle(handle, out info))
                { outcome.Reason = "无法读取文件信息"; return outcome; }
                if ((info.Attributes & (0x400 | 0x10 | 0x1)) != 0 || info.Links > 1)
                { outcome.Reason = "链接、目录、只读或多重硬链接文件"; return outcome; }
                if (info.Volume != entry.Volume || info.IndexHigh != entry.IndexHigh || info.IndexLow != entry.IndexLow)
                { outcome.Reason = "文件在扫描后被替换，已保留"; return outcome; }
                StringBuilder physical = new StringBuilder(32768);
                uint count = Native.GetFinalPathNameByHandle(handle, physical, (uint)physical.Capacity, 0);
                if (count == 0 || count >= physical.Capacity)
                { outcome.Reason = "无法验证实际路径"; return outcome; }
                string final = physical.ToString();
                if (final.StartsWith(@"\\?\")) final = final.Substring(4);
                if (!String.Equals(SafetyPolicy.Normalize(final), SafetyPolicy.Normalize(entry.Path), StringComparison.OrdinalIgnoreCase)
                    || !policy.HasSafeChain(entry.Path))
                { outcome.Reason = "实际路径与扫描路径不一致"; return outcome; }
                DateTime currentWrite = DateTime.FromFileTimeUtc(((long)info.WriteHigh << 32) | info.WriteLow);
                if (currentWrite != entry.ObservedWriteUtc ||
                    (entry.Rule.Mode == CleanupMode.OlderFiles && currentWrite >= entry.CutoffUtc))
                { outcome.Reason = "文件在扫描后更新，已保留"; return outcome; }
                // FILE_DISPOSITION_INFO deletes this validated handle, not a path looked up again.
                Native.Disposition disposition = new Native.Disposition { Delete = true };
                if (!Native.SetFileInformationByHandle(handle, 4, ref disposition, 1))
                { outcome.Reason = Native.Reason(Marshal.GetLastWin32Error()); return outcome; }
                outcome.Bytes = ((long)info.SizeHigh << 32) | info.SizeLow;
                outcome.Status = "已删除";
                return outcome;
            }
        }

        private static long Free() { try { return new DriveInfo("C").AvailableFreeSpace; } catch { return 0; } }

        internal static bool RunSelfTest(string outputPath) { return SafetyTests.Run(outputPath); }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Information
        {
            public uint Attributes, CreateLow, CreateHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct Disposition
        { [MarshalAs(UnmanagedType.U1)] public bool Delete; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out Information info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
        internal static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint count, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref Disposition info, uint size);
        internal static string Reason(int code)
        { return code == 5 ? "无删除权限或只读" : (code == 32 || code == 33 ? "文件正在占用" : "Windows 错误 " + code); }
    }

    internal static class Report
    {
        internal static string Csv(string value) { return "\"" + (value ?? "").Replace("\"", "\"\"") + "\""; }
        internal static string Redact(string path)
        {
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.StartsWith(user + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? "%USERPROFILE%" + path.Substring(user.Length) : path;
        }
        internal static void Export(CleanResult result, string path)
        {
            StringBuilder s = new StringBuilder();
            s.AppendLine("指标,数值"); s.AppendLine("软件版本,1.1.0");
            s.AppendLine("结果," + result.SummaryStatus);
            s.AppendLine("预计字节," + result.EstimatedBytes); s.AppendLine("已删除文件字节," + result.DeletedBytes);
            s.AppendLine("C盘可用空间变化字节," + (result.FreeAfter - result.FreeBefore));
            s.AppendLine("失败原因," + Csv(result.ErrorMessage));
            s.AppendLine(); s.AppendLine("路径,状态,字节,原因");
            foreach (Outcome item in result.Outcomes) s.AppendLine(Csv(Redact(item.Path)) + "," + Csv(item.Status) + "," + item.Bytes + "," + Csv(item.Reason));
            File.WriteAllText(path, s.ToString(), new UTF8Encoding(true));
        }
    }
}
