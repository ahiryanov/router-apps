﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace lte_reboot;
class Program
{
    private static string _srv;
    private static int maxRtt = 300;
    private static int maxLoss = 25;
    private const int _restartCount = 5;
    private const string _logFile = "/tmp/lte";
    static void Main(string[] args)
    {
        
        //SRV detect 
        if (File.Exists("/etc/openvpn/client.conf"))
            _srv = "cat /etc/openvpn/client.conf | grep \"^remote \" | awk '{{print $2}}'".Bash().Trim();
        if (File.Exists("/etc/openvpn/client/client.conf"))
            _srv = "cat /etc/openvpn/client/client.conf | grep \"^remote \" | awk '{{print $2}}'".Bash().Trim();

        if (args.Length == 2)
        {
            int.TryParse(args[0], out maxLoss);
            int.TryParse(args[1], out maxRtt);
        }

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddSystemdConsole();
        });
        ILogger logger = loggerFactory.CreateLogger("main");
        bool mptcpV1 = isMPTCPv1()!.Value;
        logger.LogInformation(mptcpV1?"MPTCPv1":"MPTCPv0");
        logger.LogInformation($"Detected server ip: {_srv}");
        logger.LogInformation($"Current thresholds. Max loss {maxLoss}, Max rtt {maxRtt}");
        var devices = new List<Device>();
        var devices_raw = "nmcli -f DEVICE,STATE -t device".Bash().Split('\r', '\n');

        foreach (var device_raw in devices_raw)
        {
            if (string.IsNullOrWhiteSpace(device_raw))
                continue;
            var device = new Device();
            device.Name = device_raw.Split(":")[0];
            device.State = device_raw.Split(":")[1];
            if (device.Name.Contains("cdc-wdm"))
            {
                device.Iface = $"qmicli --silent -d /dev/{device.Name} --get-wwan-iface".Bash().Replace("\n", "");
                devices.Add(device);
            }
        }
        logger.LogInformation($"Device recognized count: {devices.Count}");
        devices = devices.OrderBy(m => m.Name).ToList();

        foreach (var device in devices)
        {
            switch (device.State)
            {
                case "connected":
                    var ping = $"ping {_srv} -I {device.Iface} -A -w 1 -q -s 1400".Bash();
                    int PacketReceive =
                        int.TryParse(new Regex(@"(\w+)\s" + "packets received").Match(ping).Groups[1].Value, out PacketReceive) ? PacketReceive : 0;
                    int PacketLoss =
                        int.TryParse(new Regex(@"(\d+)%\s" + "packet loss").Match(ping).Groups[1].Value, out PacketLoss) ? PacketLoss : 100;
                    int AvgRtt =
                        int.TryParse(new Regex("/" + @"(\d+)" + ".").Match(ping).Groups[1].Value, out AvgRtt) ? AvgRtt : 10000;
                    logger.LogInformation($"{device.Name} ({device.Iface}) state {device.State}. Packet receive: {PacketReceive} # Packet loss %: {PacketLoss} # Average RTT ms: {AvgRtt}");
                    if (PacketLoss > maxLoss || AvgRtt > maxRtt)
                    {
                        if (mptcpV1)//section for MPTCPv1
                        {
                            var mptcpId = $"ip mptcp endpoint | grep {device.Iface} | awk '{{print $3}}'".Bash();
                            $"ip mptcp endpoint del id {mptcpId}".Bash();
                            //logger.LogInformation($"MPTCP NEW ID: {mptcpId}");
                        }
                        else //section for MPTCPv0
                        {
                            $"ip link set dev {device.Iface} multipath off".Bash();
                        }
                        
                        int countRoutesDevice = int.TryParse($"ip route show default dev {device.Iface} | wc -l".Bash(), out countRoutesDevice) ? countRoutesDevice : 0;
                        if (countRoutesDevice == 0)
                        {
                            logger.LogInformation($"{device.Name} ({device.Iface}) routes already deleted");
                            break;
                        }
                        int countRoutes = int.TryParse($"ip route show default | wc -l".Bash(), out countRoutes) ? countRoutes : 0;
                        if (countRoutes <= 1)
                        {
                            logger.LogError("Last route can't remove");
                            break;
                        }
                        $"ip route del default dev {device.Iface}".Bash();
                        logger.LogWarning($"{device.Name} {device.Iface} switched off multipath and default route");
                    }
                    else
                    {
                        $"ip link set dev {device.Iface} multipath on".Bash();
                        int countRoutes = int.TryParse($"ip route show default dev {device.Iface} | wc -l".Bash(), out countRoutes) ? countRoutes : 0;
                        if (countRoutes == 0)
                        {
                            var refreshRouteIsUp = ConnectionUp(device.Name, logger);
                            if (!refreshRouteIsUp)
                            {
                                logger.LogError($"Refresh route failed {device.Name} ({device.Iface})");
                                ConnectionDown(device.Name);
                            }
                            else
                                logger.LogWarning($"Refresh route success {device.Name} ({device.Iface})");
                        }
                    }
                    break;

                case "connecting (prepare)":
                    logger.LogWarning($"Trying reset connection {device.Name} ({device.Iface}). Reason: {device.State}");
                    ConnectionDown(device.Name);
                    Thread.Sleep(2000);
                    var prepareIsUp = ConnectionUp(device.Name, logger);
                    if (!prepareIsUp)
                    {
                        logger.LogError($"Failed activation of connecting (prepare) {device.Name}");
                        ConnectionDown(device.Name);
                    }
                    else
                        logger.LogWarning($"Success activation of connecting (prepare) {device.Name}");
                    break;

                case "disconnected":
                    logger.LogWarning($"Trying reset connection {device.Name} ({device.Iface}). Reason: {device.State}");
                    var disconnectIsUp = ConnectionUp(device.Name, logger);
                    if (!disconnectIsUp)
                    {
                        logger.LogError($"Activation failed of disconnected {device.Name}");
                        ConnectionDown(device.Name);
                    }
                    else
                        logger.LogWarning($"Success activation of disconnected {device.Name}");
                    break;

                case "unavailable":
                    logger.LogInformation($"{device.Name} ({device.Iface}) state {device.State}");
                    ConnectionUp(device.Name, logger);
                    break;
            }
        }
    }

    static bool? isMPTCPv1()
    {
        if (File.Exists("/proc/sys/net/mptcp/enabled"))
            return true;
        if (File.Exists("/proc/sys/net/mptcp/mptcp_enabled"))
            return false;
        return null;
    }

    static string ConnectionDown(string deviceName)
    {
        return $"nmcli -w 15 connection down {deviceName}-conn".Bash();
    }
    static bool ConnectionUp(string deviceName, ILogger logger)
    {
        string resetModemlog = $"{_logFile}-{deviceName}-reset";
        bool successUp = true;
        var response = $"nmcli -w 15 connection up {deviceName}-conn".Bash();
        if (response.ToLower().Contains("failed") || response.ToLower().Contains("timeout"))
        {
            successUp = false;
            if (!File.Exists(resetModemlog))
                File.WriteAllText(resetModemlog, "1");
            else
            {
                int currentCount;
                try
                {
                    currentCount = Convert.ToInt32(File.ReadAllText(resetModemlog));
                }
                catch
                {
                    File.WriteAllText(resetModemlog, "1");
                    currentCount = 1;
                }
                if (currentCount > _restartCount) //power reboot modem every {restartCount} times of reconnect
                {
                    File.WriteAllText(resetModemlog, "1");
                    $"qmicli -p -d /dev/{deviceName} --dms-set-operating-mode=reset".Bash();
                    logger.LogError($"{deviceName} POWER REBOOT");
                }
                else
                {
                    if (currentCount == 3) //sim reboot in the middle of cycle
                    {
                        $"qmicli -p -d /dev/{deviceName} --uim-sim-power-off=1".Bash();
                        Thread.Sleep(1500);
                        $"qmicli -p -d /dev/{deviceName} --uim-sim-power-on=1".Bash();
                        logger.LogError($"{deviceName} SIM REBOOT");
                    }
                    currentCount++;
                    File.WriteAllText(resetModemlog, currentCount.ToString());
                }
            }
        }
        else
        {
            File.WriteAllText(resetModemlog, "1");
        }
        return successUp;
    }
}

class Device
{
    public string Name { get; set; }
    public string State { get; set; }
    public string Iface { get; set; }
    public override string ToString()
    {
        return $"{Name} {State} {Iface}";
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
        process.WaitForExit();

        return result;
    }
}