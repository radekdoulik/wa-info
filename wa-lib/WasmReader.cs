﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using NameMap = System.Collections.Generic.Dictionary<System.UInt32, string>;

namespace WebAssemblyInfo
{
    public partial class WasmReader : WasmReaderBase
    {
        public List<WasmReader> ModuleReaders = new();

        public WasmReader(WasmContext context, string path) : base(context, path)
        {
        }

        public WasmReader(WasmContext context, Stream stream, long length) : base(context, stream, length)
        {
        }

        protected virtual WasmReader CreateEmbeddedReader(WasmContext context, Stream stream, long length)
        {
            return new WasmReader(context, stream, length);
        }

        protected override void PostParse()
        {
            base.PostParse();
            if (functionNames == null || functionNames.Count != 0)
                return;

            var dir = System.IO.Path.GetDirectoryName(Path);
            if (dir == null)
                return;

            var symPath = System.IO.Path.Combine(dir, $"{System.IO.Path.GetFileNameWithoutExtension(Path)}.js.symbols");
            if (!File.Exists(symPath))
                return;

            if (Context.Verbose)
                Console.WriteLine($"Reading function names from: {symPath}");

            var lines = File.ReadAllLines(symPath);
            foreach (var line in lines)
            {
                var idx = line.IndexOf(':');
                if (idx < 0 || !uint.TryParse(line.AsSpan(0, idx), out var fIdx))
                    continue;

                var name = line[(idx + 1)..];
                if (string.IsNullOrEmpty(name))
                    continue;

                if (functionNames.ContainsKey(fIdx))
                {
                    Console.WriteLine($"warning: duplicit function index: {fIdx}");
                    continue;
                }

                functionNames[fIdx] = name;
                nameToFunction[name] = fIdx;
            }
        }

        override protected void ReadSection(SectionInfo section)
        {
            switch (section.id)
            {
                case SectionId.Custom:
                    ReadCustomSection(section.size);
                    break;
                case SectionId.Type:
                    ReadTypeSection();
                    break;
                case SectionId.Function:
                    ReadFunctionSection();
                    break;
                case SectionId.Table:
                    ReadTableSection();
                    break;
                case SectionId.Export:
                    ReadExportSection();
                    break;
                case SectionId.Import:
                    ReadImportSection();
                    break;
                case SectionId.Element:
                    ReadElementSection();
                    break;
                case SectionId.Code:
                    if (Context.AotStats || Context.Disassemble || Context.ShowFunctionSize)
                        ReadCodeSection();
                    break;
                case SectionId.Data:
                    ReadDataSection();
                    break;
                case SectionId.Global:
                    ReadGlobalSection();
                    break;
                case SectionId.Memory:
                    ReadMemorySection();
                    break;
                case SectionId.WitCoreModule:
                    InWitComponent = false;
                    EndOfModulePositions.Push(Reader.BaseStream.Position + section.size);

                    var reader = CreateEmbeddedReader(Context, Reader.BaseStream, section.size);
                    reader.ReadModule();
                    ModuleReaders.Add(reader);
                    EndOfModulePositions.Pop();
                    InWitComponent = true;
                    break;
                case SectionId.WitImport:
                    ReadWitImportSection();
                    break;
                case SectionId.WitExport:
                    ReadWitExportSection();
                    break;
                default:
                    break;
            }
        }

        (uint, uint) ReadLimits()
        {
            var limitsType = Reader.ReadByte();
            var min = ReadU32();
            var max = limitsType == 1 ? ReadU32() : UInt32.MaxValue;

            return (min, max);
        }

        void ReadTableType(ref TableType table)
        {
            table.RefType = (ReferenceType)Reader.ReadByte();
            (table.Min, table.Max) = ReadLimits();
        }

        TableType[]? tables;
        void ReadTableSection()
        {
            UInt32 count = ReadU32();

            if (Context.Verbose)
                Console.Write($" count: {count}");

            if (Context.Verbose2)
                Console.WriteLine();

            tables = new TableType[count];
            for (uint i = 0; i < count; i++)
            {
                ReadTableType(ref tables[i]);

                if (Context.Verbose2)
                    Console.WriteLine($"  table: {i} reftype: {tables[i].RefType} limits: {tables[i].Min}, {tables[i].Max}");
            }
        }

        Element[]? elements;
        void ReadElementSection()
        {
            UInt32 count = ReadU32();

            if (Context.Verbose)
                Console.Write($" count: {count}");

            if (Context.Verbose2)
                Console.WriteLine();

            elements = new Element[count];
            for (uint i = 0; i < count; i++)
            {
                elements[i].Flags = (ElementFlag)Reader.ReadByte();
                if (Context.Verbose2)
                    Console.WriteLine($"  element: {i} flags: {elements[i].Flags}");

                if (elements[i].HasTableIdx)
                    elements[i].TableIdx = ReadU32();

                if (elements[i].HasExpression)
                {
                    (elements[i].Expression, _) = ReadBlock();
                    if (Context.Verbose2)
                    {
                        Console.WriteLine("  expression:");
                        foreach (var instruction in elements[i].Expression)
                            Console.WriteLine(instruction.ToString(this).Indent("    "));
                    }
                }

                if (elements[i].HasExpressions)
                {
                    if (elements[i].HasRefType)
                        elements[i].RefType = (ReferenceType)Reader.ReadByte();

                    var size = ReadU32();
                    elements[i].Expressions = new Instruction[size][];
                    for (uint j = 0; j < size; j++)
                    {
                        (elements[i].Expressions[j], _) = ReadBlock();
                    }
                }
                else
                {
                    if (elements[i].HasElemKind)
                        elements[i].Kind = Reader.ReadByte();

                    var size = ReadU32();
                    if (Context.Verbose2)
                        Console.WriteLine($"  size: {size}");

                    elements[i].Indices = new UInt32[size];
                    for (uint j = 0; j < size; j++)
                    {
                        elements[i].Indices[j] = ReadU32();

                        if (Context.Verbose2)
                            Console.WriteLine($"    idx[{j}] = {elements[i].Indices[j]}");
                    }
                }
            }
        }

        Data[]? dataSegments;
        void ReadDataSection()
        {
            var count = ReadU32();

            if (Context.Verbose)
                Console.Write($" count: {count}");

            if (Context.Verbose2)
                Console.WriteLine();

            dataSegments = new Data[count];
            for (uint i = 0; i < count; i++)
            {
                dataSegments[i].Mode = (DataMode)ReadU32();
                if (Context.Verbose2)
                    Console.Write($"  data idx: {i} mode: {dataSegments[i].Mode}");
                switch (dataSegments[i].Mode)
                {
                    case DataMode.ActiveMemory:
                        dataSegments[i].MemIdx = ReadU32();
                        if (Context.Verbose2)
                            Console.Write($" memory index: {dataSegments[i].MemIdx}");
                        goto case DataMode.Active;
                    case DataMode.Active:
                        (dataSegments[i].Expression, _) = ReadBlock();
                        if (Context.Verbose2)
                        {
                            Console.Write(" offset expression:");
                            if (dataSegments[i].Expression.Length == 1)
                            {
                                Console.Write($" {dataSegments[i].Expression[0]}");
                            }
                            else
                            {
                                Console.WriteLine();
                                foreach (var instruction in dataSegments[i].Expression)
                                    Console.Write(instruction.ToString(this).Indent("    "));
                            }
                        }
                        break;
                }

                var length = ReadU32();
                if (Context.Verbose2)
                    Console.WriteLine($" length: {length}");

                dataSegments[i].Content = Reader.ReadBytes((int)length);
            }
        }

        void ReadGlobal(ref Global global)
        {
            ReadValueType(ref global.Type);
            global.Mutability = (Mutability)Reader.ReadByte();
        }

