using System.IO;

namespace WebAssemblyInfo
{
    class WasmSizeReader : WasmReader
    {
        public WasmSizeReader(WasmContext context, string path) : base(context, path) { }

        public Code[]? GetFuncsCode() => FuncsCode;
        public Function[]? GetFunctions() => functions;
        public uint GetFunctionImportsCount() => functionImportsCount;
    }
}
