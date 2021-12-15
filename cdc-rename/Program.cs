using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;

namespace cdc_rename
{
    public class Program
    {
        
        
        static void Main(string[] args)
        {
            ILoggerFactory loggerFactory = LoggerFactory.Create(logger => logger.AddSystemdConsole());
            ILogger<Program> logger = loggerFactory.CreateLogger<Program>();

            var files = Directory.GetFileSystemEntries("/sys/class/net").Where(t => t.Contains("wwan"));
            foreach (string file in files)
                Console.WriteLine(file.Substring(file.LastIndexOf("/") + 1));
            
            

        }
        
    }
}