        Global[]? globals;
        void ReadGlobalSection()
        {
            var count = ReadU32();

            if (Context.Verbose)
                Console.Write($" count: {count}");

            if (Context.Verbose2)
                Console.WriteLine();

            globals = new Global[count];
            for (uint i = 0; i < count; i++)
            {
                if (Context.Verbose2)
                    Console.Write($"  global idx: {i}");

                ReadGlobal(ref globals[i]);

                if (Context.Verbose2)
                    Console.Write($" type: {globals[i]}");

                (globals[i].Expression, _) = ReadBlock();

                if (Context.Verbose2)
                {
                    if (globals[i].Expression.Length == 1)
                    {
                        Console.Write($" init expression: {globals[i].Expression[0]}");
                    }
                    else
                    {
                        Console.WriteLine(" init expression:");
                        foreach (var instruction in globals[i].Expression)
                            Console.Write(instruction.ToString(this).Indent("    "));
                    }
                }

                if (Context.Verbose2)
                    Console.WriteLine();
            }
        }

        Memory[]? memories;
        void ReadMemorySection()
        {
            var count = ReadU32();

            if (Context.Verbose)
                Console.Write($" count: {count}");

            if (Context.Verbose2)
                Console.WriteLine();

            memories = new Memory[count];
            for (uint i = 0; i < count; i++)
            {
                (memories[i].Min, memories[i].Max) = ReadLimits();

                if (Context.Verbose2)
                    Console.Write($"  memory: {i} limits: {memories[i].Min}, {memories[i].Max}");

                if (Context.Verbose2)
                    Console.WriteLine();
            }
        }

        protected Code[]? FuncsCode;
        public long FuncMetadataSize;
        void ReadCodeSection()
        {
            var start = Reader.BaseStream.Position;
            UInt32 count = ReadU32();

            if (Context.Verbose)
                Console.Write($" count: {count}");

            if (Context.Verbose2)
                Console.WriteLine();

            FuncsCode = new Code[count];
            FuncMetadataSize += Reader.BaseStream.Position - start;

            for (uint i = 0; i < count; i++)
            {
                start = Reader.BaseStream.Position;
                FuncsCode[i].Idx = i;
                FuncsCode[i].Size = ReadU32();
                FuncsCode[i].Offset = Reader.BaseStream.Position;
                FuncMetadataSize += FuncsCode[i].Offset - start;
                Reader.BaseStream.Seek(FuncsCode[i].Offset + FuncsCode[i].Size, SeekOrigin.Begin);
            }
        }

        BlockType ReadBlockType()
        {
            BlockType blockType = new();
            byte b = Reader.ReadByte();

            switch (b)
            {
                case 0x40:
                    blockType.Kind = BlockTypeKind.Empty;
                    break;
                case (byte)NumberType.f32:
                case (byte)NumberType.i32:
                case (byte)NumberType.f64:
                case (byte)NumberType.i64:
                case (byte)ReferenceType.ExternRef:
                case (byte)ReferenceType.FuncRef:
                    blockType.Kind = BlockTypeKind.ValueType;
                    Reader.BaseStream.Seek(-1, SeekOrigin.Current);
                    ReadValueType(ref blockType.ValueType);
                    break;
                default:
                    blockType.Kind = BlockTypeKind.TypeIdx;
                    Reader.BaseStream.Seek(-1, SeekOrigin.Current);
                    blockType.TypeIdx = (UInt32)ReadI64();
                    break;
            }

            return blockType;
        }

        MemArg ReadMemArg()
        {
            MemArg ma = new();

            ma.Align = ReadU32();
            ma.Offset = ReadU32();

            return ma;
        }

        public void DumpBytes(uint count, bool newline = false)
        {
            var pos = Reader.BaseStream.Position;
            if (newline)
                Console.WriteLine();

            Console.WriteLine("bytes");

            byte[] last = new byte[16];

            for (uint i = 0; i < count; i++)
            {
                var idx = i % 16;
                last[idx] = Reader.ReadByte();
                Console.Write($" {last[idx],2:x}");

                if (idx == 15 || i == count - 1) {
                    if (i == count -1) {
                        for (var j = i % 16; j < 15; j++) {
                            Console.Write("   ");
                            last[j+1] = (byte)' ';
                        }
                    }

                    Console.Write(" |");

                    foreach (var b in last)
                        Console.Write(b >= 0x20 && b < 0x7f ? (char)b : '.');

                    Console.WriteLine("|");
                }
            }

            Console.WriteLine();
            Reader.BaseStream.Position = pos;
        }

