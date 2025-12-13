using System;

namespace SkillbarCapture
{
    internal static class CliUsage
    {
        public static void PrintUsage()
        {
            Console.WriteLine("用法（实时截帧）：");
            Console.WriteLine("  SkillbarCapture.exe <hwnd_hex|process_name> [output_folder] [frameCount] [sampleStride]");
            Console.WriteLine("  （启动后会等待施法条出现以定位，定位后开始保存帧）");
            Console.WriteLine("  （默认输出目录：工程目录 ./castbar）");
            Console.WriteLine("示例：");
            Console.WriteLine("  SkillbarCapture.exe Gw2-64.exe");
            Console.WriteLine("  SkillbarCapture.exe Gw2-64.exe 200 5");
            Console.WriteLine("  SkillbarCapture.exe 0000000001230042 .\\castbar 200 5");
            Console.WriteLine("  SkillbarCapture.exe castbar Gw2-64.exe .\\castbar 200 5  (等价写法)");
            Console.WriteLine();
            Console.WriteLine("用法（离线标注施法条）：");
            Console.WriteLine("  SkillbarCapture.exe markcastbar [input_folder] [output_folder]");
            Console.WriteLine("示例：");
            Console.WriteLine("  SkillbarCapture.exe markcastbar .\\screen");
        }
    }
}
