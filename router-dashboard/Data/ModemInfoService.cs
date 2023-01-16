using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Xml.Linq;
using static System.String;

namespace router_dashboard.Data
{
    public class ModemInfoService
    {
        public async Task<List<ModemInfo>> GetModemInfo()
        {
            if (Directory.GetFiles("/sys/class/net").Count(f => f.Contains("cdc-wdm")) > 2)
            {
                Console.WriteLine("QMI system detected!");
            }
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
}
