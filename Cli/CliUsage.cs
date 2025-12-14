using System;

namespace SkillbarCapture
{
    internal static class CliUsage
    {
        public static void PrintUsage()
        {
            Console.WriteLine("Usage (online capture):");
            Console.WriteLine("  SkillbarCapture.exe <hwnd_hex|process_name> [output_folder] [frameCount] [sampleStride]");
            Console.WriteLine("  Wait for cast bar to appear to locate; starts saving after locating; default output ./castbar");
            Console.WriteLine("Examples:");
            Console.WriteLine("  SkillbarCapture.exe Gw2-64.exe");
            Console.WriteLine("  SkillbarCapture.exe Gw2-64.exe 200 5");
            Console.WriteLine("  SkillbarCapture.exe 0000000001230042 .\\castbar 200 5");
            Console.WriteLine("  SkillbarCapture.exe castbar Gw2-64.exe .\\castbar 200 5  (capture cast bar only)");
            Console.WriteLine();
            Console.WriteLine("Usage (full window shot):");
            Console.WriteLine("  SkillbarCapture.exe fullshot <hwnd_hex|process_name> [output_folder]");
            Console.WriteLine("Example:");
            Console.WriteLine("  SkillbarCapture.exe fullshot Gw2-64.exe .\\online_test");
            Console.WriteLine();
            Console.WriteLine("Usage (offline mark castbar ROI):");
            Console.WriteLine("  SkillbarCapture.exe markcastbar [input_folder] [output_folder]");
            Console.WriteLine("Example:");
            Console.WriteLine("  SkillbarCapture.exe markcastbar .\\screen");
            Console.WriteLine();
            Console.WriteLine("Usage (offline phase analysis):");
            Console.WriteLine("  SkillbarCapture.exe phase [castbar_folder] [config.json]");
            Console.WriteLine("Example:");
            Console.WriteLine("  SkillbarCapture.exe phase .\\castbar");
        }
    }
}
