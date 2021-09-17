﻿using System;
using System.Text.RegularExpressions;
using System.IO;
using System.Collections.Generic;

using Mono.Options;

namespace WebAssemblyInfo
{
    public class Program
    {

        public static int VerboseLevel;
        static public bool Verbose { get { return VerboseLevel > 0; } }
        static public bool Verbose2 { get { return VerboseLevel > 1; } }

        static internal Regex? AssemblyFilter;
        static internal Regex? TypeFilter;
        static bool AotStats;

        readonly static Dictionary<string, AssemblyReader> assemblies = new();

        static int Main(string[] args)
        {
            var files = ProcessArguments(args);

            foreach (var file in files)
            {
                var reader = new WasmReader(file);
                reader.Parse();

                if (!AotStats)
                    continue;

                var dir = Path.GetDirectoryName(file);
                if (dir == null)
                    continue;

                foreach (var path in Directory.GetFiles(Path.Combine(dir, "managed"), "*.dll"))
                {
                    if (AssemblyFilter != null && !AssemblyFilter.Match(Path.GetFileName(path)).Success)
                        continue;

                    //Console.WriteLine($"path {path}");
                    var ar = GetAssemblyReader(path);
                    ar.GetAllMethods();
                }
            }

            return 0;
        }

        static AssemblyReader GetAssemblyReader(string path)
        {
            if (assemblies.TryGetValue(path, out AssemblyReader? reader))
                return reader;

            reader = new AssemblyReader(path);
            assemblies[path] = reader;

            return reader;
        }

        static List<string> ProcessArguments(string[] args)
        {
            var help = false;
            var options = new OptionSet {
                $"Usage: wa-info.exe OPTIONS* file.wasm [file2.wasm ...]",
                "",
                "Provides information about WebAssembly file(s)",
                "",
                "Copyright 2021 Microsoft Corporation",
                "",
                "Options:",
                { "aot-stats",
                    "Show stats about methods",
                    v => AotStats = true },
                { "assembly-filter=",
                    "Filter assemblies and process only those matching {REGEX}",
                    v => AssemblyFilter = new Regex (v) },
                { "h|help|?",
                    "Show this message and exit",
                    v => help = v != null },
                { "type-filter=",
                    "Filter types and process only those matching {REGEX}",
                    v => TypeFilter = new Regex (v) },
                { "v|verbose",
                    "Output information about progress during the run of the tool",
                    v => VerboseLevel++ },
            };

            var remaining = options.Parse(args);

            if (help || args.Length < 1)
            {
                options.WriteOptionDescriptions(Console.Out);

                Environment.Exit(0);
            }

            return remaining;
        }
    }
}