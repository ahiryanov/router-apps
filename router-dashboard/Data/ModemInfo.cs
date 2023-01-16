using System.Net;

namespace router_dashboard.Data
{
    public class ModemInfo
    {
        public string Name { get; set; }
        public IPAddress? Ip { get; set; }
        public string? Opsos { get; set; }
        public string? WorkMode { get; set; }
        public string? Rssi { get; set; }
        public string? Sinr { get; set; }
        public string? Iccid { get; set; }
    }
}
