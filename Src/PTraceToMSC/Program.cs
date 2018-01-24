using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

//Element of the event queue:
//<event-expr, sender, receiver, sender-clock>:
using QueueElem = System.Tuple<string, string, string, int>;

namespace PTraceToMSC
{
    class ParsedTraceLine
    {
        public enum Kind
        {
            Send = 0,
            DroppedSend = 1,
            Dequeue = 2,
            Halt = 3,
            CreateMachine = 4,
            AtomicAction = 5,
            Comment = 6,
            SkipLine = 7,
            Other = 8,
        }
        public Kind lineKind;
        public string hostMachine;
        //TODO: use single field "otherMachine" for receiver/created/halted machine?
        public string receiverMachine;    //for "Dequeue"
        public string createdMachine;     //for "CreateMachine"
        public bool monitorCreated;       //for "CreateMachine"
        public bool hostMonitor;
        public string haltedMachine;      //for Halt and DroppedSend
        public string eventNameArgs;      //for "Send", "DroppedSend", "Dequeue"
        public int nDroppedEvts;          //for "Halt"; -1 if unknown
        public string traceMessage;
        //Available for Send in PSharp:
        //public string stateName;
    }
    class PTraceToMSCConverter
    {
        //Original trace file:
        //public string pTraceOrigFile;
        //Original trace:
        public string[] pTraceOrig;
        //index of the start line of the trace in the original file:
        int start;
        //Preprocessed trace:
        public List<ParsedTraceLine> pTrace;
        //Output ShiViz trace:
        public string shivizTrace;
        //ShiViz trace file:
        public string shivizTraceFile;
        //P tool that generated the original trace:
        public string pTool;
        //Stores last seen non-monitor host; needed for CreateLog lines (which have no host):
        private string lastNonMonitorHost;
        private List<string> monitors;
        private string mainMachineName;
        //Stores vector clock values for each machine:
        public Dictionary<string, int> vectorClocks;
        //Stors event queue for each machine:
        public Dictionary<string,Queue<QueueElem>> eventQueues;
        public PTraceToMSCConverter(string[] inputTrace, int start)
        {
            this.pTraceOrig = inputTrace;
            this.start = start;
            this.vectorClocks = new Dictionary<string, int>();
            this.eventQueues = new Dictionary<string, Queue<QueueElem>>();
            this.lastNonMonitorHost = "Runtime";
            this.monitors = new List<string>();
        }
        //For <host> group in Shiviz regexp,
        //replace quoted qualified names in PSharp with unquoted non-qualified names;
        //Also, remove instance numbers for spec machines and main machine in PTester
        //machine names in trace messages remain unchanged
        public string GetShortNameForMachine(string name)
        {
            //'FailureDetector.PSharpLanguage.Driver(0)' => Driver(0)
            string res = name.Trim('\'');

            MatchCollection matches = Regex.Matches(res, @"\.");
            if (matches.Count > 0)
            {
                var lastDot = matches[matches.Count - 1];
                int dotInd = lastDot.Index;
                //Debug:
                int length = res.Length;
                int len = res.Length - dotInd - 1;
                res = res.Substring(dotInd + 1, len);
                //Debug:
                //Console.WriteLine("Short name for {0} is {1}; last dot index is {2}; short name length is {3}", name, res, dotInd, len);
            }
            //Strip instance numbers (after "-") for main machine and spec machines for PTester:
            int pos = res.LastIndexOf("-");
            string pureName = (pos > 1) ? res.Substring(0, pos) : res;
            //Console.WriteLine("Pure name for {0} is {1}", res, pureName);
            if (pTool == "ptester" &&  (monitors.Contains(pureName) || String.Equals(pureName, mainMachineName)))
            {
                res = pureName;
                //Console.WriteLine("Stripped spec or main machine name {0}, result: {1}", res, pureName);    
            }
            
            return res;
        }
        //Convert original trace line into ShiViz trace message:
        public string origTraceToMessage(string line)
        {
            string res;
            //Remove "OUT" at the beginning of PTester trace:
            if (line.StartsWith("OUT:"))
            {
                res = line.Substring(5);
            }
            else { res = line; }
            //TODO: shorten names in the original message:
            return res;
        }
        private void UpdateLastNonMonitorHost(string curHost)
        {
            if (!(monitors.Contains(curHost)))
            {
                lastNonMonitorHost = GetShortNameForMachine(curHost);
            }
        }
        public List<ParsedTraceLine> PreProcessTrace()
        {
            List<ParsedTraceLine> res = new List<ParsedTraceLine>();

            {
                //Array of all lines of the original trace:
                List<string> lines = new List<string>();
                //Filter out irrelevant lines:
                for (int i = start; i < pTraceOrig.Count(); i++)
                {
                    if (!String.IsNullOrWhiteSpace(pTraceOrig[i]))
                    {
                        if (pTraceOrig[i].StartsWith("<") || pTraceOrig[i].StartsWith("OUT: <"))
                        {
                            lines.Add(pTraceOrig[i]);
                        }
                        else 
                        if (pTraceOrig[i].StartsWith("OUT: ERROR"))
                        {
                            //Sometimes the error message looks like this:
                            //OUT: ERROR: < Exception > Attempting to enqueue event Done more than max instance of 1\n
                            string truncated = pTraceOrig[i].Substring(5);
                            if (truncated.StartsWith("ERROR: < Exception >"))
                            {
                                string errorMes = "ERROR: " + pTraceOrig[i].Substring((pTraceOrig[i]).IndexOf(">") + 1);
                                lines.Add(errorMes);
                            }
                            else
                            {
                                lines.Add(pTraceOrig[i].Substring(5));
                            }   
                        }
                    } 
                }
                //Pattern for finding original log name <XXXLog>:
                var pattern = @"\<(.*?)\>";
                int lineNumber = -1;

                foreach (var lineOrig in lines)
                {
                    lineNumber++;
                    ParsedTraceLine resLine = new ParsedTraceLine();
                    string line = lineOrig;
                    //PSharp trace lines has dot at the end:
                    if (line[line.Length - 1] == '.')
                    {
                        line = line.Substring(0, line.Length - 1);
                    }
                    //Split the line into arrray of words:
                    string[] words = line.Split(' ');
                    //Find all machine and event ids in the line:
                    List<int> machInds = new List<int>();
                    List<int> evInds = new List<int>();
                    for (int i = 0; i < words.Length; i++)
                    {
                        if (String.Equals(words[i], "machine", StringComparison.OrdinalIgnoreCase))
                        {
                            machInds.Add(i + 1);
                        }
                        if (String.Equals(words[i], "monitor", StringComparison.OrdinalIgnoreCase))
                        {
                            //PSharp case; example: "Monitor 'Safety' with id 'FailureDetector.PSharpLanguage.Safety(1)'"
                            machInds.Add(i + 4);
                        }
                        
                        if (String.Equals(words[i], "event", StringComparison.OrdinalIgnoreCase))
                        {
                            evInds.Add(i + 1);
                        }
                    }

                    //Consider: for error dialog window do not appear in Release mode, replace Trace.Assert with Debug.Assert.
                    //Consider alt way of error reporting: throw custom exceptions or issue warnings?

                    //No more than two machines in each line:
                    Trace.Assert(machInds.Count <= 2);
                    //No more than a single event in each line:
                    Trace.Assert(evInds.Count <= 1);

                    //Find which log the line belongs to:
                    MatchCollection matches = Regex.Matches(line, pattern);

                    if (matches.Count == 0)
                    {
                        //trace line is a comment, for example:
                        //ERROR: EventSentAfterSentHaltHandled_v.p(59,23,59,29): error PC1001: Assert failed
                        //Leave such lines in the trace, but do not process them:
                        resLine.lineKind = ParsedTraceLine.Kind.Comment;
                        //resLine.hostMachine = GetHostMachineForNoHostTraceLine(lineNumber, lines, res);
                        resLine.hostMachine = lastNonMonitorHost;
                        resLine.traceMessage = line;
                    }
                    else
                    {
                        Trace.Assert(matches.Count > 0);

                        switch (matches[0].ToString().Trim('<').Trim('>'))
                        {
                            case "TestHarnessLog":
                                //PSharp runtime "Runtime" is the host:
                                resLine.lineKind = ParsedTraceLine.Kind.AtomicAction;
                                resLine.hostMachine = "Runtime";
                                resLine.traceMessage = origTraceToMessage(line);
                                break;
                            case "CreateLog":
                                //PTester examples:
                                //<CreateLog> Main machine Main was created by machine Runtime
                                //<CreateLog> Machine Node-2 was created by machine Main-1
                                //<CreateLog> Spec Machine Safety was created by machine Runtime
                                //PSharp examples:
                                //1. <CreateLog> Machine 'FailureDetector.PSharpLanguage.Node(5)' was created by machine 'FailureDetector.PSharpLanguage.Driver(3)'.
                                //2. <CreateLog> Machine 'FailureDetector.PSharpLanguage.Driver(3)' was created by the Runtime.
                                //For monitors, default host is "Runtime":
                                //3. <CreateLog> Monitor 'Safety' with id 'FailureDetector.PSharpLanguage.Safety(1)' was created.
                                Trace.Assert(machInds.Count > 0);
                                resLine.lineKind = ParsedTraceLine.Kind.CreateMachine;
                                resLine.createdMachine = GetShortNameForMachine(words[machInds[0]]);
                                if (words.Contains("Monitor") || words.Contains("Spec"))
                                {
                                    resLine.monitorCreated = true;
                                    monitors.Add(resLine.createdMachine);
                                    resLine.hostMachine = "Runtime";
                                }
                                else
                                {
                                    if (words.Contains("Main"))
                                    {
                                        resLine.monitorCreated = false;
                                        mainMachineName = resLine.createdMachine;
                                        resLine.hostMachine = "Runtime";
                                    }
                                    else
                                    {
                                        //Non-monitor, non-main machine created:
                                        resLine.monitorCreated = false;
                                        if (machInds.Count == 2)
                                        {
                                            resLine.hostMachine = GetShortNameForMachine(words[machInds[1]]);
                                        }
                                        else
                                        {
                                            resLine.hostMachine = "Runtime";
                                        }
                                    }

                                }
                                resLine.traceMessage = origTraceToMessage(line);
                                break;
                            case "SendLog":
                                Trace.Assert(machInds.Count == 2);
                                Trace.Assert(evInds.Count == 1);
                                resLine.lineKind = ParsedTraceLine.Kind.Send;
                                //PTester:
                                //"<EnqueueLog> Enqueued Event <unit, null> in machine Main-1 by machine Main-1"
                                //PSharp:
                                //"<SendLog> Operation Group 00000000-0000-0000-0000-000000000000: Machine 'CacheCoherence.PSharpLanguage.Host(0)' 
                                //in state 'CacheCoherence.PSharpLanguage.Host.Init' sent event 'CacheCoherence.PSharpLanguage.clientConfig' 
                                //to machine 'CacheCoherence.PSharpLanguage.Client(1)' in state 'CacheCoherence.PSharpLanguage.Client.Init'.
                                if (pTool == "ptester")
                                {
                                    resLine.hostMachine = GetShortNameForMachine(words[machInds[1]]);
                                    resLine.receiverMachine = GetShortNameForMachine(words[machInds[0]]);
                                }
                                if (pTool == "psharptester")
                                {
                                    resLine.hostMachine = GetShortNameForMachine(words[machInds[0]]);
                                    resLine.receiverMachine = GetShortNameForMachine(words[machInds[1]]);
                                }
                                resLine.eventNameArgs = words[evInds[0]];
                                resLine.traceMessage = origTraceToMessage(line);
                                break;
                            case "DequeueLog":
                                Trace.Assert(machInds.Count == 1);
                                Trace.Assert(evInds.Count == 1);
                                resLine.lineKind = ParsedTraceLine.Kind.Dequeue;
                                //PTester:
                                //<DequeueLog> Dequeued Event <unit, null> at Machine Main-1
                                //PSharp:
                                //<DequeueLog> Machine 'CacheCoherence.PSharpLanguage.Client(1)' in state 'CacheCoherence.PSharpLanguage.Client.Init' 
                                //dequeued event 'CacheCoherence.PSharpLanguage.clientConfig'.
                                resLine.hostMachine = GetShortNameForMachine(words[machInds[0]]);
                                resLine.eventNameArgs = words[evInds[0]];
                                resLine.traceMessage = origTraceToMessage(line);
                                break;
                            case "EnqueueLog":
                                Trace.Assert(evInds.Count == 1);
                                //skipped in psharptester, interpreted as "send" or "dropped send" in ptester:
                                if (pTool == "ptester")
                                {
                                    if (!(line.Contains("halted")))
                                    {
                                        Trace.Assert(machInds.Count == 2);
                                        //OUT: <EnqueueLog> Enqueued Event <unit,null> in machine Client-2 by machine Main-1
                                        resLine.lineKind = ParsedTraceLine.Kind.Send;
                                        resLine.hostMachine = GetShortNameForMachine(words[machInds[1]]);
                                        resLine.receiverMachine = GetShortNameForMachine(words[machInds[0]]);
                                        resLine.eventNameArgs = words[evInds[0]];
                                        resLine.traceMessage = origTraceToMessage(line);
                                    }
                                    else
                                    {
                                        Trace.Assert(machInds.Count == 1);
                                        //PTester: if the receiver machine is stopped, do not enqueue the event;
                                        //"<EnqueueLog> Machine {0}-{1} has been halted and Event {2} is dropped"
                                        //- here, "source" machine ID is present ("by machine")
                                        resLine.lineKind = ParsedTraceLine.Kind.DroppedSend;

                                        resLine.hostMachine = GetShortNameForMachine(words[machInds[0]]);
                                        resLine.haltedMachine = resLine.hostMachine;
                                        resLine.nDroppedEvts = 1;
                                        resLine.eventNameArgs = words[evInds[0]];
                                        resLine.traceMessage = origTraceToMessage(line);
                                    }
                                }
                                else
                                {
                                    resLine.lineKind = ParsedTraceLine.Kind.SkipLine;
                                }
                                break;
                            case "AnnounceLog":
                                //PTester: event sent from Runtime to a spec machine, for example:
                                //"<AnnounceLog> Enqueued Event <M_PONG, Node(1)> to Spec Machine Safety"
                                Trace.Assert(machInds.Count == 1);
                                resLine.lineKind = ParsedTraceLine.Kind.Send;
                                resLine.hostMachine = "Runtime";
                                resLine.receiverMachine = GetShortNameForMachine(words[machInds[0]]);
                                resLine.eventNameArgs = words[evInds[0]];
                                resLine.traceMessage = origTraceToMessage(line);
                                break;
                            case "HaltLog":
                                Trace.Assert(machInds.Count == 1);
                                resLine.lineKind = ParsedTraceLine.Kind.Halt;
                                //Halt happens when "halt" event is dequeued:
                                //PTester:
                                //"<HaltLog> Machine Main-0 halted with {2} events in the queue"  or
                                //"<HaltLog> Machine Main-0 HALTED"
                                //PSharp:
                                //<HaltLog> Machine 'FailureDetector.PSharpLanguage.Node(1)' halted with '0' events in its inbox.
                                resLine.hostMachine = GetShortNameForMachine(words[machInds[0]]);
                                resLine.haltedMachine = resLine.hostMachine;
                                resLine.traceMessage = origTraceToMessage(line);
                                for (int i = 0; i < words.Length; i++)
                                {
                                    if (words[i] == "with")
                                    {
                                        int x = 0;
                                        //Remove quotes (if any) around the string representing number of dropped events:
                                        string num = words[i + 1].Trim('\'');
                                        if (Int32.TryParse(num, out x))
                                        {
                                            resLine.nDroppedEvts = x;
                                        }
                                        else
                                        {
                                            Console.WriteLine("ERROR: number of dropped events {0} is not an int in line: {1}", num, line);
                                            Trace.Assert(false);
                                        }
                                    }
                                }
                                break;
                            //The following lines always belong to Runtime:
                            //TODO: verify this (for PSharp):
                            case "StrategyLog":
                            case "ErrorLog":
                            case "RandomLog":
                            case "ScheduleDebug":
                            case "ChordLog":
                            case "DelayLog":
                            case "IterativeDeepeningDFSLog":
                            case "PCTLog":
                                resLine.lineKind = ParsedTraceLine.Kind.AtomicAction;
                                Trace.Assert(machInds.Count == 0 || machInds.Count == 1);
                                resLine.hostMachine = "Runtime";
                                resLine.traceMessage = origTraceToMessage(line);
                                break;
                            case "StateLog":
                            case "ActionLog":
                            case "RaiseLog":
                            case "PushLog":
                            case "PopLog":
                            case "GotoLog":
                            case "DefaultLog":
                            case "NullTransLog":
                            case "NullActionLog":
                            case "FunctionLog": 
                            case "MonitorLog":
                            case "ReceiveLog":
                            case "ExitLog":
                                resLine.lineKind = ParsedTraceLine.Kind.AtomicAction;
                                Trace.Assert(machInds.Count == 0 || machInds.Count == 1);
                                if (machInds.Count == 1)
                                {
                                    resLine.hostMachine = GetShortNameForMachine(words[machInds[0]]);
                                }
                                else
                                {
                                    resLine.hostMachine = lastNonMonitorHost;
                                }
                                if (matches[0].ToString().Trim('<').Trim('>') == "MonitorLog")
                                {
                                    resLine.hostMonitor = true;
                                }
                                resLine.traceMessage = origTraceToMessage(line);
                                break;
                            default:
                                //After debugging is finished, such lines shoudl be ignored in ShiViz;
                                resLine.lineKind = ParsedTraceLine.Kind.Other;
                                resLine.traceMessage = origTraceToMessage(line);
                                Console.WriteLine("Unexpected: Next line's kind is {0}: implement this line kind or ignore it", matches[0].ToString());
                                //Trace.Assert(false);
                                break;
                        }
                        if (resLine.hostMachine != null)
                        {
                            UpdateLastNonMonitorHost(resLine.hostMachine);
                        }
                        
                    }


                    //Debug: print members of resLine:
                    //Console.WriteLine("Parsed line {0}", line);
                    //Console.WriteLine("lineKind: {0}", resLine.lineKind);
                    //Console.WriteLine("hostMachine: {0}", resLine.hostMachine);
                    //Console.WriteLine("receiverMachine: {0}", resLine.receiverMachine);
                    //Console.WriteLine("createdMachine: {0}", resLine.createdMachine);
                    //Console.WriteLine("monitorCreated: {0}", resLine.monitorCreated);
                    //Console.WriteLine("hostMonitor: {0}", resLine.hostMonitor);
                    //Console.WriteLine("haltedMachine: {0}", resLine.haltedMachine);
                    //Console.WriteLine("eventNameArgs: {0}", resLine.eventNameArgs);
                    //Console.WriteLine("nDroppedEvts: {0}", resLine.nDroppedEvts);
                    //End of Debug

                    res.Add(resLine);
                }

                //Debug:
                //Console.WriteLine("All monitors in trace:");
                //foreach (var monitor in monitors)
                //{
                //    Console.WriteLine("Monitor: {0}", monitor);
                //}
                //Console.WriteLine("Main machine in trace: {0}", mainMachineName);
                //End of Debug

                return res;
            }
        }
        //Generate ShiViz trace line with one machine:
        public string GenerateOutLine1(string hostMachine, int clockHost, string message)
        {
            return hostMachine + " { " + "\"" + hostMachine + "\": " + clockHost + " }   " + message;
        }
        //Generate ShiViz trace line with two machines:
        public string GenerateOutLine2(string hostMachine, string firstMach, int firstClock, 
            string secondMach, int secondClock, string message)
        {
            return hostMachine + " { " + "\"" + firstMach + "\": " + firstClock + ", " +
                                         "\"" + secondMach + "\": " + secondClock + " }   " + message;
        }
        //Find queue element for an event with given event arguments and receiver and return it;
        //remove the queue element from the queue:
        public QueueElem GetRemoveQueueElem(string eventArgs, string receiver)
        {
            //make a deep copy of the receiver machine queue:
            Queue<QueueElem> queue = new Queue<QueueElem>(eventQueues[receiver]);
            while (queue.Count != 0)
            {
                var res = queue.Dequeue();
                if ((res.Item1).Equals(eventArgs) && (res.Item3).Equals(receiver))
                {
                    List<QueueElem> oldQueue = (eventQueues[receiver]).ToList();
                    foreach (var elem in oldQueue)
                    {
                        if (res.Equals(elem))
                        {
                            //removes the first occurrence of the element:
                            oldQueue.Remove(elem);
                            break;
                        }
                    }
                    Queue<QueueElem> newQueue = new Queue<QueueElem>(oldQueue);
                    eventQueues[receiver] = newQueue;

                    return res;
                }
            }
            Console.WriteLine("ERROR: Queue element for event {0} and receiver {1} not found in reciever's queue", eventArgs, receiver);
            Trace.Assert(false);
            return null; 
        }
        public int GetVectorClock(string machine, ParsedTraceLine traceLine)
        {
            int result;
            if (!vectorClocks.TryGetValue(machine, out result))
            {
                //Create vectorClock entry for the host machine of the 1st line of the trace:
                if ((pTrace.First()).Equals(traceLine))
                {
                    vectorClocks.Add(machine, 0);
                    //Initialize eventQueue for the host machine:
                    eventQueues.Add(machine, new Queue<QueueElem>());
                }
                else
                {
                    //This happened due to a bug in PSharp tracing, example:FailureDetector:
                    //<PushLog> Machine '0' pushed from state '1' to state '2'.
                    //Give warning instead of throwing an exception and initialize the clock vector for the machine
                    //throw new Exception("ERROR: no entry exists in vectorClock for host machine: " + machine);
                    Console.WriteLine("Warning: machine {0} has not been created, trace line: {1}", machine, traceLine.traceMessage);
                    vectorClocks.Add(machine, 0);
                    //Initialize eventQueue for the host machine:
                    eventQueues.Add(machine, new Queue<QueueElem>());
                }
            }

            return result;
        }
        public string ConvertTrace()
        {
            StringBuilder sb = new StringBuilder();
            StringWriter sw = new StringWriter(sb);

            //Add regular expression line and blank line in front of ZhiViz trace:
            string regExp = "(?<host>\\S+) (?<clock>\\{.*\\}) (?<event>.*)";
            sw.WriteLine(regExp);
            sw.WriteLine("");

            foreach (var traceLine in this.pTrace)
            {
                string resLine;
                int hostClock = -1;
                if (traceLine.lineKind != ParsedTraceLine.Kind.SkipLine)
                {
                    hostClock = GetVectorClock(traceLine.hostMachine, traceLine);
                }

                switch (traceLine.lineKind)
                {
                    case ParsedTraceLine.Kind.CreateMachine:
                        resLine = GenerateOutLine1(traceLine.hostMachine, hostClock + 1, traceLine.traceMessage);
                        sw.WriteLine(resLine);
                        //Create new trace line for the (fictitious) start state of the created machine:
                        int temp1;
                        if (!vectorClocks.TryGetValue(traceLine.createdMachine, out temp1))
                        {
                            vectorClocks.Add(traceLine.createdMachine, 0);
                        }
                        else
                        {
                            throw new Exception("ERROR: vectorClock entry: " + temp1 + " already exists for newly created machine: " + traceLine.createdMachine);
                        }
                        //Initialize eventQueue for the created machine:
                        eventQueues.Add(traceLine.createdMachine, new Queue<QueueElem>());
                        string monitorOrMachine = (traceLine.monitorCreated) 
                            ? 
                                ((pTool == "psharp") ? "Monitor " : "Spec ")
                            : "Machine ";
                        string message = monitorOrMachine + traceLine.createdMachine + " entering start state";
                        string newLine = GenerateOutLine2(traceLine.createdMachine, traceLine.hostMachine,
                            hostClock +1, traceLine.createdMachine, 1, message);
                        sw.WriteLine(newLine);
                        //Update vectorClock for both machines:
                        vectorClocks[traceLine.hostMachine] = hostClock + 1;
                        vectorClocks[traceLine.createdMachine] = 1;
                        break;
                    case ParsedTraceLine.Kind.Send:
                        resLine = GenerateOutLine1(traceLine.hostMachine, hostClock + 1, traceLine.traceMessage);
                        sw.WriteLine(resLine);
                        //Add element to the receiver machine's event queue:
                        Queue<QueueElem> recQueue;
                        if (!eventQueues.TryGetValue(traceLine.receiverMachine, out recQueue))
                        {
                            throw new Exception("ERROR: eventQueue entry does not exist for receiver machine: " + traceLine.receiverMachine);
                        }
                        //Tuple<event expr, sender, receiver, sender clock>
                        var elem = new Tuple<string, string, string, int>
                            (traceLine.eventNameArgs, traceLine.hostMachine, traceLine.receiverMachine, hostClock + 1);
                        recQueue.Enqueue(elem);
                        eventQueues[traceLine.receiverMachine] = recQueue;
                        vectorClocks[traceLine.hostMachine] = hostClock + 1;
                        break;
                    case ParsedTraceLine.Kind.Dequeue:
                        var queueElem = GetRemoveQueueElem(traceLine.eventNameArgs, traceLine.hostMachine);
                        if (queueElem == null)
                        {
                            throw new Exception("ERROR: eventQueue entry does not exist for receiver machine: " + traceLine.hostMachine +
                                "for the event " + traceLine.eventNameArgs + ";\n trace line:" + traceLine.traceMessage);

                        }
                        //Tuple<event expr, sender, receiver, sender clock>
                        resLine = GenerateOutLine2(traceLine.hostMachine, queueElem.Item2, queueElem.Item4,
                            traceLine.hostMachine, hostClock + 1, traceLine.traceMessage);
                        sw.WriteLine(resLine);
                        vectorClocks[traceLine.hostMachine] = hostClock + 1;
                        break;
                    case ParsedTraceLine.Kind.DroppedSend:
                        //TODO: do we want to connect dropped send action with the corresp. halt action
                        //(if the halt action happened in a diff machine only)
                        //Treated as atomic action for now:
                        resLine = GenerateOutLine1(traceLine.hostMachine, hostClock + 1, traceLine.traceMessage);
                        sw.WriteLine(resLine);
                        vectorClocks[traceLine.hostMachine] = hostClock + 1;
                        break;
                    case ParsedTraceLine.Kind.Halt:
                        //Halt happens when "halt" event is dequeued:
                        //PTester:
                        //"<HaltLog> Machine Main-0 halted with {2} events in the queue"  or
                        //"<HaltLog> Machine Main-0 HALTED"
                        //PSharp:
                        //<HaltLog> Machine 'FailureDetector.PSharpLanguage.Node(1)' halted with '0' events in its inbox.
                        resLine = GenerateOutLine1(traceLine.hostMachine, hostClock + 1, traceLine.traceMessage);
                        sw.WriteLine(resLine);
                        vectorClocks[traceLine.hostMachine]++;
                        //Generate "nDroppedEvt" lines in the trace, each connected to the corresp. Send in the sender machine;
                        //leave the same message, but copy sender and the clock from each event in the queue:
                        Trace.Assert(traceLine.nDroppedEvts == eventQueues[traceLine.haltedMachine].Count());
                        foreach (QueueElem evtInQueue in eventQueues[traceLine.haltedMachine])
                        {
                            //Tuple<event expr, sender, receiver, sender clock>
                            resLine = GenerateOutLine2(traceLine.haltedMachine, evtInQueue.Item2, evtInQueue.Item4,
                                traceLine.haltedMachine, vectorClocks[traceLine.haltedMachine] + 1, 
                                "Machine " + traceLine.haltedMachine + " halted and sent event " + evtInQueue.Item1 + " is dropped");
                            sw.WriteLine(resLine);
                            vectorClocks[traceLine.haltedMachine]++;
                        }
                        break;
                    case ParsedTraceLine.Kind.AtomicAction:
                        resLine = GenerateOutLine1(traceLine.hostMachine, hostClock + 1, traceLine.traceMessage);
                        sw.WriteLine(resLine);
                        vectorClocks[traceLine.hostMachine] = hostClock + 1;
                        break;
                    case ParsedTraceLine.Kind.Comment:
                        resLine = GenerateOutLine1(traceLine.hostMachine, hostClock + 1, traceLine.traceMessage);
                        sw.WriteLine(resLine);
                        vectorClocks[traceLine.hostMachine] = hostClock + 1;
                        break;
                    case ParsedTraceLine.Kind.SkipLine:
                        resLine = "";
                        sw.WriteLine(resLine);
                        break;
                    case ParsedTraceLine.Kind.Other:
                        resLine = traceLine.traceMessage;
                        sw.WriteLine(resLine);
                        break;
                }               
            }

            sw.Flush();
            sw.Close();

            return sw.ToString();

        }
    }
}