        Instruction ReadInstruction(Opcode opcode)
        {
            Instruction instruction = new() { Offset = Reader.BaseStream.Position - 1 };
            instruction.Opcode = opcode;
            Opcode op;

            // Console.WriteLine($"read opcode: 0x{opcode:x} {opcode}");
            switch (opcode)
            {
                case Opcode.Block:
                case Opcode.Loop:
                case Opcode.If:
                case Opcode.Try:
                    // DumpBytes(64);
                    instruction.BlockType = ReadBlockType();

                    // Console.WriteLine($"blocktype: {instruction.BlockType.Kind}");
                    var end = opcode switch
                    {
                        Opcode.If => Opcode.Else,
                        Opcode.Try => Opcode.Delegate,
                        _ => Opcode.End,
                    };
                    (instruction.Block, op) = ReadBlock(end);

                    if (op == Opcode.Else)
                        (instruction.Block2, _) = ReadBlock();
                    else if (op == Opcode.Delegate)
                    {
                        instruction.TryDelegate = true;
                        instruction.Idx = ReadU32();
                    }
                    break;
                case Opcode.Catch:
                case Opcode.Catch_All:
                    if (opcode != Opcode.Catch_All)
                    {
                        instruction.I32 = ReadI32();
                    }
                    break;
                case Opcode.Throw:
                    instruction.I32 = ReadI32();
                    break;
                case Opcode.Rethrow:
                    instruction.Idx = ReadU32();
                    break;
                case Opcode.Memory_Size:
                case Opcode.Memory_Grow:
                    op = (Opcode)Reader.ReadByte();
                    if (op != Opcode.Unreachable)
                        throw new Exception($"0x00 expected after opcode: {opcode}, got {op} instead");
                    break;
                case Opcode.Call:
                case Opcode.Local_Get:
                case Opcode.Local_Set:
                case Opcode.Local_Tee:
                case Opcode.Global_Get:
                case Opcode.Global_Set:
                case Opcode.Br:
                case Opcode.Br_If:
                    instruction.Idx = ReadU32();
                    break;
                case Opcode.Br_Table:
                    var count = ReadU32();

                    instruction.IdxArray = new UInt32[count];

                    for (var i = 0; i < count; i++)
                        instruction.IdxArray[i] = ReadU32();

                    instruction.Idx = ReadU32();

                    break;
                case Opcode.Call_Indirect:
                    instruction.Idx = ReadU32();
                    instruction.Idx2 = ReadU32();
                    break;
                case Opcode.I32_Const:
                    instruction.I32 = ReadI32();
                    break;
                case Opcode.F32_Const:
                    instruction.F32 = Reader.ReadSingle();
                    break;
                case Opcode.I64_Const:
                    instruction.I64 = ReadI64();
                    break;
                case Opcode.F64_Const:
                    instruction.F64 = Reader.ReadDouble();
                    break;
                case Opcode.I32_Load:
                case Opcode.I64_Load:
                case Opcode.F32_Load:
                case Opcode.F64_Load:
                case Opcode.I32_Load8_S:
                case Opcode.I32_Load8_U:
                case Opcode.I32_Load16_S:
                case Opcode.I32_Load16_U:
                case Opcode.I64_Load8_S:
                case Opcode.I64_Load8_U:
                case Opcode.I64_Load16_S:
                case Opcode.I64_Load16_U:
                case Opcode.I64_Load32_S:
                case Opcode.I64_Load32_U:
                case Opcode.I32_Store:
                case Opcode.I64_Store:
                case Opcode.F32_Store:
                case Opcode.F64_Store:
                case Opcode.I32_Store8:
                case Opcode.I32_Store16:
                case Opcode.I64_Store8:
                case Opcode.I64_Store16:
                case Opcode.I64_Store32:
                    instruction.MemArg = ReadMemArg();
                    break;
                case Opcode.Unreachable:
                case Opcode.Nop:
                case Opcode.Return:
                case Opcode.Drop:
                case Opcode.Select:
                case Opcode.I32_Eqz:
                case Opcode.I32_Eq:
                case Opcode.I32_Ne:
                case Opcode.I32_Lt_S:
                case Opcode.I32_Lt_U:
                case Opcode.I32_Gt_S:
                case Opcode.I32_Gt_U:
                case Opcode.I32_Le_S:
                case Opcode.I32_Le_U:
                case Opcode.I32_Ge_S:
                case Opcode.I32_Ge_U:
                case Opcode.I64_Eqz:
                case Opcode.I64_Eq:
                case Opcode.I64_Ne:
                case Opcode.I64_Lt_S:
                case Opcode.I64_Lt_U:
                case Opcode.I64_Gt_S:
                case Opcode.I64_Gt_U:
                case Opcode.I64_Le_S:
                case Opcode.I64_Le_U:
                case Opcode.I64_Ge_S:
                case Opcode.I64_Ge_U:
                case Opcode.F32_Eq:
                case Opcode.F32_Ne:
                case Opcode.F32_Lt:
                case Opcode.F32_Gt:
                case Opcode.F32_Le:
                case Opcode.F32_Ge:
                case Opcode.F64_Eq:
                case Opcode.F64_Ne:
                case Opcode.F64_Lt:
                case Opcode.F64_Gt:
                case Opcode.F64_Le:
                case Opcode.F64_Ge:
                case Opcode.I32_Clz:
                case Opcode.I32_Ctz:
                case Opcode.I32_Popcnt:
                case Opcode.I32_Add:
                case Opcode.I32_Sub:
                case Opcode.I32_Mul:
                case Opcode.I32_Div_S:
                case Opcode.I32_Div_U:
                case Opcode.I32_Rem_S:
                case Opcode.I32_Rem_U:
                case Opcode.I32_And:
                case Opcode.I32_Or:
                case Opcode.I32_Xor:
                case Opcode.I32_Shl:
                case Opcode.I32_Shr_S:
                case Opcode.I32_Shr_U:
                case Opcode.I32_Rotl:
                case Opcode.I32_Rotr:
                case Opcode.I64_Clz:
                case Opcode.I64_Ctz:
                case Opcode.I64_Popcnt:
                case Opcode.I64_Add:
                case Opcode.I64_Sub:
                case Opcode.I64_Mul:
                case Opcode.I64_Div_S:
                case Opcode.I64_Div_U:
                case Opcode.I64_Rem_S:
                case Opcode.I64_Rem_U:
                case Opcode.I64_And:
                case Opcode.I64_Or:
                case Opcode.I64_Xor:
                case Opcode.I64_Shl:
                case Opcode.I64_Shr_S:
                case Opcode.I64_Shr_U:
                case Opcode.I64_Rotl:
                case Opcode.I64_Rotr:
                case Opcode.F32_Abs:
                case Opcode.F32_Neg:
                case Opcode.F32_Ceil:
                case Opcode.F32_Floor:
                case Opcode.F32_Trunc:
                case Opcode.F32_Nearest:
                case Opcode.F32_Sqrt:
                case Opcode.F32_Add:
                case Opcode.F32_Sub:
                case Opcode.F32_Mul:
                case Opcode.F32_Div:
                case Opcode.F32_Min:
                case Opcode.F32_Max:
                case Opcode.F32_Copysign:
                case Opcode.F64_Abs:
                case Opcode.F64_Neg:
                case Opcode.F64_Ceil:
                case Opcode.F64_Floor:
                case Opcode.F64_Trunc:
                case Opcode.F64_Nearest:
                case Opcode.F64_Sqrt:
                case Opcode.F64_Add:
                case Opcode.F64_Sub:
                case Opcode.F64_Mul:
                case Opcode.F64_Div:
                case Opcode.F64_Min:
                case Opcode.F64_Max:
                case Opcode.F64_Copysign:
                case Opcode.I32_Wrap_I64:
                case Opcode.I32_Trunc_F32_S:
                case Opcode.I32_Trunc_F32_U:
                case Opcode.I32_Trunc_F64_S:
                case Opcode.I32_Trunc_F64_U:
                case Opcode.I64_Extend_I32_S:
                case Opcode.I64_Extend_I32_U:
                case Opcode.I64_Trunc_F32_S:
                case Opcode.I64_Trunc_F32_U:
                case Opcode.I64_Trunc_F64_S:
                case Opcode.I64_Trunc_F64_U:
                case Opcode.F32_Convert_I32_S:
                case Opcode.F32_Convert_I32_U:
                case Opcode.F32_Convert_I64_S:
                case Opcode.F32_Convert_I64_U:
                case Opcode.F32_Demote_F64:
                case Opcode.F64_Convert_I32_S:
                case Opcode.F64_Convert_I32_U:
                case Opcode.F64_Convert_I64_S:
                case Opcode.F64_Convert_I64_U:
                case Opcode.F64_Promote_F32:
                case Opcode.I32_Reinterpret_F32:
                case Opcode.I64_Reinterpret_F64:
                case Opcode.F32_Reinterpret_I32:
                case Opcode.F64_Reinterpret_I64:
                case Opcode.I32_Extend8_S:
                case Opcode.I32_Extend16_S:
                case Opcode.I64_Extend8_S:
                case Opcode.I64_Extend16_S:
                case Opcode.I64_Extend32_S:
                    break;

                case Opcode.Prefix:
                    ReadPrefixInstruction(ref instruction);
                    break;

                case Opcode.SIMDPrefix:
                    ReadSIMDInstruction(ref instruction);
                    break;

                case Opcode.MTPrefix:
                    ReadMTInstruction(ref instruction);
                    break;

                default:
                    throw new FileLoadException($"Unknown opcode: {opcode} ({opcode:x})");
            }

            return instruction;
        }

        void ReadPrefixInstruction(ref Instruction instruction)
        {
            instruction.PrefixOpcode = (PrefixOpcode)ReadU32();
            // Console.WriteLine($"Prefix opcode: {instruction.PrefixOpcode}");
            switch (instruction.PrefixOpcode)
            {
                case PrefixOpcode.I32_Trunc_Sat_F32_S:
                case PrefixOpcode.I32_Trunc_Sat_F32_U:
                case PrefixOpcode.I32_Trunc_Sat_F64_S:
                case PrefixOpcode.I32_Trunc_Sat_F64_U:
                case PrefixOpcode.I64_Trunc_Sat_F32_S:
                case PrefixOpcode.I64_Trunc_Sat_F32_U:
                case PrefixOpcode.I64_Trunc_Sat_F64_S:
                case PrefixOpcode.I64_Trunc_Sat_F64_U:
                    break;
                case PrefixOpcode.Memory_Init:
                    instruction.Idx = ReadU32();
                    Reader.ReadByte();
                    break;
                case PrefixOpcode.Data_Drop:
                    instruction.Idx = ReadU32();
                    break;
                case PrefixOpcode.Memory_Copy:
                    Reader.ReadByte();
                    Reader.ReadByte();
                    break;
                case PrefixOpcode.Memory_Fill:
                    Reader.ReadByte();
                    break;
                case PrefixOpcode.Table_Init:
                case PrefixOpcode.Table_Copy:
                    instruction.Idx = ReadU32();
                    instruction.Idx2 = ReadU32();
                    break;
                case PrefixOpcode.Elem_Drop:
                case PrefixOpcode.Table_Grow:
                case PrefixOpcode.Table_Size:
                case PrefixOpcode.Table_Fill:
                    instruction.Idx = ReadU32();
                    break;
                default:
                    throw new FileLoadException($"Unknown Prefix opcode: {instruction.PrefixOpcode} ({instruction.PrefixOpcode:x})");
            }
        }

