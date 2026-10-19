using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace Receiver.Diagnostics.Probes;

internal static class HostProbe
{
    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void Collect(CapabilityReport r)
    {
        r.Host["osDescription"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        r.Host["osVersion"] = Environment.OSVersion.Version.ToString();
        r.Host["architecture"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
        r.Host["dotnet"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        r.Host["utcNow"] = DateTimeOffset.UtcNow.ToString("O");
    }

    /// <summary>
    /// Runs a console tool and captures its output verbatim as an evidence file.
    /// The captured text is never parsed for capability decisions on this machine,
    /// because the shell output here is localized (de-DE); it exists so a human can
    /// check the machine-readable findings against what Windows itself printed.
    /// </summary>
    public static string? CaptureCommand(string exe, string args, string evidenceDir, string evidenceId)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);

            var sb = new StringBuilder();
            sb.AppendLine($"# command: {exe} {args}");
            sb.AppendLine($"# exitCode: {p.ExitCode}");
            sb.AppendLine($"# capturedUtc: {DateTimeOffset.UtcNow:O}");
            sb.AppendLine();
            sb.AppendLine(stdout);
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                sb.AppendLine("# --- stderr ---");
                sb.AppendLine(stderr);
            }

            var path = Path.Combine(evidenceDir, evidenceId + ".txt");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return evidenceId + ".txt";
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(evidenceDir, evidenceId + ".txt"),
                $"# command: {exe} {args}\n# FAILED: {ex.Message}\n");
            return evidenceId + ".txt";
        }
    }
}
