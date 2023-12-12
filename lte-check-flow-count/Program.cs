using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace lte_check_flow_count;
class Program
{
    private static string _srv;
    private const int _restartCount = 10;
    private const string _logFile = "/tmp/flow-restart";
    private const string _ovpnUnit = "openvpn@client.service";
    private const string _ovpnUnit2 = "openvpn-client@client.service";

    static void Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddSystemdConsole();
        });
        ILogger logger = loggerFactory.CreateLogger("main");


        if(File.Exists("/etc/openvpn/client.conf"))
            _srv = "cat /etc/openvpn/client.conf | grep \"remote \" | awk '{{print $2}}'".Bash().Trim();
        if(File.Exists("/etc/openvpn/client/client.conf"))
            _srv = "cat /etc/openvpn/client/client.conf | grep \"remote \" | awk '{{print $2}}'".Bash().Trim();
        var flowCount = $"ss -Hntp state established dst {_srv}".Bash().Trim().Split('\r', '\n').Length;
        var routeCount = "ip route show default".Bash().Split('\r', '\n').Count(a => a.Contains("wwan"));

        if (flowCount != routeCount)
        {
            logger.LogWarning($"Flow count = {flowCount}\nMultipath count = {routeCount}");
            logger.LogWarning("Flow NOT equal routes!");
            File.WriteAllText(_logFile, "1");
            $"systemctl -q restart {_ovpnUnit}".Bash();
            $"systemctl -q restart {_ovpnUnit2}".Bash();
            Thread.Sleep(1000);
            if ($"systemctl is-active {_ovpnUnit}".Bash().Trim() == "active" || $"systemctl is-active {_ovpnUnit2}".Bash().Trim() == "active")
                logger.LogInformation("Success restart openvpn client");
            else
                logger.LogError("FAILED restart openvpn client");
        }
        else
        {
            logger.LogInformation($"Flow equal routes. Count: {flowCount}");
            if (!File.Exists(_logFile))
                File.WriteAllText(_logFile, "1");
            else
            {
                int currentCount;
                try
                {
                    currentCount = Convert.ToInt32(File.ReadAllText(_logFile));
                }
                catch
                {
                    File.WriteAllText(_logFile, "1");
                    currentCount = 1;
                }
                if (currentCount > _restartCount)
                {
                    File.WriteAllText(_logFile, "1");
                    $"systemctl -q restart {_ovpnUnit}".Bash();
                    Thread.Sleep(1000);
                    var response = $"systemctl is-active {_ovpnUnit}".Bash().Trim();
                    if (response == "active")
                        logger.LogInformation("Success restart openvpn client by counter");
                    else
                        logger.LogError("FAILED restart openvpn client by counter");
                }
                else
                {
                    currentCount++;
                    File.WriteAllText(_logFile, currentCount.ToString());
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
        process.WaitForExit(35000);

        return result;
    }
}