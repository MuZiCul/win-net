using System.Diagnostics;
using System.Text;

namespace WinNetFix;

/// <summary>隐藏窗口执行外部命令的封装。</summary>
public sealed record RunResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

public static class ProcessRunner
{
    /// <summary>执行命令，隐藏窗口，捕获输出（UTF-8）。超时返回 -1 退出码。</summary>
    public static RunResult Run(string fileName, string? args = null, int timeoutMs = 30000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var p = Process.Start(psi);
            if (p == null) return new RunResult(-1, "", "启动进程失败");

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new RunResult(-1, "", $"超时({timeoutMs}ms)");
            }

            Task.WaitAll(outTask, errTask);
            return new RunResult(p.ExitCode, outTask.Result.Trim(), errTask.Result.Trim());
        }
        catch (Exception ex)
        {
            return new RunResult(-1, "", ex.Message);
        }
    }

    /// <summary>执行 PowerShell 脚本。使用 -EncodedCommand 避免引号与中文编码问题。</summary>
    public static RunResult RunPowershell(string script, int timeoutMs = 30000)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", timeoutMs);
    }

    /// <summary>
    /// 执行 netsh 命令。通过 cmd + chcp 65001 强制 netsh 以 UTF-8/英文标签输出，
    /// 避免中文系统下 netsh 输出本地化中文标签导致解析失败。
    /// </summary>
    public static RunResult RunNetsh(string args, int timeoutMs = 30000)
        => Run("cmd.exe", $"/c chcp 65001 >nul & netsh {args}", timeoutMs);
}