        void ReadSIMDInstruction(ref Instruction instruction)
        {
            instruction.SIMDOpcode = (SIMDOpcode)ReadU32();
            //Console.WriteLine($"SIMD opcode: {instruction.SIMDOpcode}");

            switch (instruction.SIMDOpcode)
            {
                case SIMDOpcode.V128_Load:
                case SIMDOpcode.V128_Load8x8_S:
                case SIMDOpcode.V128_Load8x8_U:
                case SIMDOpcode.V128_Load16x4_S:
                case SIMDOpcode.V128_Load16x4_U:
                case SIMDOpcode.V128_Load32x2_S:
                case SIMDOpcode.V128_Load32x2_U:
                case SIMDOpcode.V128_Load8_Splat:
                case SIMDOpcode.V128_Load16_Splat:
                case SIMDOpcode.V128_Load32_Splat:
                case SIMDOpcode.V128_Load64_Splat:
                case SIMDOpcode.V128_Store:
                case SIMDOpcode.V128_Load32_Zero:
                case SIMDOpcode.V128_Load64_Zero:
                    instruction.MemArg = ReadMemArg();
                    break;
                case SIMDOpcode.V128_Load8_Lane:
                case SIMDOpcode.V128_Load16_Lane:
                case SIMDOpcode.V128_Load32_Lane:
                case SIMDOpcode.V128_Load64_Lane:
                case SIMDOpcode.V128_Store8_Lane:
                case SIMDOpcode.V128_Store16_Lane:
                case SIMDOpcode.V128_Store32_Lane:
                case SIMDOpcode.V128_Store64_Lane:
                    instruction.MemArg = ReadMemArg();
                    instruction.SIMDImmLaneIdx = Reader.ReadByte();
                    break;
                case SIMDOpcode.V128_Const:
                    instruction.SIMDImmByteArray = Reader.ReadBytes(16);
                    break;
                case SIMDOpcode.I8x16_Shuffle:
                    instruction.SIMDImmByteArray = Reader.ReadBytes(16);
                    break;
                case SIMDOpcode.I8x16_Extract_Lane_S:
                case SIMDOpcode.I8x16_Extract_Lane_U:
                case SIMDOpcode.I8x16_Replace_Lane:
                case SIMDOpcode.I16x8_Extract_Lane_S:
                case SIMDOpcode.I16x8_Extract_Lane_U:
                case SIMDOpcode.I16x8_Replace_Lane:
                case SIMDOpcode.I32x4_Extract_Lane:
                case SIMDOpcode.I32x4_Replace_Lane:
                case SIMDOpcode.I64x2_Extract_Lane:
                case SIMDOpcode.I64x2_Replace_Lane:
                case SIMDOpcode.F32x4_Extract_Lane:
                case SIMDOpcode.F32x4_Replace_Lane:
                case SIMDOpcode.F64x2_Extract_Lane:
                case SIMDOpcode.F64x2_Replace_Lane:
                    instruction.SIMDImmLaneIdx = Reader.ReadByte();
                    break;
                case SIMDOpcode.I8x16_Swizzle:
                case SIMDOpcode.I8x16_Splat:
                case SIMDOpcode.I16x8_Splat:
                case SIMDOpcode.I32x4_Splat:
                case SIMDOpcode.I64x2_Splat:
                case SIMDOpcode.F32x4_Splat:
                case SIMDOpcode.F64x2_Splat:
                case SIMDOpcode.I8x16_Eq:
                case SIMDOpcode.I8x16_Ne:
                case SIMDOpcode.I8x16_Lt_S:
                case SIMDOpcode.I8x16_Lt_U:
                case SIMDOpcode.I8x16_Gt_S:
                case SIMDOpcode.I8x16_Gt_U:
                case SIMDOpcode.I8x16_Le_S:
                case SIMDOpcode.I8x16_Le_U:
                case SIMDOpcode.I8x16_Ge_S:
                case SIMDOpcode.I8x16_Ge_U:
                case SIMDOpcode.I16x8_Eq:
                case SIMDOpcode.I16x8_Ne:
                case SIMDOpcode.I16x8_Lt_S:
                case SIMDOpcode.I16x8_Lt_U:
                case SIMDOpcode.I16x8_Gt_S:
                case SIMDOpcode.I16x8_Gt_U:
                case SIMDOpcode.I16x8_Le_S:
                case SIMDOpcode.I16x8_Le_U:
                case SIMDOpcode.I16x8_Ge_S:
                case SIMDOpcode.I16x8_Ge_U:
                case SIMDOpcode.I32x4_Eq:
                case SIMDOpcode.I32x4_Ne:
                case SIMDOpcode.I32x4_Lt_S:
                case SIMDOpcode.I32x4_Lt_U:
                case SIMDOpcode.I32x4_Gt_S:
                case SIMDOpcode.I32x4_Gt_U:
                case SIMDOpcode.I32x4_Le_S:
                case SIMDOpcode.I32x4_Le_U:
                case SIMDOpcode.I32x4_Ge_S:
                case SIMDOpcode.I32x4_Ge_U:
                case SIMDOpcode.F32x4_Eq:
                case SIMDOpcode.F32x4_Ne:
                case SIMDOpcode.F32x4_Lt:
                case SIMDOpcode.F32x4_Gt:
                case SIMDOpcode.F32x4_Le:
                case SIMDOpcode.F32x4_Ge:
                case SIMDOpcode.F64x2_Eq:
                case SIMDOpcode.F64x2_Ne:
                case SIMDOpcode.F64x2_Lt:
                case SIMDOpcode.F64x2_Gt:
                case SIMDOpcode.F64x2_Le:
                case SIMDOpcode.F64x2_Ge:
                case SIMDOpcode.V128_Not:
                case SIMDOpcode.V128_And:
                case SIMDOpcode.V128_Andnot:
                case SIMDOpcode.V128_Or:
                case SIMDOpcode.V128_Xor:
                case SIMDOpcode.V128_Bitselect:
                case SIMDOpcode.I8x16_Abs:
                case SIMDOpcode.I8x16_Neg:
                case SIMDOpcode.I8x16_All_True:
                case SIMDOpcode.I8x16_Bitmask:
                case SIMDOpcode.I8x16_Narrow_I16x8_S:
                case SIMDOpcode.I8x16_Narrow_I16x8_U:
                case SIMDOpcode.I8x16_Shl:
                case SIMDOpcode.I8x16_Shr_S:
                case SIMDOpcode.I8x16_Shr_U:
                case SIMDOpcode.I8x16_Add:
                case SIMDOpcode.I8x16_Add_Sat_S:
                case SIMDOpcode.I8x16_Add_Sat_U:
                case SIMDOpcode.I8x16_Sub:
                case SIMDOpcode.I8x16_Sub_Sat_S:
                case SIMDOpcode.I8x16_Sub_Sat_U:
                case SIMDOpcode.I8x16_Min_S:
                case SIMDOpcode.I8x16_Min_U:
                case SIMDOpcode.I8x16_Max_S:
                case SIMDOpcode.I8x16_Max_U:
                case SIMDOpcode.I8x16_Avgr_U:
                case SIMDOpcode.I16x8_Abs:
                case SIMDOpcode.I16x8_Neg:
                case SIMDOpcode.I16x8_All_True:
                case SIMDOpcode.I16x8_Bitmask:
                case SIMDOpcode.I16x8_Narrow_I32x4_S:
                case SIMDOpcode.I16x8_Narrow_I32x4_U:
                case SIMDOpcode.I16x8_Extend_Low_I8x16_S:
                case SIMDOpcode.I16x8_Extend_High_I8x16_S:
                case SIMDOpcode.I16x8_Extend_Low_I8x16_U:
                case SIMDOpcode.I16x8_Extend_High_I8x16_U:
                case SIMDOpcode.I16x8_Shl:
                case SIMDOpcode.I16x8_Shr_S:
                case SIMDOpcode.I16x8_Shr_U:
                case SIMDOpcode.I16x8_Add:
                case SIMDOpcode.I16x8_Add_Sat_S:
                case SIMDOpcode.I16x8_Add_Sat_U:
                case SIMDOpcode.I16x8_Sub:
                case SIMDOpcode.I16x8_Sub_Sat_S:
                case SIMDOpcode.I16x8_Sub_Sat_U:
                case SIMDOpcode.I16x8_Mul:
                case SIMDOpcode.I16x8_Min_S:
                case SIMDOpcode.I16x8_Min_U:
                case SIMDOpcode.I16x8_Max_S:
                case SIMDOpcode.I16x8_Max_U:
                case SIMDOpcode.I16x8_Avgr_U:
                case SIMDOpcode.I32x4_Abs:
                case SIMDOpcode.I32x4_Neg:
                case SIMDOpcode.I32x4_All_True:
                case SIMDOpcode.I32x4_Bitmask:
                case SIMDOpcode.I32x4_Extend_Low_I16x8_S:
                case SIMDOpcode.I32x4_Extend_High_I16x8_S:
                case SIMDOpcode.I32x4_Extend_Low_I16x8_U:
                case SIMDOpcode.I32x4_Extend_High_I16x8_U:
                case SIMDOpcode.I32x4_Shl:
                case SIMDOpcode.I32x4_Shr_S:
                case SIMDOpcode.I32x4_Shr_U:
                case SIMDOpcode.I32x4_Add:
                case SIMDOpcode.I32x4_Sub:
                case SIMDOpcode.I32x4_Mul:
                case SIMDOpcode.I32x4_Min_S:
                case SIMDOpcode.I32x4_Min_U:
                case SIMDOpcode.I32x4_Max_S:
                case SIMDOpcode.I32x4_Max_U:
                case SIMDOpcode.I32x4_Dot_I16x8_S:
                case SIMDOpcode.I64x2_Abs:
                case SIMDOpcode.I64x2_Neg:
                case SIMDOpcode.I64x2_Bitmask:
                case SIMDOpcode.I64x2_Extend_Low_I32x4_S:
                case SIMDOpcode.I64x2_Extend_High_I32x4_S:
                case SIMDOpcode.I64x2_Extend_Low_I32x4_U:
                case SIMDOpcode.I64x2_Extend_High_I32x4_U:
                case SIMDOpcode.I64x2_Shl:
                case SIMDOpcode.I64x2_Shr_S:
                case SIMDOpcode.I64x2_Shr_U:
                case SIMDOpcode.I64x2_Add:
                case SIMDOpcode.I64x2_Sub:
                case SIMDOpcode.I64x2_Mul:
                case SIMDOpcode.F32x4_Ceil:
                case SIMDOpcode.F32x4_Floor:
                case SIMDOpcode.F32x4_Trunc:
                case SIMDOpcode.F32x4_Nearest:
                case SIMDOpcode.F64x2_Ceil:
                case SIMDOpcode.F64x2_Floor:
                case SIMDOpcode.F64x2_Trunc:
                case SIMDOpcode.F64x2_Nearest:
                case SIMDOpcode.F32x4_Abs:
                case SIMDOpcode.F32x4_Neg:
                case SIMDOpcode.F32x4_Sqrt:
                case SIMDOpcode.F32x4_Add:
                case SIMDOpcode.F32x4_Sub:
                case SIMDOpcode.F32x4_Mul:
                case SIMDOpcode.F32x4_Div:
                case SIMDOpcode.F32x4_Min:
                case SIMDOpcode.F32x4_Max:
                case SIMDOpcode.F32x4_Pmin:
                case SIMDOpcode.F32x4_Pmax:
                case SIMDOpcode.F64x2_Abs:
                case SIMDOpcode.F64x2_Neg:
                case SIMDOpcode.F64x2_Sqrt:
                case SIMDOpcode.F64x2_Add:
                case SIMDOpcode.F64x2_Sub:
                case SIMDOpcode.F64x2_Mul:
                case SIMDOpcode.F64x2_Div:
                case SIMDOpcode.F64x2_Min:
                case SIMDOpcode.F64x2_Max:
                case SIMDOpcode.F64x2_Pmin:
                case SIMDOpcode.F64x2_Pmax:
                case SIMDOpcode.I32x4_Trunc_Sat_F32x4_S:
                case SIMDOpcode.I32x4_Trunc_Sat_F32x4_U:
                case SIMDOpcode.F32x4_Convert_I32x4_S:
                case SIMDOpcode.F32x4_Convert_I32x4_U:
                case SIMDOpcode.I16x8_Extmul_Low_I8x16_S:
                case SIMDOpcode.I16x8_Extmul_High_I8x16_S:
                case SIMDOpcode.I16x8_Extmul_Low_I8x16_U:
                case SIMDOpcode.I16x8_Extmul_High_I8x16_U:
                case SIMDOpcode.I32x4_Extmul_Low_I16x8_S:
                case SIMDOpcode.I32x4_Extmul_High_I16x8_S:
                case SIMDOpcode.I32x4_Extmul_Low_I16x8_U:
                case SIMDOpcode.I32x4_Extmul_High_I16x8_U:
                case SIMDOpcode.I64x2_Extmul_Low_I32x4_S:
                case SIMDOpcode.I64x2_Extmul_High_I32x4_S:
                case SIMDOpcode.I64x2_Extmul_Low_I32x4_U:
                case SIMDOpcode.I64x2_Extmul_High_I32x4_U:
                case SIMDOpcode.I16x8_Q15mulr_Sat_S:
                case SIMDOpcode.V128_Any_True:
                case SIMDOpcode.I64x2_Eq:
                case SIMDOpcode.I64x2_Ne:
                case SIMDOpcode.I64x2_Lt_S:
                case SIMDOpcode.I64x2_Gt_S:
                case SIMDOpcode.I64x2_Le_S:
                case SIMDOpcode.I64x2_Ge_S:
                case SIMDOpcode.I64x2_All_True:
                case SIMDOpcode.F64x2_Convert_Low_I32x4_S:
                case SIMDOpcode.F64x2_Convert_Low_I32x4_U:
                case SIMDOpcode.I32x4_Trunc_Sat_F64x2_S_Zero:
                case SIMDOpcode.I32x4_Trunc_Sat_F64x2_U_Zero:
                case SIMDOpcode.F32x4_Demote_F64x2_Zero:
                case SIMDOpcode.F64x2_Promote_Low_F32x4:
                case SIMDOpcode.I8x16_Popcnt:
                case SIMDOpcode.I16x8_Extadd_Pairwise_I8x16_S:
                case SIMDOpcode.I16x8_Extadd_Pairwise_I8x16_U:
                case SIMDOpcode.I32x4_Extadd_Pairwise_I16x8_S:
                case SIMDOpcode.I32x4_Extadd_Pairwise_I16x8_U:
                    break;
                default:
                    throw new FileLoadException($"Unknown SIMD opcode: {instruction.SIMDOpcode} ({instruction.SIMDOpcode:x})");
            }
        }

