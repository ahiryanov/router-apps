using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Globalization;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace gps_viewer
{

    public class Program
    {

        public static void Main(string[] args)
        {
            CreateHostBuilder(args).ConfigureServices(services => { services.AddHostedService<GetGps>(); })
                .Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                    webBuilder.UseUrls("http://0.0.0.0:5000");
                });
    }

    public class GetGps : BackgroundService
    {
        private static readonly string[] Serials = { "/dev/ttyUSB0", "/dev/ttyUSB1", "/dev/ttymxc1" };
        public static GpsPosition Current;
        private static SerialPort _port;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            int i = 0;
            string read = null;
            do
            {
                try
                {
                    _port = new SerialPort(Serials[i], 9600, Parity.None, 8, StopBits.One);
                    _port.Open();
                    _port.ReadTimeout = 1000;
                    read = _port.ReadLine();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }

                await Task.Delay(1000);

                if (read != null)
                {
                    Console.WriteLine($"Serial {Serials[i]} is OK. Continue");
                    break;
                }

                Console.WriteLine($"Serial {Serials[i]} unreadable. Trying next.");
                _port.Close();

                i++;
                if (i >= Serials.Length)
                {
                    i = 0;
                    await Task.Delay(100000, stoppingToken);
                }
            } while (true);

            try
            {
                while (true)
                {
                    string line = _port.ReadLine();
                    if (line.Contains("GPRMC") || line.Contains("GNRMC"))
                    {
                        string[] lines = line.Split(',');
                        string LAT = string.IsNullOrWhiteSpace(lines[3]) ? "0" : lines[3];
                        string LON = string.IsNullOrWhiteSpace(lines[5]) ? "0" : lines[5];
                        string SPEED = string.IsNullOrWhiteSpace(lines[7]) ? "0" : lines[7];
                        try
                        {
                            LAT = Math.Round(Convert.ToDouble(LAT.Substring(0, 2)) + Convert.ToDouble(LAT.Substring(2)) / 60, 6)
                                .ToString(CultureInfo.InvariantCulture);
                            LON = Math.Round(Convert.ToDouble(LON.Substring(0, 3)) + Convert.ToDouble(LON.Substring(3)) / 60, 6)
                                .ToString(CultureInfo.InvariantCulture);
                            SPEED = Convert.ToInt32(Convert.ToDouble(SPEED) * 1.852).ToString();
                        }
                        catch
                        {
                            // ignored
                        }

                        Current = new GpsPosition { Lat = LAT, Lon = LON, Speed = SPEED };
                        await Task.Delay(1000, stoppingToken);
                    }
                    await Task.Delay(200, stoppingToken);

                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }

    public class GpsPosition
    {
        public string Lat { get; set; }
        public string Lon { get; set; }
        public string Speed { get; set; }
    }
}
