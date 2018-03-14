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
        //Text file with relevant machine types to leave in the trace (comma-separated):
        public string relevantMachines;
        //(TODO) Text file with relevant event names to leave in the trace (comma-separated):
        public string relevantEvents;
        //Line number in the input file to start generating the log:
        public long startLineNum;
    }
    public class PTraceToMSCCommandLine
    {
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
                        case "relmachines":
                            {
                                if (options.relevantMachines != null)
                                {
                                    PrintHelp(arg, "Only one /relMachines argument is allowed");
                                    return null;
                                }
                                if (!File.Exists(param))
                                {
                                    PrintHelp(param, "Cannot find relMachines file");
                                    return null;
                                }
                                options.relevantMachines = param;
                                break;
                            }
                        case "relevents":
                            {
                                if (options.relevantEvents != null)
                                {
                                    PrintHelp(arg, "Only one /relEvents argument is allowed");
                                    return null;
                                }
                                if (!File.Exists(param))
                                {
                                    PrintHelp(param, "Cannot find relEvents file");
                                    return null;
                                }
                                options.relevantEvents = param;
                                break;
                            }
                        case "startlinenum":
                            {
                                if (options.startLineNum != 0)
                                {
                                    PrintHelp(arg, "Only one /startLineNum argument is allowed");
                                    return null;
                                }
                                long k;
                                if (System.Int64.TryParse(param, out k))
                                    options.startLineNum = k;
                                else
                                    Console.WriteLine("/startLineNum argument could not be parsed.");
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
            Console.WriteLine("USAGE: PTraceToMSC.exe /ptool:[PSharpTester|PTester] /trace:<input trace file> /output:<output trace file> ");
            Console.WriteLine("          [-interleaving] [/relMachines:<file with machine types>] [/relEvents:<file with event names>] [/?]");
            Console.WriteLine("Compiles .txt execution trace from PSharTester.exe or pt.exe into ShiViz visualization tool log");
            Console.WriteLine("-interleaving                          generate log with only error reports and interleaving actions: ");
            Console.WriteLine("                                       machine create/halt, enqueue, dequeue");
            Console.WriteLine("/relMachines:<file with machine types> text file with comma-separated machine type names that should be left in the trace");
            Console.WriteLine("/relEvents:<file with event names>     text file with comma-separated event names that should be left in the trace");
            Console.WriteLine("/startLineNum:<int>                    used when generating only a tail of the output file (for long ShiViz logs)");
        }
        public static string RemoveNewlineSpaces(string s)
        {
            s = s.Replace("\n", String.Empty);
            s = s.Replace("\r", String.Empty);
            s = s.Replace(" ", String.Empty);
            return s;
        }
        public static List<string> GetNames(string fileName)
        {
            string names;
            try
            {
                names = File.ReadAllText(fileName);
            }
            catch (Exception e)
            {
                Console.WriteLine(
                    "ERROR: Could not read input file {0}: {1}",
                    fileName,
                    e.Message);
                return null;
            }
            names = RemoveNewlineSpaces(names);
            return (names.Split(',')).ToList(); 
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

            //Split into an array by using both \n and \r\n as end of line symbols:
            string[] lines = inputTrace.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            int traceStartLine = 0;
            //If trace file represents multiple traces generated by "pt.exe -verbose",
            //remove all traces except the the last trace in the file.
            //TODO: Implements multiple traces conversion into logs by using 
            //ShiViz tool capability for displaying multiple traces.
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
            }

            PTraceToMSCConverter converter = new PTraceToMSCConverter(lines, traceStartLine, options);
            converter.pTraceOrig = lines;
            if (!(options.relevantMachines == null))
            {
                converter.relMachTypes = GetNames(options.relevantMachines);
                //Add Runtime to the list, if it's not already there:
                if (!converter.relMachTypes.Contains("Runtime"))
                {
                    converter.relMachTypes.Add("Runtime");
                }
                Console.WriteLine("Generating log for the following machine types:");
                converter.relMachTypes.ForEach(Console.WriteLine);
            }
            
            if (!(options.relevantEvents == null))
            {
                Console.WriteLine("\"/relEvents\" option is not implemented yet");
                //converter.relEvents = GetNames(options.relevantEvents);
                //Console.WriteLine("Generating log for the following machine events:");
                //converter.relEvents.ForEach(Console.WriteLine);
            }

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


