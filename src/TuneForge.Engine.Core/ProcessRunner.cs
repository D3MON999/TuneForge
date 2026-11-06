using System.ComponentModel;
using System.Diagnostics;

namespace TuneForge.Engine.Core;

public interface IProcessRunner
{
    /// <summary>Runs a fixed Windows tool and returns its exit code, or -1 if it couldn't finish.</summary>
    int Run(string tool, IReadOnlyList<string> arguments, TimeSpan timeLimit);
}

/// <summary>Runs tools from System32 by full path. Arguments are never joined into a shell command.</summary>
public sealed class WindowsProcessRunner : IProcessRunner
{
    public int Run(string tool, IReadOnlyList<string> arguments, TimeSpan timeLimit)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, tool))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return -1;
            }

            // Drain output so a chatty tool can't fill the pipe and stall.
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (process.WaitForExit(timeLimit))
            {
                return process.ExitCode;
            }

            process.Kill(entireProcessTree: true);
            return -1;
        }
        catch (Win32Exception)
        {
            return -1;
        }
    }
}