        void ReadMTInstruction(ref Instruction instruction)
        {
            instruction.MTOpcode = (MTOpcode)ReadU32();
            // Console.WriteLine($"MT opcode: {instruction.MTOpcode}");

            switch (instruction.MTOpcode)
            {
                case MTOpcode.Atomic_Fence:
                    Reader.ReadByte();
                    break;
                case MTOpcode.Memory_Atomic_Notify:
                case MTOpcode.Memory_Atomic_Wait32:
                case MTOpcode.Memory_Atomic_Wait64:
                case MTOpcode.I32_Atomic_Load:
                case MTOpcode.I64_Atomic_Load:
                case MTOpcode.I32_Atomic_Load8_u:
                case MTOpcode.I32_Atomic_Load16_u:
                case MTOpcode.I64_Atomic_Load8_u:
                case MTOpcode.I64_Atomic_Load16_u:
                case MTOpcode.I64_Atomic_Load32_u:
                case MTOpcode.I32_Atomic_Store:
                case MTOpcode.I64_Atomic_Store:
                case MTOpcode.I32_Atomic_Store8:
                case MTOpcode.I32_Atomic_Store16:
                case MTOpcode.I64_Atomic_Store8:
                case MTOpcode.I64_Atomic_Store16:
                case MTOpcode.I64_Atomic_Store32:
                case MTOpcode.I32_Atomic_Rmw_Add:
                case MTOpcode.I64_Atomic_Rmw_Add:
                case MTOpcode.I32_Atomic_Rmw8_Add_U:
                case MTOpcode.I32_Atomic_Rmw16_Add_U:
                case MTOpcode.I64_Atomic_Rmw8_Add_U:
                case MTOpcode.I64_Atomic_Rmw16_Add_U:
                case MTOpcode.I64_Atomic_Rmw32_Add_U:
                case MTOpcode.I32_Atomic_Rmw_Sub:
                case MTOpcode.I64_Atomic_Rmw_Sub:
                case MTOpcode.I32_Atomic_Rmw8_Sub_U:
                case MTOpcode.I32_Atomic_Rmw16_Sub_U:
                case MTOpcode.I64_Atomic_Rmw8_Sub_U:
                case MTOpcode.I64_Atomic_Rmw16_Sub_U:
                case MTOpcode.I64_Atomic_Rmw32_Sub_U:
                case MTOpcode.I32_Atomic_Rmw_And:
                case MTOpcode.I64_Atomic_Rmw_And:
                case MTOpcode.I32_Atomic_Rmw8_And_U:
                case MTOpcode.I32_Atomic_Rmw16_And_U:
                case MTOpcode.I64_Atomic_Rmw8_And_U:
                case MTOpcode.I64_Atomic_Rmw16_And_U:
                case MTOpcode.I64_Atomic_Rmw32_And_U:
                case MTOpcode.I32_Atomic_Rmw_Or:
                case MTOpcode.I64_Atomic_Rmw_Or:
                case MTOpcode.I32_Atomic_Rmw8_Or_U:
                case MTOpcode.I32_Atomic_Rmw16_Or_U:
                case MTOpcode.I64_Atomic_Rmw8_Or_U:
                case MTOpcode.I64_Atomic_Rmw16_Or_U:
                case MTOpcode.I64_Atomic_Rmw32_Or_U:
                case MTOpcode.I32_Atomic_Rmw_Xor:
                case MTOpcode.I64_Atomic_Rmw_Xor:
                case MTOpcode.I32_Atomic_Rmw8_Xor_U:
                case MTOpcode.I32_Atomic_Rmw16_Xor_U:
                case MTOpcode.I64_Atomic_Rmw8_Xor_U:
                case MTOpcode.I64_Atomic_Rmw16_Xor_U:
                case MTOpcode.I64_Atomic_Rmw32_Xor_U:
                case MTOpcode.I32_Atomic_Rmw_Xchg:
                case MTOpcode.I64_Atomic_Rmw_Xchg:
                case MTOpcode.I32_Atomic_Rmw8_Xchg_U:
                case MTOpcode.I32_Atomic_Rmw16_Xchg_U:
                case MTOpcode.I64_Atomic_Rmw8_Xchg_U:
                case MTOpcode.I64_Atomic_Rmw16_Xchg_U:
                case MTOpcode.I64_Atomic_Rmw32_Xchg_U:
                case MTOpcode.I32_Atomic_Rmw_CmpXchg:
                case MTOpcode.I64_Atomic_Rmw_CmpXchg:
                case MTOpcode.I32_Atomic_Rmw8_CmpXchg_U:
                case MTOpcode.I32_Atomic_Rmw16_CmpXchg_U:
                case MTOpcode.I64_Atomic_Rmw8_CmpXchg_U:
                case MTOpcode.I64_Atomic_Rmw16_CmpXchg_U:
                case MTOpcode.I64_Atomic_Rmw32_CmpXchg_U:
                    instruction.MemArg = ReadMemArg();
                    break;
                default:
                    throw new FileLoadException($"Unknown MT opcode: {instruction.MTOpcode} ({instruction.MTOpcode:x})");
            }
        }

