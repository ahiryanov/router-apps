using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace gps_viewer
{
    
    public class Program
    {

        public static void Main(string[] args)
        {
            CreateHostBuilder(args).ConfigureServices(services =>
            {
                services.AddHostedService<GetGps>();
            })
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
        //private static string[] serials = { "/dev/ttymxc1" };
        private static string[] serials = { "/dev/ttyUSB0", "/dev/ttyUSB1" };
        public static GpsPosition current;
        public static SerialPort port;
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            int i = 0;
            string read = null;
            do
            {
                try
                {
                    port = new SerialPort(serials[i], 9600, Parity.None, 8, StopBits.One);
                    port.Open();
                    port.ReadTimeout = 1000;
                    read = port.ReadLine();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
                await Task.Delay(1000);
                
                if (read != null)
                {
                    Console.WriteLine($"Serial {serials[i]} is OK. Continue");
                    break;
                }
                Console.WriteLine($"Serial {serials[i]} unreadable. Trying next.");
                port.Close();

                i++;
                if (i >= serials.Length)
                {
                    i = 0;
                    await Task.Delay(100000);
                }
            } while (true);

            try
            {
                port.DataReceived += new SerialDataReceivedEventHandler(port_DataReceived);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private static void port_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            string line = port.ReadLine();
            
                
            if (line.Contains("GPRMC") || line.Contains("GNRMC"))
            {
                string[] lines = line.Split(',');
                string LAT = string.IsNullOrWhiteSpace(lines[3]) ? "0" : lines[3];
                string LON = string.IsNullOrWhiteSpace(lines[5]) ? "0" : lines[5];
                string SPEED = string.IsNullOrWhiteSpace(lines[7]) ? "0" : lines[7];
                try
                {
                    LAT = Math.Round(Convert.ToDouble(LAT.Substring(0, 2)) + Convert.ToDouble(LAT.Substring(2)) / 60, 6).ToString();
                    LON = Math.Round(Convert.ToDouble(LON.Substring(0, 3)) + Convert.ToDouble(LON.Substring(3)) / 60, 6).ToString();
                    SPEED = Convert.ToInt32(Convert.ToDouble(SPEED) * 1.852).ToString();
                }
                catch { }
                current = new GpsPosition() { Lat = LAT, Lon = LON, Speed = SPEED };
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
