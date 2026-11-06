using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace TuneForge.Engine.Core;

public interface IProcessRunner
{
    /// <summary>Runs a fixed Windows tool and returns its exit code, or -1 if it couldn't finish.</summary>
    int Run(string tool, IReadOnlyList<string> arguments, TimeSpan timeLimit);
}

public sealed record ToolOutput(int ExitCode, string Text);

public interface IToolRunner
{
    /// <summary>Runs a program by full path and keeps its output. Exit code -1 means it couldn't finish.</summary>
    ToolOutput Capture(string path, IReadOnlyList<string> arguments, TimeSpan timeLimit);
}

/// <summary>Arguments are never joined into a shell command.</summary>
public sealed class WindowsProcessRunner : IProcessRunner, IToolRunner
{
    /// <summary>Runs a tool from System32.</summary>
    public int Run(string tool, IReadOnlyList<string> arguments, TimeSpan timeLimit) =>
        Capture(Path.Combine(Environment.SystemDirectory, tool), arguments, timeLimit).ExitCode;

    public ToolOutput Capture(string path, IReadOnlyList<string> arguments, TimeSpan timeLimit)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
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
                return new ToolOutput(-1, string.Empty);
            }

            // Reading both streams stops a chatty tool from filling the pipe and stalling.
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeLimit))
            {
                process.Kill(entireProcessTree: true);
                return new ToolOutput(-1, string.Empty);
            }

            // An installer left running by a tool can hold the pipe open, so don't wait on it for long.
            output.Wait(TimeSpan.FromSeconds(5));
            return new ToolOutput(process.ExitCode, output.IsCompletedSuccessfully ? output.Result : string.Empty);
        }
        catch (Win32Exception)
        {
            return new ToolOutput(-1, string.Empty);
        }
    }
}
