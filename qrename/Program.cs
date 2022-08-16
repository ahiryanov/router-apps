using System;

namespace qrename
{
    public class Program
    {
        static void Main(string[] args)
        {
            string[] subs = args[0].Split('.','-');
            try
            {
                if (subs[0] == "1")
                {
                    Console.Out.WriteLine($"qmi{Convert.ToInt32(subs[0]) * Convert.ToInt32(subs[2]) - 1}");
                    return;
                }

                else
                {
                    Console.Out.WriteLine($"qmi{Convert.ToInt32(subs[0]) + Convert.ToInt32(subs[2]) + 1}");
                    return;
                }
            }
            catch
            {
                var rand = new Random();
                Console.Out.WriteLine($"qmi{rand.Next(101)}");
                return;
            }
        }
    }
}
