using System;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class PayloadVerifier
{
    private static string root;
    private static int next = -1, completed, failed;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(IntPtr handle, StringBuilder path, uint capacity, uint flags);

    private static string ExtendedPath(string path)
    {
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
    }

    private static void CheckPath(string path, bool directory)
    {
        FileAttributes attributes = File.GetAttributes(ExtendedPath(path));
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            ((attributes & FileAttributes.Directory) != 0) != directory)
            throw new IOException("Unexpected linked or nonregular payload path.");
    }

    private static void CheckAncestors(string path)
    {
        for (string parent = Path.GetDirectoryName(path); !String.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            CheckPath(parent, true);
    }

    private static void CheckOpenedPath(FileStream stream, string expected, StringBuilder buffer)
    {
        buffer.Length = 0;
        uint length = GetFinalPathNameByHandle(stream.SafeFileHandle.DangerousGetHandle(), buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("Cannot bind opened payload path.");
        string actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual.Substring(8);
        else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual.Substring(4);
        if (!String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Opened payload path differs.");
        CheckAncestors(expected);
        CheckPath(expected, false);
    }

    private static void VerifyFiles()
    {
        try
        {
            StringBuilder pathBuffer = new StringBuilder(32768);
            while (Volatile.Read(ref failed) == 0)
            {
                int index = Interlocked.Increment(ref next);
                if (index >= PayloadManifest.Paths.Length) return;
                string path = Path.Combine(root, PayloadManifest.Paths[index].Replace('/', '\\'));
                if (!Path.GetFullPath(ExtendedPath(path)).StartsWith(ExtendedPath(root) + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Payload path escaped its root.");
                CheckAncestors(path);
                CheckPath(path, false);
                using (SHA256 algorithm = SHA256.Create())
                using (FileStream stream = new FileStream(ExtendedPath(path), FileMode.Open, FileAccess.Read, FileShare.Read,
                    1048576, FileOptions.SequentialScan))
                {
                    CheckOpenedPath(stream, path, pathBuffer);
                    string actual = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    if (!String.Equals(actual, PayloadManifest.Digests[index], StringComparison.Ordinal))
                        throw new IOException("Payload digest differs.");
                    CheckOpenedPath(stream, path, pathBuffer);
                }
                Interlocked.Increment(ref completed);
            }
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref failed, 1);
        }
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1 || String.IsNullOrEmpty(args[0]) || !Path.IsPathRooted(args[0]) ||
                args[0].EndsWith("\\", StringComparison.Ordinal) || args[0].IndexOf('/') >= 0 ||
                args[0].StartsWith(@"\\?\", StringComparison.Ordinal) || args[0].StartsWith(@"\\.\", StringComparison.Ordinal) ||
                !String.Equals(Path.GetFullPath(ExtendedPath(args[0])), ExtendedPath(args[0]), StringComparison.OrdinalIgnoreCase) ||
                PayloadManifest.Paths.Length == 0 || PayloadManifest.Paths.Length != PayloadManifest.Digests.Length)
                return 23;
            root = args[0];
            foreach (string part in root.Split('\\'))
                if (part.EndsWith(" ", StringComparison.Ordinal) || part.EndsWith(".", StringComparison.Ordinal)) return 23;
            CheckAncestors(root);
            CheckPath(root, true);
            Thread[] workers = new Thread[Math.Min(4, PayloadManifest.Paths.Length)];
            int started = 0;
            try
            {
                for (int index = 0; index < workers.Length; index++)
                {
                    workers[index] = new Thread(VerifyFiles);
                    workers[index].Start();
                    started++;
                }
            }
            catch (Exception)
            {
                Interlocked.Exchange(ref failed, 1);
            }
            finally
            {
                for (int index = 0; index < started; index++) workers[index].Join();
            }
            return failed == 0 && completed == PayloadManifest.Paths.Length ? 0 : 23;
        }
        catch (Exception)
        {
            return 23;
        }
    }
}
