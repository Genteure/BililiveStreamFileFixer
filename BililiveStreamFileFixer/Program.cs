/*
B站直播录像修复工具
Copyright(C) 2020 Genteure

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.If not, see<https://www.gnu.org/licenses/>.
*/
using CommandLine;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BililiveStreamFileFixer
{
    internal class Program
    {
        private class Options
        {
            [Value(0, Required = true, HelpText = "要修复的文件", Min = 1)]
            public IEnumerable<string> Input { get; set; }

            [Option('i', "interactive", Default = false, Required = false, HelpText = "不直接输出文件，需要手动确认")]
            public bool Interactive { get; set; }

            [Option('d', "dry-run", Default = false, Required = false, HelpText = "只输出判断结果不输出文件")]
            public bool DryRun { get; set; }
        }

        private static void Main(string[] args)
        {
            Console.Write("B站直播录像修复工具 by genteure\n问题反馈 flvfix@genteure.com / https://github.com/Genteure/BililiveStreamFileFixer \n\n");

            Parser.Default.ParseArguments<Options>(args)
                      .WithParsed(o =>
                      {
                          int fileCount = o.Input.Count();
                          if (fileCount > 1)
                          {
                              Console.WriteLine($"批量处理 {fileCount} 个文件。");
                          }

                          foreach (var file in o.Input)
                          {
                              if (fileCount > 1)
                                  Console.WriteLine($"\n读取文件: {file} \n");

                              try
                              {
                                  using (var p = new Processor(file))
                                  {
                                      Console.WriteLine("读取文件检测中...");
                                      if (p.DetectProblem())
                                      {
                                          Console.Write("\n");
                                          Console.WriteLine(p.GetProblemDescription());
                                          if (o.DryRun)
                                          {
                                              goto no;
                                          }
                                          if (!o.Interactive)
                                          {
                                              goto yes;
                                          }
                                          Console.WriteLine("是否输出文件？输入 [y]es/[n]o 选择：");
                                          while (true)
                                          {
                                              switch (Console.ReadLine().ToLowerInvariant())
                                              {
                                                  case "y":
                                                  case "yes":
                                                      goto yes;
                                                  case "n":
                                                  case "no":
                                                      goto no;
                                                  default:
                                                      Console.WriteLine("是否输出文件？输入 [y]es/[n]o 选择：");
                                                      break;
                                              }
                                          }
                                      yes:
                                          Console.WriteLine("写文件中...");
                                          p.WriteNewFile();
                                          Console.WriteLine("完成");
                                      }
                                      else
                                      {
                                          Console.WriteLine("未检测到问题");
                                      }
                                  }
                              no:;
                              }
                              catch (Exception ex)
                              {
                                  Console.WriteLine("\n处理时出现错误：" + ex.Message);
                                  Console.WriteLine("\n" + ex.ToString());

                                  Environment.ExitCode = -1;
                              }
                          }
                      });
        }
    }
}
