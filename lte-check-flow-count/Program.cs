using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace lte_check_flow_count;
class Program
{
    private static int restart_count = 5;
    private static string log_file = "/tmp/flow-restart";
    private static string ovpn_unit = "openvpn@client.service";

    static void Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddSystemdConsole();
        });
        ILogger logger = loggerFactory.CreateLogger("main");

        var flow_count = "ss -ntp".Bash().Split('\r', '\n').Where(a => (a.Contains("openvpn")) && (a.Contains("85.192.1.122"))).Count();
        var route_count = "ip route show default".Bash().Split('\r', '\n').Where(a => a.Contains("wwan")).Count();

        if (flow_count != route_count)
        {
            logger.LogWarning($"Flow count = {flow_count}\nRoute count = {route_count}");
            logger.LogWarning("Flow NOT equal routes!");
            File.WriteAllText(log_file, "1");
            $"systemctl -q restart {ovpn_unit}".Bash();
            Thread.Sleep(1000);
            if ($"systemctl is-active {ovpn_unit}".Bash().Trim() == "active")
                logger.LogInformation("Success restart openvpn client");
            else
                logger.LogError("FAILED restart openvpn client");
        }
        else
        {
            logger.LogInformation("Flow equal routes");
            if (!File.Exists(log_file))
                File.WriteAllText(log_file, "1");
            else
            {
                int current_count;
                try
                {
                    current_count = Convert.ToInt32(File.ReadAllText(log_file));
                }
                catch
                {
                    File.WriteAllText(log_file, "1");
                    current_count = Convert.ToInt32(File.ReadAllText(log_file));
                }
                if (current_count >= restart_count)
                {
                    File.WriteAllText(log_file, "1");
                    $"systemctl -q restart {ovpn_unit}".Bash();
                    Thread.Sleep(1000);
                    var responce = $"systemctl is-active {ovpn_unit}".Bash().Trim();
                    logger.LogInformation($"response #{responce}#");
                    if (responce == "active")
                        logger.LogInformation("Success restart openvpn client by counter");
                    else
                        logger.LogError("FAILED restart openvpn client by counter");
                }
                else
                {
                    current_count++;
                    File.WriteAllText(log_file, current_count.ToString());
                }
            }
        }
    }
}

public static class ShellHelper
{
    public static string Bash(this string cmd)
    {
        string escapedArgs = cmd.Replace("\"", "\\\"");

        Process process = new Process()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{escapedArgs} 2>&1\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        process.Start();
        string result = process.StandardOutput.ReadToEnd();
        process.WaitForExit(40000);

        return result;
    }
}