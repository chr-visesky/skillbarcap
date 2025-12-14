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

            if (args[0].Equals("fullshot", StringComparison.OrdinalIgnoreCase))
            {
                FullCaptureCommand.Run(args);
                return;
            }

            if (args[0].Equals("phase", StringComparison.OrdinalIgnoreCase))
            {
                OfflineCommands.RunAnalyzeCastbarPhase(args);
                return;
            }

            await OnlineCaptureCommand.RunAsync(args);
        }
    }
}