        public (Instruction[], Opcode) ReadBlock(Opcode end = Opcode.End)
        {
            List<Instruction> instructions = new();
            Opcode b;
            do
            {
                b = (Opcode)Reader.ReadByte();
                //Console.WriteLine($"    opcode: {b}");

                if (b == Opcode.End || b == end)
                    break;

                instructions.Add(ReadInstruction(b));
            } while (true);

            return (instructions.ToArray(), b);
        }

        List<string> customSectionNames = new();

        void ReadCustomSection(UInt32 size)
        {
            var start = Reader.BaseStream.Position;
            var name = ReadString();
            customSectionNames.Add(name);

            if (Context.Verbose)
                Console.Write($" name: {name}");

            var remainingSize = size - (UInt32)(Reader.BaseStream.Position - start);

            switch(name)
            {
                case "name":
                    ReadCustomNameSection(remainingSize);
                    break;
                case "linking":
                    ReadCustomLinkingSection(remainingSize);
                    break;
                default:
                    if (name.StartsWith("reloc."))
                        ReadCustomRelocSection(remainingSize, name.Substring(6));
                    else if (Context.Verbose2)
                        DumpBytes(remainingSize, true);
                    break;
            }
        }

        public override string ToString()
        {
            return $"module name: {ModuleName} path: {Path}";
        }

        protected string ModuleName = "";
        readonly NameMap functionNames = new();
        readonly Dictionary<string, UInt32> nameToFunction = new();
        readonly NameMap globalNames = new();
        readonly NameMap dataSegmentNames = new();
        readonly Dictionary<UInt32, NameMap> localNames = new();

        void ReadCustomNameSection(UInt32 size)
        {
            var start = Reader.BaseStream.Position;

            if (Context.Verbose2)
                Console.WriteLine();

            while (Reader.BaseStream.Position - start < size)
            {
                var id = (CustomSubSectionId)Reader.ReadByte();
                UInt32 subSectionSize = ReadU32();
                var subSectionStart = Reader.BaseStream.Position;

                switch (id)
                {
                    case CustomSubSectionId.ModuleName:
                        ModuleName = ReadString();
                        if (Context.Verbose2)
                            Console.WriteLine($"  module name: {ModuleName}");
                        break;
                    case CustomSubSectionId.FunctionNames:
                        ReadNameMap(functionNames, "function", nameToFunction);
                        break;
                    case CustomSubSectionId.LocalNames:
                        ReadIndirectNameMap(localNames, "local", "function");
                        break;
                    case CustomSubSectionId.GlobalNames:
                        ReadNameMap(globalNames, "global");
                        break;
                    case CustomSubSectionId.DataSegmentNames:
                        ReadNameMap(dataSegmentNames, "data segment");
                        break;
                    default:
                        if (Context.Verbose2)
                            Console.WriteLine($"  subsection {id}");
                        break;
                }

                Reader.BaseStream.Seek(subSectionStart + subSectionSize, SeekOrigin.Begin);
            }
        }

