using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace lcd
{
    internal static class Program
    {
        private static SerialPort? _port;

        static async Task Main()
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .AddSystemdConsole();
            });
            ILogger logger = loggerFactory.CreateLogger("lcd");

            do
            {
                logger.LogInformation("Trying find port ttyUSB0");
                var portName = SerialPort.GetPortNames().FirstOrDefault(p => p.Contains("ttyUSB0"));
                if (portName != null)
                {
                    logger.LogInformation("Successful find ttyUSB0, stay tuned");
                    _port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One);
                    logger.LogWarning(portName);
                    try
                    {
                        _port.Open();
                        logger.LogInformation("Successful open port ttyUSB0. Starting...");
                        break;
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e.Message);
                    }
                }
                else
                {
                    logger.LogError("Can't find ttyUSB0!");
                }
                await Task.Delay(10000);
                //Thread.Sleep(10000);
            } while (true);


            var host = Environment.MachineName;
            var ver = File.ReadAllLines("/etc/os-release").FirstOrDefault(l => l.Contains("BUILD_VERSION"))?.Split('"')[1];
            
            
            while (true)
            {
                if (_port is { IsOpen: true })
                {
                    var ip = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(i => i.Name == "tun1")?.GetIPProperties()
                        .UnicastAddresses[0].Address.ToString();

                    var online = "offline";
                    var ping = new Ping();
                    try
                    {
                        var reply = ping.Send("172.30.0.1", 500);
                        if (reply.Status == IPStatus.Success)
                            online = "online";
                    }
                    catch
                    {
                        // ignored
                    }
                    var modems = "nmcli -f DEVICE,STATE -t device".Bash().Split('\r', '\n').Count(s => s.Contains("cdc-wdm"));
                    var modemsUp = "nmcli -f DEVICE,STATE -t device".Bash().Split('\r', '\n').Count(s => s.Contains(":connected"));

                    _port.Write($"ECHO 1 {host}\r");
                    _port.Write($"ECHO 2 VER: {ver}\r");
                    await Task.Delay(6000);
                    //Thread.Sleep(6000);
                    _port.Write($"ECHO 1 {ip}\r");
                    _port.Write($"ECHO 2 VPN: {online}\r");
                    await Task.Delay(6000);
                    //Thread.Sleep(6000);
                    _port.Write($"ECHO 1 LTE: {modems}\r");
                    _port.Write($"ECHO 2 LTE UP: {modemsUp}\r");
                    await Task.Delay(6000);
                    //Thread.Sleep(6000);
                }
                else
                {
                    logger.LogError("Port ttyUSB0 is not opened");
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
            process.WaitForExit();

            return result;
        }
    }
}