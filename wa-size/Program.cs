using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Options;

namespace WebAssemblyInfo
{
    class Program
    {
        static int Main(string[] args)
        {
            int topN = 50;
            bool jsonOutput = false;
            bool csvOutput = false;
            string? graphJsonPath = null;
            Regex? functionFilter = null;
            int verboseLevel = 0;

            OptionSet? options = null;
            options = new OptionSet
            {
                { "n|top=", "Number of top functions to show (default: 50)", (int v) => topN = v },
                { "json", "Output as JSON", v => jsonOutput = v != null },
                { "csv", "Output as CSV", v => csvOutput = v != null },
                { "graph-json=", "Dump full call graph as JSON to FILE", v => graphJsonPath = v },
                { "f|function-filter=", "Filter functions by regex", v => functionFilter = new Regex(v) },
                { "v|verbose", "Increase verbosity", v => verboseLevel++ },
                { "h|help", "Show help", v => { ShowHelp(options!); Environment.Exit(0); } },
            };

            List<string> files;
            try
            {
                files = options.Parse(args);
            }
            catch (OptionException e)
            {
                Console.Error.WriteLine($"wa-size: {e.Message}");
                return 1;
            }

            if (files.Count < 1)
            {
                Console.Error.WriteLine("wa-size: missing wasm file argument");
                ShowHelp(options);
                return 1;
            }

            foreach (var file in files)
            {
                if (!File.Exists(file))
                {
                    Console.Error.WriteLine($"wa-size: file not found: {file}");
                    return 1;
                }

                var context = new WasmContext
                {
                    VerboseLevel = verboseLevel,
                    ShowFunctionSize = true,
                };

                if (functionFilter != null)
                    context.FunctionFilter = functionFilter;

                var reader = new WasmSizeReader(context, file);
                reader.Parse();

                var analyzer = new SizeAnalyzer(reader);
                analyzer.Analyze();

                var results = analyzer.ComputeTransitiveSizes();

                // Sort by transitive size descending
                results.Sort((a, b) => b.TransitiveSize.CompareTo(a.TransitiveSize));

                // Apply function filter to results if set
                if (functionFilter != null)
                    results = results.Where(r => r.Name != null && functionFilter.Match(r.Name).Success).ToList();

                var topResults = results.Take(topN).ToList();

                if (graphJsonPath != null)
                    WriteGraphJson(analyzer, graphJsonPath);

                if (jsonOutput)
                    WriteJson(topResults);
                else if (csvOutput)
                    WriteCsv(topResults);
                else
                    WriteTable(topResults, results.Count, results.Sum(r => (long)r.SelfSize));
            }

            return 0;
        }

        static void ShowHelp(OptionSet options)
        {
            Console.WriteLine("Usage: wa-size [OPTIONS] file.wasm");
            Console.WriteLine("Analyze WASM function sizes and transitive call graph impact.");
            Console.WriteLine();
            options.WriteOptionDescriptions(Console.Out);
        }

        static void WriteTable(List<SizeAnalyzer.FunctionSizeInfo> results, int totalFunctions, long totalCodeSize)
        {
            // Determine column widths
            int rankWidth = Math.Max(4, results.Count.ToString().Length);
            int selfWidth = Math.Max(9, totalCodeSize.ToString("N0").Length);
            int totalWidth = Math.Max(10, results.Count > 0 ? results[0].TransitiveSize.ToString("N0").Length : 10);

            Console.WriteLine(string.Format("{0}  {1}  {2}  ~  {3}  {4}  Name",
                "Rank".PadLeft(rankWidth), "Self Size".PadLeft(selfWidth),
                "Total Size".PadLeft(totalWidth), "Callees".PadLeft(7), "Callers".PadLeft(7)));
            Console.WriteLine(new string('-', rankWidth + selfWidth + totalWidth + 7 + 7 + 24));

            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                var approx = r.IsApproximate ? "~" : " ";
                Console.WriteLine(string.Format("{0}  {1}  {2}  {3}  {4}  {5}  {6}",
                    (i + 1).ToString().PadLeft(rankWidth),
                    r.SelfSize.ToString("N0").PadLeft(selfWidth),
                    r.TransitiveSize.ToString("N0").PadLeft(totalWidth),
                    approx,
                    r.DirectCallees.ToString().PadLeft(7),
                    r.DirectCallers.ToString().PadLeft(7),
                    r.Name));
            }

            Console.WriteLine();
            Console.WriteLine($"Total defined functions: {totalFunctions:N0}");
            Console.WriteLine($"Total code size: {totalCodeSize:N0} bytes");
        }

        static void WriteJson(List<SizeAnalyzer.FunctionSizeInfo> results)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[");
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                var name = EscapeJsonString(r.Name ?? "");
                var comma = i < results.Count - 1 ? "," : "";
                sb.AppendLine($"  {{\"rank\":{i + 1}, \"name\":\"{name}\", \"selfSize\":{r.SelfSize}, \"totalSize\":{r.TransitiveSize}, \"approximate\":{(r.IsApproximate ? "true" : "false")}, \"directCallees\":{r.DirectCallees}, \"directCallers\":{r.DirectCallers}, \"indirectCalls\":{r.IndirectCallCount}}}{comma}");
            }
            sb.AppendLine("]");
            Console.Write(sb.ToString());
        }

        static void WriteGraphJson(SizeAnalyzer analyzer, string path)
        {
            var (nodes, edges) = analyzer.GetGraph();
            var sb = new StringBuilder();
            sb.AppendLine("{");

            // Nodes
            sb.AppendLine("  \"nodes\": [");
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                var name = EscapeJsonString(n.Name ?? "");
                var comma = i < nodes.Count - 1 ? "," : "";
                sb.AppendLine($"    {{\"id\":{n.Id}, \"name\":\"{name}\", \"size\":{n.Size}}}{comma}");
            }
            sb.AppendLine("  ],");

            // Edges
            sb.AppendLine("  \"edges\": [");
            for (int i = 0; i < edges.Count; i++)
            {
                var e = edges[i];
                var comma = i < edges.Count - 1 ? "," : "";
                sb.AppendLine($"    {{\"from\":{e.From}, \"to\":{e.To}, \"type\":\"{e.Type}\"}}{comma}");
            }
            sb.AppendLine("  ]");

            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString());
            Console.Error.WriteLine($"Graph written to: {path}");
        }

        static void WriteCsv(List<SizeAnalyzer.FunctionSizeInfo> results)
        {
            Console.WriteLine("Rank,SelfSize,TotalSize,Approximate,DirectCallees,DirectCallers,IndirectCalls,Name");
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                var name = EscapeCsvField(r.Name ?? "");
                Console.WriteLine($"{i + 1},{r.SelfSize},{r.TransitiveSize},{r.IsApproximate},{r.DirectCallees},{r.DirectCallers},{r.IndirectCallCount},{name}");
            }
        }

        static string EscapeCsvField(string s)
        {
            if (s.Contains('"') || s.Contains(',') || s.Contains('\n') || s.Contains('\r'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return "\"" + s + "\"";
        }

        static string EscapeJsonString(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
