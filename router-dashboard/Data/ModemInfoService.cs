using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using static System.String;

namespace router_dashboard.Data;

public class ModemInfoService
{
    private readonly ILogger _Logger;
    public ModemInfoService(ILogger<ModemInfoService> logger)
    {
        _Logger = logger;
    }
    public async Task<List<ModemInfo>> GetModemInfo()
    {
        var machine = File.ReadLines("/etc/os-release").FirstOrDefault(t => t.Contains("MACHINE"))?.Split("=")[1]
            .Replace("\"", "");
        switch (machine)
        {
            case "yocto-arm64-imx":
            case "yocto-x86-qmi":
            case "yocto-arm64-nanopi":
                _Logger.LogInformation("QMI system detected!!");
                return await GetModemInfoQmi();
            case "yocto-x86-e3372":
                _Logger.LogInformation("e3372 system detected!!");
                return await GetModemInfoE3372();
        }
        return null;
    }

    private Task<List<ModemInfo>> GetModemInfoQmi()
    {
        List<ModemInfo> infoList = new List<ModemInfo>();
        var devicesRaw = "nmcli -f DEVICE,STATE -t device".Bash().Split('\r', '\n');
        foreach (var deviceRaw in devicesRaw)
        {
            if (IsNullOrWhiteSpace(deviceRaw))
                continue;
            var name = deviceRaw.Split(":")[0];
            if (name.Contains("cdc-wdm"))
            {
                string modemInfoJson = $"/etc/zabbix/zabbix_agentd.d/qmi/qmi.py {name}".Bash();
                {
                    var modemInfo = JsonNode.Parse(modemInfoJson);
                    infoList.Add(new ModemInfo()
                    {
                        Name = name,
                        Status = modemInfo?["Status"]?.ToString(),
                        WorkMode = modemInfo?["Mode"]?.ToString(),
                        Opsos = modemInfo?["Name"]?.ToString(),
                        Rssi = modemInfo?["RSSI"]?.ToString(),
                        Sinr = modemInfo?["SINR"]?.ToString(),
                        Iccid = modemInfo?["ICCID"]?.ToString(),
                    });
                }
            }
        }
        return Task.FromResult(infoList);
    }

    private async Task<List<ModemInfo>> GetModemInfoE3372()
    {
        List<ModemInfo> infoList = new List<ModemInfo>();
        foreach (NetworkInterface inter in NetworkInterface.GetAllNetworkInterfaces().OrderBy(i => i.Name))
        {
            if (inter.Name.Contains("lte"))
            {
                var ip = inter.GetIPProperties().UnicastAddresses.FirstOrDefault()?.Address;
                HttpClient http = await GetClient(ip);
                var sigInfo = await GetSignal(http);
                var devInfo = await GetDevInfo(http);
                var opsos = await GetOperator(http);
                infoList.Add(new ModemInfo()
                {
                    Name = inter.Name,
                    Ip = ip,
                    Opsos = opsos,
                    WorkMode = devInfo.workmode,
                    Rssi = sigInfo.rssi,
                    Sinr = sigInfo.sinr,
                    Iccid = devInfo.iccid
                });
            }
        }
        return infoList;
    }

    private async Task<HttpClient> GetClient(IPAddress? ip)
    {
        SocketsHttpHandler handler = new SocketsHttpHandler();
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(ip, 0));
            socket.NoDelay = true;
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("User-Agent", "curl/7.55.1");
        string responseStringKey;
        try
        {
            responseStringKey = await http.GetStringAsync("http://192.168.8.1/api/webserver/SesTokInfo");
        }
        catch (Exception ex)
        {
            responseStringKey = ex.Message;
            http.Dispose();
        }
        XElement xml = XElement.Parse(responseStringKey);
        string? token = xml.Element("TokInfo")?.Value;
        string? session = xml.Element("SesInfo")?.Value;
        http.DefaultRequestHeaders.Add("__RequestVerificationToken", token);
        http.DefaultRequestHeaders.Add("Cookie", session);
        http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        http.DefaultRequestHeaders.Add("Accept-Language", "ru-RU,ru;q=0.8,en-US;q=0.5,en;q=0.3");
        return http;
    }

    private async Task<(string? rssi, string? sinr)> GetSignal(HttpClient http)
    {
        string responseString = Empty;
        try
        {
            responseString = await http.GetStringAsync("http://192.168.8.1/api/device/signal");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
        XElement xml = XElement.Parse(responseString);
        return (xml.Element("rssi")?.Value, xml.Element("sinr")?.Value);
    }

    private async Task<(string? iccid, string? workmode)> GetDevInfo(HttpClient http)
    {
        string responseString = Empty;
        try
        {
            responseString = await http.GetStringAsync("http://192.168.8.1/api/device/information");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
        XElement xml = XElement.Parse(responseString);
        return (xml.Element("Iccid")?.Value, xml.Element("workmode")?.Value);
    }

    private async Task<string?> GetOperator(HttpClient http)
    {
        string responseString = Empty;
        try
        {
            responseString = await http.GetStringAsync("http://192.168.8.1/api/net/current-plmn");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
        XElement xml = XElement.Parse(responseString);
        return xml.Element("FullName")?.Value;
    }
}
