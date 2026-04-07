using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ABS_System.MachineIdHelper;

/// <summary>
/// Reads stable Windows identifiers (registry + WMI), hashes with SHA-256, prints 64-char hex (LAAS DEVICE_ID-friendly).
/// Run on each PC that should activate; paste the line into Activation → Blocked → Device ID (or store in LICENSE_ACTIVATION per your column mapping).
/// </summary>
internal static class Program
{
    private const string AlgorithmVersion = "ABS-MACHINE-ID-v1";

    internal static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This tool is for Windows only.");
            return 2;
        }

        var copyClipboard = args.Any(a =>
            string.Equals(a, "-c", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "--clipboard", StringComparison.OrdinalIgnoreCase));

        try
        {
            var material = BuildMachineMaterialString();
            var hashHex = Sha256HexUpper(material);

            Console.WriteLine("ABS Machine ID (SHA-256, 64 hex chars). Paste into Activation / Blocked → Device ID if your license uses this id.");
            Console.WriteLine();
            Console.WriteLine(hashHex);
            Console.WriteLine();

            if (copyClipboard)
            {
                TryCopyToClipboard(hashHex);
                Console.WriteLine("Copied to clipboard.");
            }
            else
            {
                Console.WriteLine("Tip: run with --clipboard to copy automatically.");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Failed: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Single string fed to SHA-256; version prefix allows changing ingredients later.</summary>
    internal static string BuildMachineMaterialString()
    {
        var sb = new StringBuilder();
        sb.Append(AlgorithmVersion).Append('|');

        sb.Append("MachineGuid=").Append(GetRegistryMachineGuid() ?? "").Append('|');

        sb.Append("WmiProductUuid=").Append(WmiScalar(
            "SELECT UUID FROM Win32_ComputerSystemProduct",
            "UUID") ?? "").Append('|');

        sb.Append("WmiBiosSerial=").Append(WmiScalar(
            "SELECT SerialNumber FROM Win32_BIOS",
            "SerialNumber") ?? "").Append('|');

        sb.Append("WmiBoardSerial=").Append(WmiScalar(
            "SELECT SerialNumber FROM Win32_BaseBoard",
            "SerialNumber") ?? "").Append('|');

        sb.Append("WmiCsName=").Append(WmiScalar(
            "SELECT Name FROM Win32_ComputerSystem",
            "Name") ?? "");

        return sb.ToString();
    }

    internal static string? GetRegistryMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
            return key?.GetValue("MachineGuid") as string;
        }
        catch
        {
            return null;
        }
    }

    internal static string? WmiScalar(string query, string propertyName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (var o in searcher.Get())
            {
                using (o)
                {
                    var v = o[propertyName]?.ToString();
                    if (!string.IsNullOrWhiteSpace(v))
                        return v.Trim();
                }
            }
        }
        catch
        {
            // WMI optional on locked-down systems
        }

        return null;
    }

    internal static string Sha256HexUpper(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static void TryCopyToClipboard(string text)
    {
        // Windows clip.exe: stdin -> clipboard
        using var p = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c clip",
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        p.Start();
        p.StandardInput.Write(text);
        p.StandardInput.Close();
        p.WaitForExit(5000);
    }
}
