using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace YourApp.Services;

/// <summary>
/// SHA-256 machine fingerprint aligned with <c>activation_app/licensing_sdk/fingerprint.py</c>
/// (normalize parts, join with |, UTF-8, lowercase hex digest). Used so ABS_System matches
/// LICENSE_ACTIVATION.MACHINE_FINGERPRINT after LAAS/API activation.
/// </summary>
public static class MachineFingerprint
{
    private static readonly Regex NonAlphanumeric = new(@"[^A-Z0-9]", RegexOptions.Compiled);

    public static string NormalizePart(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        s = s.Trim().ToUpperInvariant();
        return NonAlphanumeric.Replace(s, "");
    }

    /// <summary>64-character lowercase hex SHA-256.</summary>
    public static string Compute()
    {
        var parts = new List<string>();

        parts.Add(HexUuidGetNode());

        if (OperatingSystem.IsWindows())
        {
            parts.Add(NormalizePart(RunProcess("wmic", "csproduct get UUID", 8000)));
            parts.Add(NormalizePart(RunProcess("wmic", "baseboard get SerialNumber", 8000)));
            parts.Add(NormalizePart(RunProcess("wmic", "diskdrive get SerialNumber", 8000)));
            parts.Add(NormalizePart(RunProcess(
                "wmic",
                "path win32_NetworkAdapter where NetConnectionStatus=2 get MACAddress",
                8000)));
        }
        else if (OperatingSystem.IsLinux())
        {
            TryAddFilePart(parts, "/etc/machine-id");
            TryAddFilePart(parts, "/sys/class/dmi/id/product_uuid");
        }
        else if (OperatingSystem.IsMacOS())
        {
            var io = NormalizePart(File.Exists("/usr/sbin/ioreg")
                ? RunProcess("/usr/sbin/ioreg", "-rd1 -c IOPlatformExpertDevice", 8000)
                : RunProcess("ioreg", "-rd1 -c IOPlatformExpertDevice", 8000));
            if (!string.IsNullOrEmpty(io))
                parts.Add(io);
        }

        parts.Add(NormalizePart(GetPlatformNodeName()));

        var chunks = parts.Where(static p => !string.IsNullOrEmpty(p)).ToList();
        var raw = chunks.Count > 0
            ? string.Join("|", chunks)
            : (GetPlatformNodeName() ?? "unknown-host");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Mirrors Python <c>hex(uuid.getnode())</c> using the first 6-byte physical address found.</summary>
    private static string HexUuidGetNode()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().OrderBy(static n => n.Id, StringComparer.Ordinal))
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            var bytes = ni.GetPhysicalAddress()?.GetAddressBytes();
            if (bytes is not { Length: 6 })
                continue;

            ulong v = 0;
            for (var i = 0; i < 6; i++)
                v = (v << 8) | bytes[i];

            return "0x" + v.ToString("x", CultureInfo.InvariantCulture);
        }

        return "0x0";
    }

    /// <summary>Python <c>platform.node()</c> uses <c>socket.gethostname()</c> on Windows.</summary>
    private static string? GetPlatformNodeName()
    {
        try
        {
            return Dns.GetHostName();
        }
        catch
        {
            return Environment.MachineName;
        }
    }

    private static void TryAddFilePart(List<string> parts, string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            var text = File.ReadAllText(path, Encoding.UTF8);
            var n = NormalizePart(text);
            if (!string.IsNullOrEmpty(n))
                parts.Add(n);
        }
        catch
        {
            // ignore
        }
    }

    private static string RunProcess(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null)
                return "";
            if (!p.WaitForExit(timeoutMs))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }

                return "";
            }

            return p.StandardOutput.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }
}