        void ReadIndirectNameMap(Dictionary<UInt32, NameMap> indirectMap, string mapName, string indiceName)
        {
            var count = ReadU32();
            if (Context.Verbose2)
                Console.WriteLine($"  {mapName} names count: {count}");

            for (int i = 0; i < count; i++)
            {
                var idx = ReadU32();
                if (Context.Verbose2)
                    Console.WriteLine($"    {mapName} map for {indiceName}: {idx}");

                var map = ReadNameMap(null, mapName);
                if (indirectMap.ContainsKey(idx))
                    Console.WriteLine($"\nwarning: duplicate {indiceName} idx: {idx} in {mapName} names indirect map ignored");
                else
                    indirectMap[idx] = map;
            }
        }

        Dictionary<UInt32, string> ReadNameMap(Dictionary<UInt32, string>? map, string mapName, Dictionary<string, UInt32>? reversed = null)
        {
            var count = ReadU32();
            if (Context.Verbose2)
                Console.WriteLine($"      {mapName} names count: {count}");

            if (map == null)
                map = new Dictionary<UInt32, string>();

            for (int i = 0; i < count; i++)
            {
                var idx = ReadU32();
                var name = ReadString();
                if (Context.Verbose2)
                    Console.WriteLine($"        {mapName} idx: {idx} name: {name}");

                if (map.ContainsKey(idx))
                    Console.WriteLine($"\nwarning: duplicate {mapName} idx: {idx} = '{name}' in {mapName} names map ignored");
                else
                {
                    map[idx] = name;
                    if (reversed != null)
                        reversed[name] = idx;
                }
            }

            return map;
        }

        Export[]? exports;

        void ReadExportSection()
        {
            UInt32 count = ReadU32();

            if (Context.Verbose)
                Console.Write($" count: {count}");

            if (Context.Verbose2)
                Console.WriteLine();

            exports = new Export[count];

            for (int i = 0; i < count; i++)
            {
                exports[i].Name = ReadString();
                exports[i].Desc = (ExportDesc)Reader.ReadByte();
                exports[i].Idx = ReadU32();

                if (Context.Verbose2)
                    Console.WriteLine($"  {exports[i]}");
            }
        }

        Import[]? imports;
        uint functionImportsCount = 0;

        void ReadImportSection()
        {
            UInt32 count = ReadU32();

            if (Context.Verbose)
                Console.Write($" count: {count}");

            if (Context.Verbose2)
                Console.WriteLine();

            imports = new Import[count];

            for (int i = 0; i < count; i++)
            {
                imports[i].Module = ReadString();
                imports[i].Name = ReadString();
                imports[i].Desc = (ImportDesc)Reader.ReadByte();

                switch(imports[i].Desc) {
                    case ImportDesc.TypeIdx:
                        imports[i].Idx = ReadU32();
                        break;
                    case ImportDesc.MemType:
                        (imports[i].Min, imports[i].Max) = ReadLimits();
                        break;
                    case ImportDesc.GlobalType:
                        ReadGlobal(ref imports[i].GlobalType);
                        break;
                    case ImportDesc.TableType:
                        ReadTableType(ref imports[i].TableType);
                        break;
                }

                if (imports[i].Desc == ImportDesc.TypeIdx)
                    functionImportsCount++;

                if (Context.Verbose2)
                    Console.WriteLine($"  {imports[i]}");
            }
        }

        string ReadString()
        {
            var len = ReadU32();

            return Encoding.UTF8.GetString(Reader.ReadBytes((int)len));
        }

        protected Function[]? functions;

        protected FunctionType[]? functionTypes;

        void ReadTypeSection()
        {
            UInt32 count = ReadU32();

            if (Context.Verbose)
                Console.Write($" count: {count}");

            if (Context.Verbose2)
                Console.WriteLine();

            functionTypes = new FunctionType[count];
            for (int i = 0; i < count; i++)
            {
                var b = Reader.ReadByte();
                if (b != 0x60)
                    throw new FileLoadException("Expected 0x60 for function type");

                ReadResultTypes(ref functionTypes[i].Parameters);
                ReadResultTypes(ref functionTypes[i].Results);

                if (Context.Verbose2)
                    Console.WriteLine($"  Function type[{i}]: {functionTypes[i]}");
            }
        }

        void ReadResultTypes(ref ResultType type)
        {
            UInt32 count = ReadU32();

            type.Types = new ValueType[count];

            for (int i = 0; i < count; i++)
            {
                ReadValueType(ref type.Types[i]);
            }
        }

        public void ReadValueType(ref ValueType vt)
        {
            var b = Reader.ReadByte();
            vt.IsRefenceType = b <= 0x70;
            vt.IsVectorType = b == 0x7b;
            vt.value = b;
        }

        void ReadFunctionSection()
        {
            UInt32 count = ReadU32();

            if (Context.Verbose)
                Console.Write($" count: {count}");

            functions = new Function[count];

            for (int i = 0; i < count; i++)
            {
                functions[i].TypeIdx = ReadU32();
            }
        }

        public bool HasFunctionNames { get { return functionNames != null && functionNames.Count > 0; } }

        public string? GetFunctionName(UInt32 idx, bool needsOffset = true)
        {
            string? name = null;
            var fidx = needsOffset ? FunctionOffset(idx) : idx;
            if (HasFunctionNames && functionNames.ContainsKey(fidx))
                name = functionNames[fidx];

            if (string.IsNullOrEmpty(name))
                name = $"idx:{idx}";

            return name;
        }

        public bool GetFunctionIdx(string? name, out UInt32 idx)
        {
            if (name == null || !nameToFunction.ContainsKey(name))
            {
                idx = 0;
                return false;
            }

            idx = nameToFunction[name] - functionImportsCount;

            return true;
        }

        List<WitImport>? witImports;

        void ReadWitExternDescription(ref WitExternDescription ed)
        {
                ed.Kind = (WitExternDescriptionKind) Reader.ReadByte();
                if (Context.Verbose2)
                    Console.WriteLine($" extern description kind: {ed.Kind}");

                switch(ed.Kind) {
                    case WitExternDescriptionKind.CoreModule:
                    case WitExternDescriptionKind.Function:
                    case WitExternDescriptionKind.Component:
                    case WitExternDescriptionKind.Instance:
                        var typeIdx = ReadU32();
                    if (Context.Verbose2)
                        Console.WriteLine($" idx: {typeIdx}");
                        break;
                    case WitExternDescriptionKind.Type:
                        ed.TypeBound = (WitTypeBound) Reader.ReadByte();
                        if (ed.TypeBound == WitTypeBound.Eq)
                            ed.Idx = ReadU32();
                        if (Context.Verbose2)
                            Console.WriteLine($" typebound: {ed.ValueBound} idx: {ed.Idx}");
                        break;
                    case WitExternDescriptionKind.Value:
                        ed.ValueBound = (WitValueBound) Reader.ReadByte();
                        if (ed.ValueBound == WitValueBound.Eq)
                            ed.Idx = ReadU32();
                        else
                            ReadWitValueType(ref ed.ValueType);
                        if (Context.Verbose2)
                            Console.WriteLine($" valuebound: {ed.ValueBound} idx: {ed.Idx}");
                        break;
                }
        }

        void ReadWitValueType(ref WitValueType vt)
        {
            var v = Reader.ReadByte();
            vt.Kind = v >= 0x73 && v < 0x7f ? WitValueTypeKind.PrimaryValueType : WitValueTypeKind.Type;
            if (Context.Verbose2)
                Console.WriteLine($" kind: {vt.Kind}");
            if (vt.Kind == WitValueTypeKind.PrimaryValueType)
                vt.PrimaryValueType = (WitPrimaryValueType) v;
            else {
                Reader.BaseStream.Seek(-1, SeekOrigin.Current);
                vt.TypeIdx = ReadU32();
            }
        }

