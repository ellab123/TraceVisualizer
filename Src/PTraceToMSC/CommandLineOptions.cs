using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PTraceToMSC
{
    public enum TestResult
    {
        /// <summary>
        /// No errors
        /// </summary>
        Success = 0,

        /// <summary>
        /// Invalid parameters passed
        /// </summary>
        InvalidParameters = 1,

        /// <summary>
        /// An internal error was encountered, typically indicating a bug in the compiler or runtime.
        /// </summary>
        InternalError = 2,
    }
    public class CommandLineOptions
    {
        //P or PSharp tool which generated the input trace:
        public string pTool;
        //Input file with P trace:
        public string inputTraceFile;
        //Output file with ShiViz trace:
        public string outputTraceFile;
        //If "true", only leave interleaving actions in the trace (create/send/dequeue...):
        public bool interleaving = false;
    }
    public class PTraceToMSCCommandLine
    {
        //Command line arguments example:
        ///ptool:psharptester /trace:C:\Traces\PingPong.trace /output:C:\Traces\PingPong.shiviz
        public static CommandLineOptions ParseCommandLine(string[] args)
        {
            var options = new CommandLineOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                if (arg[0] == '-' || arg[0] == '/')
                {
                    string option = arg.TrimStart('/', '-').ToLower();

                    string param = string.Empty;

                    int sepIndex = option.IndexOf(':');

                    if (sepIndex > 0)
                    {
                        param = option.Substring(sepIndex + 1);
                        option = option.Substring(0, sepIndex);
                    }
                    else if (sepIndex == 0)
                    {
                        PrintHelp(arg, "Malformed option");
                        return null;
                    }

                    switch (option)
                    {
                        case "?":
                        case "h":
                            {
                                PrintHelp(null, null);
                                Environment.Exit((int)TestResult.Success);
                                break;
                            }
                        case "ptool":
                            {
                                if (options.pTool != null)
                                {
                                    PrintHelp(arg, "Only one /pTool argument is allowed");
                                    return null;
                                }
                                options.pTool = param;
                                break;
                            }
                        case "trace":
                            {
                                if (options.inputTraceFile != null)
                                {
                                    PrintHelp(arg, "Only one /trace argument is allowed");
                                    return null;
                                }
                                if (!File.Exists(param))
                                {
                                    PrintHelp(param, "Cannot find input trace file");
                                    return null;
                                }
                                options.inputTraceFile = param;
                                break;
                            }
                        case "output":
                            {
                                if (options.outputTraceFile != null)
                                {
                                    PrintHelp(arg, "Only one /trace argument is allowed");
                                    return null;
                                }
                                options.outputTraceFile = param;
                                break;
                            }
                        case "interleaving":
                            {
                                options.interleaving = true;
                                break;
                            }
                        default:
                            PrintHelp(arg, "Invalid option");
                            return null;
                    }
                }
                else
                {
                    PrintHelp(arg, "argument must start with '-' or '?' ");
                    return null;
                }
            }

            if (options.pTool == null)
            {
                PrintHelp(null, "No ptool specified");
                return null;
            }
            if (options.inputTraceFile == null)
            {
                PrintHelp(null, "No input trace file specified");
                return null;
            }
            if (options.outputTraceFile == null)
            {
                PrintHelp(null, "No output file specified");
                return null;
            }

            return options;
        }

        public static void PrintHelp(string arg, string errorMessage)
        {
            if (errorMessage != null)
            {
                if (arg != null)
                    Console.WriteLine("Error: \"{0}\" - {1}", arg, errorMessage);
                else
                    Console.WriteLine("Error: {0}", errorMessage);
            }

            PrintUsage();
        }
        public static void PrintUsage()
        {
            Console.WriteLine("USAGE: PTraceToMSC.exe /ptool:[psharptester|ptester] /trace:<input trace file> /output:<output trace file> [-interleaving] [/?]");
            Console.WriteLine("Compiles .txt execution trace from PSharTester.exe or pt.exe into ShiViz MSC visualization tool log");
            Console.WriteLine("-interleaving: generate log with only error reports and interleaving actions: machine create/halt, enqueue, dequeue");
        }
        private static int Main(string[] args)
        {
            var options = ParseCommandLine(args);
            if (options == null)
            {
                Environment.Exit((int)TestResult.InvalidParameters);
            }
            //Read input trace file:
            string inputTrace;
            try
            {
                inputTrace = File.ReadAllText(options.inputTraceFile);
            }
            catch (Exception e)
            {
                Console.WriteLine(
                    "ERROR: Could not read input trace file: {0}",
                    e.Message);
                return 1;
            }
            //Convert trace into array of lines:
            //This is only splitting with \n and the newline char:
            //string[] lines1 = inputTrace.Split(
            //        new[] { Environment.NewLine },
            //        StringSplitOptions.None
            //        );

            //Split by using both \n and \r\n as end of line symbols:
            string[] lines = inputTrace.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            //Debug:
            //Console.WriteLine("NUmber of lines when splitting the old way: {0}, new way: {1}", lines1.Count(), lines.Count());

            int traceStartLine = 0;
            //If trace file represents multiple traces generated by "pt.exe -verbose",
            //remove all traces except the the last trace in the file:
            if ((options.pTool == "ptester"))
            {
                //Find index of the last line which starts with "Execution":
                List<int> inds = new List<int>();
                for (int i = 0; i < lines.Count(); i++)
                {
                    if (lines[i].StartsWith("Execution"))
                    {
                        inds.Add(i);
                    }
                }
                if (inds.Count > 0)
                {
                    traceStartLine = inds[inds.Count - 1] + 1;
                }
                //Console.WriteLine("Trace starts at line: {0}", traceStartLine);
            }

            PTraceToMSCConverter converter = new PTraceToMSCConverter(lines, traceStartLine);
            converter.pTraceOrig = lines;
            converter.pTool = options.pTool;
            converter.shivizTraceFile = options.outputTraceFile;
            converter.interleaving = options.interleaving;
            switch (converter.pTool)
            {
                case "psharptester":
                    converter.pTrace = converter.PreProcessTrace();
                    converter.shivizTrace = converter.ConvertTrace();
                    break;
                case "ptester":
                    converter.pTrace = converter.PreProcessTrace();
                    converter.shivizTrace = converter.ConvertTrace();
                    break;
                default:
                    Console.WriteLine("ERROR: Only PSharp and PTester-generated traces are implemented");
                    PrintUsage();
                    break;
            }
            //Write ShiViz trace into specified output file:
            try
            {
                File.WriteAllText(converter.shivizTraceFile, converter.shivizTrace);
            }
            catch (Exception e)
            {
                Console.WriteLine(
                    "ERROR: Error writing ShiViz trace file: {0}",
                    e.Message);
                return 1;
            }

            return 0;
        }
    }
}


