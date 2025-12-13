using System;
using System.Threading.Tasks;

namespace SkillbarCapture
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                RunAsync(args).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex);
            }
        }

        private static async Task RunAsync(string[] args)
        {
            if (args.Length == 0)
            {
                CliUsage.PrintUsage();
                return;
            }

            if (args[0].Equals("markcastbar", StringComparison.OrdinalIgnoreCase))
            {
                OfflineCommands.RunMarkCastbar(args);
                return;
            }

            await OnlineCaptureCommand.RunAsync(args);
        }
    }
}