        void ReadWitImportSection()
        {
            UInt32 count = ReadU32();

            if (Context.Verbose)
                Console.Write($" count: {count}");

            if (Context.Verbose2)
                Console.WriteLine();

            witImports ??= new ();

            for (int i = 0; i < count; i++)
            {
                var import = new WitImport();
                import.Length = ReadU32();
                import.Name = ReadString();
                if (Context.Verbose2)
                    Console.WriteLine($" len: {import.Length} name: {import.Name}");

                ReadWitExternDescription(ref import.ExternDescription);

                witImports.Add(import);
            }
        }

        List<WitExport>? witExports;

        void ReadWitExportSection()
        {
            UInt32 count = ReadU32();

            if (Context.Verbose)
                Console.Write($" count: {count}");

            if (Context.Verbose2)
                Console.WriteLine();

            witExports ??= new ();


            for (int i = 0; i < count; i++)
            {
                var export = new WitExport();
                export.Length = ReadU32();
                export.Name = ReadString();
                if (Context.Verbose2)
                    Console.WriteLine($" len: {export.Length} name: {export.Name}");

                export.Sort = (WitSort) Reader.ReadByte();
                if (Context.Verbose2)
                    Console.WriteLine($" sort: {export.Sort}");

                if (export.Sort == WitSort.CoreSort) {
                    export.CoreSort = (WitCoreSort) Reader.ReadByte();
                    Console.WriteLine($" core:sort: {export.CoreSort}");
                }

                export.SortIdx = ReadU32();

                if (Context.Verbose2)
                    Console.WriteLine($" sort idx: {export.SortIdx}");

                ReadWitExternDescription(ref export.ExternDescription);

                witExports.Add(export);
            }
        }

        protected delegate void ProcessFunction(UInt32 idx, string? name, object? data);

        protected virtual void FilterFunctions(ProcessFunction processFunction, object? data = null)
        {
            if (functions == null)
                return;

            for (UInt32 idx = 0; idx < functions.Length; idx++)
            {

                if (!FilterFunction(idx, out string? name, data))
                    continue;

                processFunction(idx, name, data);
            }
        }

        protected virtual bool FilterFunction(uint idx, out string? name, object? data = null)
        {
            name = null;
            bool process = Context.FunctionFilter == null && Context.FunctionOffset == -1;

            if (Context.FunctionOffset != -1 && FuncsCode != null
                && idx < FuncsCode.Length
                && FuncsCode[idx].Offset <= Context.FunctionOffset && FuncsCode[idx].Offset + FuncsCode[idx].Size > Context.FunctionOffset)
            {
                process = true;
            }

            if (!process && Context.FunctionFilter != null)
            {
                if (!HasFunctionNames)
                    return false;

                name = GetFunctionName(idx);
                if (name == null)
                    return false;

                if (!Context.FunctionFilter.Match(name).Success)
                    return false;

                process = true;
            }
            else if (process)
            {
                name = GetFunctionName(idx);
            }

            return process;
        }

        public void PrintFunctions()
        {
            FilterFunctions(PrintFunction);

            foreach(var reader in ModuleReaders) {
                Console.WriteLine($"Module: {reader.Path}");
                reader.PrintFunctions();
            }
        }

        protected void PrintFunctionWithPrefix(UInt32 idx, string? name, string? prefix = null)
        {
            if (functions == null || functionTypes == null || FuncsCode == null)
                return;

            var type = functionTypes[functions[idx].TypeIdx];
            Console.WriteLine($"{prefix}{type.ToString(name, true)}\n{FuncsCode[idx].ToString(this, type.Parameters.Types.Length).Indent(prefix)}");
        }

        protected void PrintFunction(UInt32 idx, string? name, object? _ = null)
        {
            PrintFunctionWithPrefix(idx, name, null);
        }

        UInt32 FunctionOffset(UInt32 idx) => functionImportsCount + idx;

        public string FunctionName(UInt32 idx)
        {
            return functionNames[idx];
        }
        public string FunctionType(UInt32 idx)
        {
            if (functionTypes == null)
                return string.Empty;

            return functionTypes[idx].ToString();
        }

        public string GlobalName(UInt32 idx)
        {
            return (globalNames != null && globalNames.ContainsKey(idx)) ? globalNames[idx] : $"global:{idx}";
        }

        public void FindFunctionsCallingInterp()
        {
            if (FuncsCode == null || imports == null)
                return;

            if (!nameToFunction.TryGetValue("mini_llvmonly_get_interp_entry", out var interpIdx))
            {
                Console.WriteLine("Unable to find `mini_llvmonly_get_interp_entry` function. Make sure the wasm is built with AOT and native debug symbols enabled.");

                return;
            }

            uint count = 0, totalCount = 0;
            for (UInt32 idx = 0; idx < FuncsCode.Length; idx++)
            {
                UInt32 funcIdx = FunctionOffset(idx);
                var name = functionNames[funcIdx];
                if (Context.FunctionFilter != null && !Context.FunctionFilter.Match(name).Success)
                    continue;

                totalCount++;

                if (FunctionCallsFunction(funcIdx, interpIdx))
                {
                    count++;

                    if (Context.Verbose)
                        Console.WriteLine($"function {name} calls interpreter, code size: {FuncsCode[idx].Size}");
                }
            }

            Console.WriteLine($"AOT stats: {count} function(s) call(s) interpreter, {(totalCount == 0 ? 0 : ((double)100 * count) / totalCount):N2}% of {totalCount} functions");
        }

        bool FunctionCallsFunction(UInt32 idx, UInt32 calledIdx)
        {
            if (FuncsCode == null || imports == null)
                return false;

            var code = FuncsCode[idx - functionImportsCount];
            if (!code.EnsureCodeReaded(this))
                return false;

            foreach (var inst in code.Instructions)
            {
                if (inst.Opcode == Opcode.Call && inst.Idx == calledIdx)
                    return true;
            }

            return false;
        }

        void PrintWitSummary()
        {
            var sectionsInfo = new Dictionary<SectionId, (UInt32 size, UInt32 count)>();

            foreach (var section in sections)
            {
                if (sectionsInfo.ContainsKey(section.id))
                {
                    sectionsInfo[section.id] = (sectionsInfo[section.id].size + section.size, sectionsInfo[section.id].count + 1);
                }
                else
                {
                    sectionsInfo[section.id] = (section.size, 1);
                }
            }

            foreach (var entry in sectionsInfo)
            {
                Console.WriteLine($"  id: {entry.Key,-15} count: {entry.Value.count,4} total size: {entry.Value.size,12:N0}");
            }
        }

        public void PrintSummary()
        {
            var moduleName = string.IsNullOrEmpty(this.ModuleName) ? null : $" name: {this.ModuleName}";
            Console.WriteLine($"Module:{moduleName} path: {Path}");
            Console.WriteLine($"  size: {Length:N0}");
            Console.WriteLine($"  binary format version: {Version}");
            Console.WriteLine($"  sections: {sections.Count}");

            if (InWitComponent)
                PrintWitSummary();
            else
            {
                int customSectionOffset = 0;
                for (int i = 0; i < sections.Count; i++)
                {
                    var id = sections[i].id;
                    var sectionName = (id == SectionId.Custom && customSectionOffset < customSectionNames.Count) ? $" name: {customSectionNames[customSectionOffset++]}" : "";
                    Console.WriteLine($"    id: {id,-12}{sectionName,-22} size: {sections[i].size,12:N0}");
                }
            }

            foreach (var reader in ModuleReaders) {
                reader.PrintSummary();
            }
        }

        public void PrintWitImports()
        {
            if (witImports == null)
                return;

            Console.WriteLine($"wit imports: {witImports.Count}");

            foreach (var import in witImports)
            {
                Console.WriteLine($"  {import}");
            }
        }

        public void PrintWitExports()
        {
            if (witExports == null)
                return;

            Console.WriteLine($"wit exports: {witExports.Count}");

            foreach (var export in witExports)
            {
                Console.WriteLine($"  {export}");
            }
        }
    }
}
