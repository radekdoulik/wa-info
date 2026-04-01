using System;
using System.Collections.Generic;
using System.Linq;

namespace WebAssemblyInfo
{
    class SizeAnalyzer
    {
        readonly WasmSizeReader reader;
        readonly uint importsCount;
        readonly Code[] funcsCode;

        // function index (absolute) -> self size in bytes
        readonly Dictionary<uint, uint> functionSizes = new();

        // function index -> set of direct callee function indices (non-import only)
        readonly Dictionary<uint, HashSet<uint>> callGraph = new();

        // function index -> count of call_indirect instructions
        readonly Dictionary<uint, int> indirectCalls = new();

        public SizeAnalyzer(WasmSizeReader reader)
        {
            this.reader = reader;
            var code = reader.GetFuncsCode();
            var functions = reader.GetFunctions();
            importsCount = reader.GetFunctionImportsCount();

            if (code == null || functions == null)
                throw new InvalidOperationException("WASM file must have code and function sections");

            funcsCode = code;
        }

        public void Analyze()
        {
            BuildFunctionSizes();
            BuildCallGraph();
        }

        void BuildFunctionSizes()
        {
            for (uint i = 0; i < funcsCode.Length; i++)
            {
                uint absIdx = importsCount + i;
                functionSizes[absIdx] = funcsCode[i].Size;
            }
        }

        void BuildCallGraph()
        {
            for (uint i = 0; i < funcsCode.Length; i++)
            {
                uint absIdx = importsCount + i;
                var callees = new HashSet<uint>();
                int indirectCount = 0;

                if (!funcsCode[i].EnsureCodeReaded(reader))
                    continue;

                WalkInstructions(funcsCode[i].Instructions, inst =>
                {
                    if (inst.Opcode == Opcode.Call)
                    {
                        // Only add edges to defined functions (skip imports)
                        if (inst.Idx >= importsCount)
                            callees.Add(inst.Idx);
                    }
                    else if (inst.Opcode == Opcode.Call_Indirect)
                    {
                        indirectCount++;
                    }
                });

                callGraph[absIdx] = callees;
                if (indirectCount > 0)
                    indirectCalls[absIdx] = indirectCount;
            }
        }

        static void WalkInstructions(Instruction[] instructions, Action<Instruction> action)
        {
            if (instructions == null)
                return;

            foreach (var inst in instructions)
            {
                action(inst);

                if (inst.Block != null)
                    WalkInstructions(inst.Block, action);
                if (inst.Block2 != null)
                    WalkInstructions(inst.Block2, action);
            }
        }

        public struct FunctionSizeInfo
        {
            public uint FunctionIndex;
            public string? Name;
            public uint SelfSize;
            public long TransitiveSize;
            public bool IsApproximate;
            public int DirectCallees;
            public int DirectCallers;
            public int IndirectCallCount;
        }

        public List<FunctionSizeInfo> ComputeTransitiveSizes()
        {
            // Build reverse caller count map
            var callerCounts = new Dictionary<uint, int>();
            foreach (var entry in callGraph)
            {
                foreach (var callee in entry.Value)
                {
                    if (callerCounts.ContainsKey(callee))
                        callerCounts[callee]++;
                    else
                        callerCounts[callee] = 1;
                }
            }

            var results = new List<FunctionSizeInfo>(funcsCode.Length);

            for (uint i = 0; i < funcsCode.Length; i++)
            {
                uint absIdx = importsCount + i;
                var visited = new HashSet<uint>();
                bool isApproximate = false;

                ComputeReachable(absIdx, visited, ref isApproximate);

                long transitiveSize = 0;
                foreach (var reachable in visited)
                {
                    if (functionSizes.TryGetValue(reachable, out var size))
                        transitiveSize += size;
                }

                var callees = callGraph.TryGetValue(absIdx, out var c) ? c : null;
                var indirectCount = indirectCalls.TryGetValue(absIdx, out var ic) ? ic : 0;
                var callers = callerCounts.TryGetValue(absIdx, out var cc) ? cc : 0;

                results.Add(new FunctionSizeInfo
                {
                    FunctionIndex = absIdx,
                    Name = reader.GetFunctionName(i),
                    SelfSize = functionSizes.TryGetValue(absIdx, out var ss) ? ss : 0,
                    TransitiveSize = transitiveSize,
                    IsApproximate = isApproximate,
                    DirectCallees = callees?.Count ?? 0,
                    DirectCallers = callers,
                    IndirectCallCount = indirectCount,
                });
            }

            return results;
        }

        void ComputeReachable(uint funcIdx, HashSet<uint> visited, ref bool isApproximate)
        {
            if (!visited.Add(funcIdx))
                return;

            if (indirectCalls.ContainsKey(funcIdx))
                isApproximate = true;

            if (callGraph.TryGetValue(funcIdx, out var callees))
            {
                foreach (var callee in callees)
                    ComputeReachable(callee, visited, ref isApproximate);
            }
        }

        public (int nodeCount, int edgeCount) GetGraphStats()
        {
            int edges = 0;
            foreach (var entry in callGraph)
                edges += entry.Value.Count;
            return (functionSizes.Count, edges);
        }

        public (List<GraphNode> nodes, List<GraphEdge> edges) GetGraph()
        {
            var nodes = new List<GraphNode>(functionSizes.Count);
            foreach (var entry in functionSizes)
            {
                uint codeIdx = entry.Key - importsCount;
                nodes.Add(new GraphNode
                {
                    Id = entry.Key,
                    Name = reader.GetFunctionName(codeIdx),
                    Size = entry.Value,
                });
            }

            var edges = new List<GraphEdge>();
            foreach (var entry in callGraph)
            {
                foreach (var callee in entry.Value)
                {
                    edges.Add(new GraphEdge
                    {
                        From = entry.Key,
                        To = callee,
                        Type = "direct",
                    });
                }
            }

            return (nodes, edges);
        }

        public struct GraphNode
        {
            public uint Id;
            public string? Name;
            public uint Size;
        }

        public struct GraphEdge
        {
            public uint From;
            public uint To;
            public string Type;
        }
    }
}
