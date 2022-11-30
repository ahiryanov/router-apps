using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace NMControl_v0;
class Program
{
    static void Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddSystemdConsole();
        });
        ILogger logger = loggerFactory.CreateLogger("main");

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
        devices = devices.OrderBy(m => m.Name).ToList();
        foreach (var device in devices)
        {
            switch (device.State)
            {
                case "connected":
                    logger.LogInformation($"Stay tune with {device.Name}. State {device.State}");
                    break;
                case "connecting (prepare)":
                    logger.LogWarning($"Trying reset connection {device.Name}. Reason: {device.State}");
                    ConnectionDown(device.Name);
                    Thread.Sleep(2000);
                    var prepareResponse = ConnectionUp(device.Name);
                    if (prepareResponse.ToLower().Contains("failed") || prepareResponse.ToLower().Contains("timeout"))
                    {
                        logger.LogError($"Failed activation of connecting (prepare) {device.Name}");
                        ConnectionDown(device.Name);
                    }
                    else
                        logger.LogWarning($"Success activation of connecting (prepare) {device.Name}");
                    break;
                case "disconnected":
                    logger.LogWarning($"Trying reset connection {device.Name}. Reason: {device.State}");
                    var disconnectResponse = ConnectionUp(device.Name);
                    if (disconnectResponse.ToLower().Contains("failed") || disconnectResponse.ToLower().Contains("timeout"))
                    {
                        logger.LogError($"Activation failed of disconnected {device.Name}");
                        ConnectionDown(device.Name);
                    }
                    else
                        logger.LogWarning($"Success activation of disconnected {device.Name}");
                    break;
                case "unavailable":
                    logger.LogInformation($"Stay tune with {device.Name}. State {device.State}");
                    break;
            }
        }
    }

    static string ConnectionDown(string deviceName)
    {
        return $"nmcli -w 30 connection down {deviceName}-conn 2>&1".Bash();
    }
    static string ConnectionUp(string deviceName)
    {
        return $"nmcli -w 30 connection up {deviceName}-conn 2>&1".Bash();
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
                Arguments = $"-c \"{escapedArgs}\"",
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