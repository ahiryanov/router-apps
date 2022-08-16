using System;
using Tmds.DBus;
using NetworkManager.DBus;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;

namespace netmon
{
    class QosCheckInfo
    {
        public DateTime Time { get; set; }
        public bool IsSuccess { get; set; }
        public string? ResponseString { get; set; }
        public long Latency { get; set; }

    }
    class Program
    {
        public static async Task<QosCheckInfo> CurlPing(IPAddress address)
        {
            SocketsHttpHandler handler = new SocketsHttpHandler();
            handler.ConnectCallback = async (context, cancellationToken) =>
            {
                Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(address, 0));
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
            http.Timeout = TimeSpan.FromSeconds(5);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string _responseString = string.Empty;
            bool _isSuccess = false;
            try
            {
                var response = await http.SendAsync(new HttpRequestMessage(new HttpMethod("GET"), "http://2ip.ru/"));
                _responseString = await response.Content.ReadAsStringAsync();
                _isSuccess = true;
            }
            catch
            {
                _isSuccess = false;
                _responseString = "Timeout";
            }

            watch.Stop();

            return (new QosCheckInfo() { 
                Time = DateTime.Now, 
                IsSuccess = _isSuccess, 
                ResponseString = _responseString, 
                Latency = watch.ElapsedMilliseconds 
                });
        }
        static async Task Main(string[] args)
        {
            Console.WriteLine("Monitoring network state changes. Press Ctrl-C to stop.");

            var systemConnection = Connection.System;
            var networkManager = systemConnection.CreateProxy<INetworkManager>("org.freedesktop.NetworkManager",
                                                                               "/org/freedesktop/NetworkManager");
            var networkManagerSettings = systemConnection.CreateProxy<ISettings>("org.freedesktop.NetworkManager",
                                                                               "/org/freedesktop/NetworkManager/Settings");
            var dev = await networkManager.GetAllDevicesAsync();
            ObjectPath connDown;
            foreach (var connection in await networkManagerSettings.ListConnectionsAsync())
            {

                var settings = await connection.GetSettingsAsync();
                var conn = settings.Where(d => d.Key == "connection").FirstOrDefault().Value;
                var id = conn.Where(c => c.Key == "id").FirstOrDefault().Value as string;

                if (id.Contains("Wired"))
                {
                    connDown = connection.ObjectPath;
                    Console.WriteLine(id);
                }
            }

            while (true)
            {
                var https = new List<Task<QosCheckInfo>>();
                https.Add(CurlPing(IPAddress.Parse("100.72.65.85")));
                https.Add(CurlPing(IPAddress.Parse("172.16.100.58")));
                var tasks = https.Select(i => i);
                var responses = await Task.WhenAll(tasks);
                foreach (var response in responses)
                {
                    Console.WriteLine($"{response.ResponseString} {response.Latency}");
                }                
                await Task.Delay(3000);
            }

            /*try
            {
                var active = await networkManager.ActivateConnectionAsync(connDown,"/","/");
            }
            catch(Exception e)
            {
                Console.WriteLine($"{e.Message}");
            }
            await Task.Delay(int.MaxValue);*/

        }
    }
}
