using System.Text;
using System.Text.RegularExpressions;
using RecompOne.Recompiler.Analysis;
using RecompOne.Recompiler.Config;
using RecompOne.Recompiler.Disasm;
using RecompOne.Recompiler.Elf;
using RecompOne.Recompiler.Map;
using RecompOne.Recompiler.Symbols;
using RecompOne.Recompiler.Psx;
using RecompOne.Runtime.Cdrom;

namespace RecompOne.Recompiler.CodeGen;

public static class OverlayWriter
{
    record OverlayResult(string Name, List<MipsFunction> Functions, int LbaStart);

    public static void Write(RecompOneConfig config, CueFs fs, string outDir)
    {
        string className = SafeIdentifier(config.Game.Name);

        Console.WriteLine("[Recompiler] reading SYSTEM.CNF");
        var sysCfg = SystemCfg.Parse(fs);
        Console.WriteLine($"[Recompiler] SYSTEM.CNF: BOOT={sysCfg.BootExe}  TCB={sysCfg.Tcb}  EVENT={sysCfg.Event}  STACK=0x{sysCfg.Stack:X8}");

        var mainExe = Parser.ParseExe(fs, sysCfg.BootExe);
        Console.WriteLine($"[Recompiler] PS-EXE: {mainExe.Region}");
        Console.WriteLine($"[Recompiler] PS-EXE: PC=0x{mainExe.InitialPC:X8}  GP=0x{mainExe.InitialGP:X8}  SP=0x{mainExe.InitialSP:X8}  load=0x{mainExe.Destination:X8} ");

        var overlayResults = new List<OverlayResult>();

        {
            List<MipsFunction> funcs;
            MipsInstruction[] mainInstrs;
            FunctionInfo? elfInfo = null;

            FunctionInfo? rawElf = null;
            if (config.Elf != null)
            {
                if (!File.Exists(config.Elf))
                    throw new FileNotFoundException($"Main ELF not found: {config.Elf}");

                Console.WriteLine($"[Recompiler] Processing main executable with ELF: {config.Elf} (WARNING: 'elf' is deprecated, prefer 'map'/'funcMap')");
                rawElf = ElfReader.Read(config.Elf);
            }

            FunctionInfo? rawMap = null;
            if (config.Map != null)
            {
                if (!File.Exists(config.Map))
                    throw new FileNotFoundException($"Main map not found: {config.Map}");

                Console.WriteLine($"[Recompiler] Processing main executable with map: {config.Map}");
                rawMap = MapReader.Read(config.Map);
            }

            FunctionInfo? rawFuncMap = null;
            if (config.FuncMap != null)
            {
                if (!File.Exists(config.FuncMap))
                    throw new FileNotFoundException($"Main function map not found: {config.FuncMap}");

                Console.WriteLine($"[Recompiler] Processing main executable with function map: {config.FuncMap}");
                uint funcMapBase = rawElf?.TextBase ?? rawMap?.LoadAddress ?? mainExe.Destination;
                rawFuncMap = FunctionMapLoader.Load(config.FuncMap, funcMapBase, mainExe.Code);
            }

            if (rawElf != null || rawMap != null || rawFuncMap != null)
            {
                elfInfo = FunctionMapLoader.Merge(rawElf, rawMap, rawFuncMap);
                if (elfInfo.TextData.Length == 0) elfInfo.TextData = mainExe.Code;
                Console.WriteLine($"[Recompiler] main function info: TextBase=0x{elfInfo.TextBase:X8} Functions={elfInfo.Functions.Count}");

                mainInstrs = MipsDisasm.Disassemble(mainExe.Code, elfInfo.TextBase);

                funcs = elfInfo.Functions.Count > 0 ? FunctionDetector.DetectFromElf(mainInstrs, elfInfo, "main") : FunctionDetector.DetectFromScan(mainInstrs, elfInfo.LoadAddress, "main");
            }
            else
            {
                Console.WriteLine("[Recompiler] processing main executable");
                mainInstrs = MipsDisasm.Disassemble(mainExe.Code, mainExe.Destination);
                funcs = FunctionDetector.DetectFromScan(mainInstrs, mainExe.InitialPC, "main");
            }

            if (funcs.All(f => f.Start != mainExe.InitialPC))
            {
                var entry = FunctionDetector.DetectFromAddresses(mainInstrs, [(mainExe.InitialPC, null)], funcs, "main");
                funcs.AddRange(entry);
                Console.WriteLine($"[Recompiler] added entry point function at 0x{mainExe.InitialPC:X8}");
            }

            var mainDiscovered = FunctionDetector.DiscoverCalls(mainInstrs, funcs, elfInfo?.NoTypeSymbols ?? [], "main");
            if (mainDiscovered.Count > 0)
            {
                funcs.AddRange(mainDiscovered);
                Console.WriteLine($"[Recompiler] discovered {mainDiscovered.Count} called function(s) in main");
            }

            AddConfigFunctions(funcs, config.Functions, mainInstrs, elfInfo?.NoTypeSymbols ?? [], "main");

            if (config.LinearSweep)
                SweepFunctions(funcs, mainInstrs, elfInfo?.NoTypeSymbols ?? [], "main");

            // analyze jump tables even without an elf/map (linear-sweep games):
            // the table words live in the loaded exe image, readable from TextData
            var jtInfo = elfInfo ?? new FunctionInfo
            {
                LoadAddress = mainExe.Destination,
                TextBase = mainExe.Destination,
                TextData = mainExe.Code,
            };
            AnalyzeJumpTables(funcs, jtInfo, "main");

            ApplyStubsAndIgnored(funcs, config.Stubs, config.Ignored);
            overlayResults.Add(new OverlayResult("main", funcs, -1));
        }

        foreach (var overlayConfig in config.Overlays)
        {
            bool noSymbols = overlayConfig.Elf == null && overlayConfig.Map == null && overlayConfig.FuncMap == null;
            // With no symbol file the overlay still has a source if it has a load address plus
            // either a linear sweep or an explicit function list (the recompiler then walks the
            // call graph out from those roots) — the latter is how data-heavy overlays avoid
            // having megabytes of models swept in as bogus code.
            bool hasRoots = overlayConfig.Base != null &&
                            ((overlayConfig.LinearSweep ?? config.LinearSweep) || overlayConfig.Functions.Length > 0);
            if (noSymbols && !hasRoots)
            {
                Console.WriteLine($"[Recompiler] WARNING: Overlay '{overlayConfig.Name}' has no source defined, this will be skiped");
                continue;
            }
            if (overlayConfig.Elf != null && !File.Exists(overlayConfig.Elf))
            {
                Console.WriteLine($"[Recompiler] WARNING: ELF file not found for overlay '{overlayConfig.Name}' ({overlayConfig.Elf}), this will be skiped.");
                continue;
            }
            if (overlayConfig.Map != null && !File.Exists(overlayConfig.Map))
            {
                Console.WriteLine($"[Recompiler] WARNING: map file not found for overlay '{overlayConfig.Name}' ({overlayConfig.Map}), this will be skiped.");
                continue;
            }
            if (overlayConfig.FuncMap != null)
            {
                if (!File.Exists(overlayConfig.FuncMap))
                {
                    Console.WriteLine($"[Recompiler] WARNING: function map not found for overlay '{overlayConfig.Name}' ({overlayConfig.FuncMap}), this will be skiped.");
                    continue;
                }
                if (overlayConfig.Elf == null && overlayConfig.Map == null && overlayConfig.Base == null)
                {
                    Console.WriteLine($"[Recompiler] WARNING: overlay '{overlayConfig.Name}' uses 'funcMap' alone but has no 'base' address defined, this will be skiped.");
                    continue;
                }
            }

            if (overlayConfig.Elf != null)
                Console.WriteLine($"[Recompiler] processing the overlay {overlayConfig.Name} (WARNING: 'elf' is deprecated, prefer 'map'/'funcMap')");
            else
                Console.WriteLine($"[Recompiler] processing the overlay {overlayConfig.Name}");

            var (discBin, overlayLba) = ResolveOverlay(fs, overlayConfig);
            if (discBin == null)
            {
                Console.WriteLine($"[Recompiler] WARNING: could not resolve disc data for overlay '{overlayConfig.Name}', skipping");
                continue;
            }

            var rawElf = overlayConfig.Elf != null ? ElfReader.Read(overlayConfig.Elf) : null;
            var rawMap = overlayConfig.Map != null ? MapReader.Read(overlayConfig.Map) : null;

            FunctionInfo? rawFuncMap = null;
            if (overlayConfig.FuncMap != null)
            {
                uint funcMapBase = rawElf?.TextBase ?? rawMap?.LoadAddress ?? Convert.ToUInt32(overlayConfig.Base, 16);
                rawFuncMap = FunctionMapLoader.Load(overlayConfig.FuncMap, funcMapBase, discBin);
            }

            var elfInfo = FunctionMapLoader.Merge(rawElf, rawMap, rawFuncMap);
            if (elfInfo.TextData.Length == 0) elfInfo.TextData = discBin;

            if (noSymbols)
            {
                elfInfo.TextBase = Convert.ToUInt32(overlayConfig.Base, 16);
                elfInfo.LoadAddress = elfInfo.TextBase;
            }

            if (overlayConfig.Rebase != 0)
                RebaseElf(elfInfo, overlayConfig.Rebase, discBin);

            var instrs = MipsDisasm.Disassemble(discBin, elfInfo.TextBase);

            //elf is weird and doest properly provide all functions (specially asm) so resort to checking it
            var funcs = elfInfo.Functions.Count > 0 ? FunctionDetector.DetectFromElf(instrs, elfInfo, overlayConfig.Name) : FunctionDetector.DetectFromScan(instrs, elfInfo.LoadAddress, overlayConfig.Name);

            var discovered = FunctionDetector.DiscoverCalls(instrs, funcs, elfInfo.NoTypeSymbols, overlayConfig.Name);
            if (discovered.Count > 0)
            {
                funcs.AddRange(discovered);
                Console.WriteLine($"[Recompiler] discovered {discovered.Count} called funs in {overlayConfig.Name}");
            }

            AddConfigFunctions(funcs, overlayConfig.Functions, instrs, elfInfo.NoTypeSymbols, overlayConfig.Name);

            if (overlayConfig.LinearSweep ?? config.LinearSweep)
                SweepFunctions(funcs, instrs, elfInfo.NoTypeSymbols, overlayConfig.Name);

            AnalyzeJumpTables(funcs, elfInfo, overlayConfig.Name);

            ApplyStubsAndIgnored(funcs, overlayConfig.Stubs.Concat(config.Stubs), overlayConfig.Ignored.Concat(config.Ignored));
            overlayResults.Add(new OverlayResult(overlayConfig.Name, funcs, overlayLba));
        }

        var allFuncs = overlayResults.SelectMany(o => o.Functions).ToList();
        ResolveCollisions(allFuncs);
        ApplyPatches(allFuncs, config.Patches);
        SdkPatches.Apply(allFuncs);

        var uniqueAddrs = allFuncs.GroupBy(f => f.Start).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet();
        var knownFuncs = allFuncs.Where(f => uniqueAddrs.Contains(f.Start))
                                 .ToDictionary(f => f.Start, f => $"{TypeNameFor(className, f.OverlayName)}.{f.EmittedName}");

        var funcOverlay = allFuncs.Where(f => uniqueAddrs.Contains(f.Start))
                                  .ToDictionary(f => f.Start, f => f.OverlayName ?? "");

        int conflictCount = allFuncs.Count - knownFuncs.Count;
        Console.WriteLine($"[Recompiler] total functions: {allFuncs.Count}");

        string? mainCall = null;
        if (config.Main != null)
        {
            uint mainAddr = Convert.ToUInt32(config.Main, 16);
            var mainFunc = allFuncs.FirstOrDefault(f => f.Start == mainAddr);
            if (mainFunc == null) throw new InvalidOperationException($"[recompiler] the main function not found at 0x{mainAddr:X8}");
            mainCall = $"{TypeNameFor(className, mainFunc.OverlayName)}.{mainFunc.EmittedName}";
            Console.WriteLine($"[Recompiler] main: {mainCall} @ 0x{mainAddr:X8}");
        }

        // Stale outputs from a previous run (overlays since removed from the config) would still
        // be compiled by the game project's `Recompiled\**` glob and reference old names — clear them.
        foreach (var stale in Directory.GetFiles(outDir, "*.cs"))
            File.Delete(stale);

        foreach (var result in overlayResults)
        {
            Console.WriteLine($"[Recompiler] emiting {result.Name}.cs ({result.Functions.Count} functions)");
            // A direct call may only be bound statically to a function in the SAME overlay, or
            // in the main executable. Overlays are loaded into a small number of fixed slots, so
            // several of them cover the same addresses with completely different code; binding
            // across overlays let a call in one module land in a module that was never loaded.
            // Cross-overlay targets go through the dispatcher, which resolves whatever is
            // actually resident.
            var scoped = knownFuncs
                .Where(kv => funcOverlay.TryGetValue(kv.Key, out var ov)
                             && (ov == result.Name || ov == "main" || string.IsNullOrEmpty(ov)))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            EmitOverlayFile(result.Name, result.Functions, TypeNameFor(className, result.Name), scoped, config.Debug, result.LbaStart, outDir);
        }

        Console.WriteLine("[Recompiler] Emitting Entry.cs");
        var overlayNames = overlayResults.Select(o => o.Name).ToList();
        EntryWriter.Write(mainExe, sysCfg.BootExe, className, mainCall, overlayNames, outDir);

        Console.WriteLine("[Recompiler] finished "); //maybe add time it took
    }
    
    static void AddConfigFunctions(List<MipsFunction> funcs, Config.FunctionEntry[] entries, MipsInstruction[] instrs, IEnumerable<Symbols.FunctionEntry> noTypeSymbols, string overlayName)
    {
        if (entries.Length == 0) return;

        var byStart = funcs.GroupBy(f => f.Start).ToDictionary(g => g.Key, g => g.First());
        int renamed = 0;
        var have = new HashSet<uint>(funcs.Select(f => f.Start));
        var missing = new List<(uint Addr, string? Name)>();
        foreach (var e in entries)
        {
            uint addr = Convert.ToUInt32(e.Address, 16);
            if (have.Add(addr))
            {
                missing.Add((addr, e.Name));
            }
            else if (e.Name != null && byStart.TryGetValue(addr, out var existingFunc) && existingFunc.Name != e.Name)
            {
                // function was already discovered under an auto name, config name wins
                // (needed so sdk reimplementations can match by name)
                existingFunc.Name = e.Name;
                existingFunc.EmittedName = e.Name;
                renamed++;
            }
        }
        if (renamed > 0)
            Console.WriteLine($"[Recompiler] renamed {renamed} existing function(s) from config in {overlayName}");
        if (missing.Count == 0) return;

        var extras = FunctionDetector.DetectFromAddresses(instrs, missing.Select(e => (e.Addr, e.Name)), funcs, overlayName);
        funcs.AddRange(extras);
        var callees = FunctionDetector.DiscoverCalls(instrs, funcs, noTypeSymbols, overlayName);
        funcs.AddRange(callees);
        Console.WriteLine($"[Recompiler] added {extras.Count} config function(s) (+{callees.Count} callees) to {overlayName}");
    }

    static void SweepFunctions(List<MipsFunction> funcs, MipsInstruction[] instrs, IEnumerable<Symbols.FunctionEntry> noTypeSymbols, string overlayName)
    {
        var swept = FunctionDetector.LinearSweep(instrs, funcs, noTypeSymbols, overlayName);
        if (swept.Count == 0) return;
        funcs.AddRange(swept);
        var callees = FunctionDetector.DiscoverCalls(instrs, funcs, noTypeSymbols, overlayName);
        funcs.AddRange(callees);
        Console.WriteLine($"[Recompiler] linear sweep found {swept.Count} function(s) (+{callees.Count} callees) in {overlayName}");
    }

    static void EmitOverlayFile(string overlayName, List<MipsFunction> funcs, string typeName, Dictionary<uint, string> knownFuncs, bool debug, int lbaStart, string outDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using RecompOne.Runtime.Context;");
        sb.AppendLine("using RecompOne.Runtime.Dispatch;");
        sb.AppendLine("using RecompOne.Runtime.Memory;");
        sb.AppendLine();
        sb.AppendLine("namespace Recompiled;");
        sb.AppendLine();
        sb.AppendLine($"public static partial class {typeName}");
        sb.AppendLine("{");

        foreach (var func in funcs.OrderBy(f => f.Start))
        {
            var labels = LabelManager.Collect(func);
            var ctx = new FunctionContext
            {
                FuncStart = func.Start,
                FuncEnd = func.End,
                KnownFunctions = knownFuncs,
                Labels = labels,
                Debug = debug,
                JumpTablesByJr = func.JumpTables.ToDictionary(j => j.JrVram),
                RaReturnJrs = FunctionDetector.ComputeRaReturnJrs(func)
            };
            sb.Append(FunctionEmitter.Emit(func, ctx));
        }

        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine($"public sealed class {DispatchTableName(overlayName)} : IOverlay");
        sb.AppendLine("{");
        sb.AppendLine($"    public string Name => \"{overlayName}\";");
        sb.AppendLine($"    public int LbaStart => {lbaStart};");
        sb.AppendLine("    public IReadOnlyDictionary<uint, Action<CpuContext, IMemory>> Functions { get; } =");
        sb.AppendLine("        new Dictionary<uint, Action<CpuContext, IMemory>>");
        sb.AppendLine("        {");
        foreach (var func in funcs.Where(f => !f.IsStub).OrderBy(f => f.Start))
            sb.AppendLine($"            [0x{func.Start:X8}u] = {typeName}.{func.EmittedName},");
        sb.AppendLine("        };");
        sb.AppendLine("}");

        File.WriteAllText(Path.Combine(outDir, $"{overlayName}.cs"), sb.ToString());
    }

    /// <summary>
    /// The type that owns an overlay's emitted functions.
    ///
    /// A .NET type may contain at most 65535 methods, and the recompiler emits one static
    /// method per recompiled function. Emitting every overlay's functions into a single
    /// partial class therefore made that CLR limit a hard cap on how much of a game could be
    /// mapped at all. Brave Fencer Musashi hit it: a 28-module scene selection reached 64238
    /// methods, and going past it produced a build that reported no errors and then threw
    /// `TypeLoadException: ... contains more methods than the current implementation allows`
    /// before the first frame -- which reads as a black-screen hang, not as a build failure.
    ///
    /// Splitting per overlay removes the cap, because the limit is per TYPE. Partial *files*
    /// do not help: they all merge into the same type. `main` deliberately keeps the plain
    /// game class name so anything referring to `Recompiled.&lt;Game&gt;` still resolves, and
    /// because a direct call may only bind statically within its own overlay or to main.
    /// </summary>
    static string TypeNameFor(string className, string? overlayName) =>
        string.IsNullOrEmpty(overlayName) || overlayName == "main"
            ? className
            : $"{className}_{SafeIdentifier(overlayName)}";

    static string DispatchTableName(string name) => $"{char.ToUpperInvariant(name[0])}{name[1..]}DispatchTable";

    static void ResolveCollisions(List<MipsFunction> allFuncs)
    {
        var crossOverlayDups = allFuncs.GroupBy(f => f.Name)
            .Where(g => g.Select(f => f.OverlayName).Distinct().Count() > 1)
            .Select(g => g.Key).ToHashSet();

        foreach (var func in allFuncs)
            func.EmittedName = SafeFuncName(crossOverlayDups.Contains(func.Name) && !string.IsNullOrEmpty(func.OverlayName)
                ? $"{func.Name}_{SafeIdentifier(func.OverlayName)}"
                : func.Name);

        foreach (var group in allFuncs.GroupBy(f => (f.OverlayName, f.EmittedName)).Where(g => g.Count() > 1))
            foreach (var func in group)
                func.EmittedName = $"{func.EmittedName}_{func.Start:X8}";
    }

    static string SafeFuncName(string s) => Regex.Replace(s, @"[^A-Za-z0-9_]", "_");

    static (byte[]? data, int lba) ResolveOverlay(CueFs fs, Config.OverlayConfig cfg)
    {
        try
        {
            if (cfg.Bin != null)
            {
                // Host binary image (e.g. RAM dump of a module the disc stores compressed).
                // Resolved relative to the config file like every other config path.
                if (!File.Exists(cfg.Bin)) throw new FileNotFoundException($"overlay '{cfg.Name}': bin not found: {cfg.Bin}");
                byte[] img = File.ReadAllBytes(cfg.Bin);
                int start = cfg.Offset + cfg.Skip;
                int length = cfg.Size ?? (img.Length - start);
                return (img.AsSpan(start, length).ToArray(), cfg.Lba);
            }
            if (cfg.Lba >= 0)
            {
                int sz = cfg.Size ?? throw new InvalidOperationException($"'size' is required when using 'lba' for overlay '{cfg.Name}'");
                return (Decrypt(fs.ReadSectors(cfg.Lba, sz), cfg.Decrypt), cfg.Lba);
            }
            if (cfg.File != null)
            {
                if (!fs.Locate(cfg.File, out int lba, out uint fileSize))
                {
                    Console.WriteLine($"[Recompiler] WARNING: disc file not found: {cfg.File}");
                    return (null, -1);
                }
                int absLba = lba + cfg.Offset / 2048;
                byte[] full = fs.ReadFile(cfg.File);
                int start = cfg.Offset + cfg.Skip;
                int length = cfg.Size ?? (full.Length - start);
                return (Decrypt(full.AsSpan(start, length).ToArray(), cfg.Decrypt), absLba);
            }
            Console.WriteLine($"[Recompiler] WARNING: overlay '{cfg.Name}' has no 'file' or 'lba' source defined");
            return (null, -1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Recompiler] WARNING: failed to resolve disc data for '{cfg.Name}': {ex.Message}");
            return (null, -1);
        }
    }

    
    static void RebaseElf(FunctionInfo elf, int delta, byte[] discBin)
    {
        uint d = (uint)delta;
        elf.TextBase += d;
        elf.LoadAddress += d;
        foreach (var f in elf.Functions) f.Address += d;
        foreach (var f in elf.NoTypeSymbols) f.Address += d;
        foreach (var s in elf.DataSections) s.Va += d;
        elf.TextData = discBin;
    }

    static byte[] Decrypt(byte[] data, bool decrypt)
    {
        if (!decrypt) return data;
        uint seed = 0;
        for (int i = 0; i + 4 <= data.Length; i += 4)
        {
            seed = (seed + 0x01309125u) * 0x03A452F7u;
            uint w = BitConverter.ToUInt32(data, i) ^ seed;
            BitConverter.GetBytes(w).CopyTo(data, i);
        }
        return data;
    }
    static void AnalyzeJumpTables(List<MipsFunction> funcs, FunctionInfo elf, string name)
    {
        int funcsWithTables = 0, totalEntries = 0;
        foreach (var func in funcs)
        {
            func.JumpTables = JumpTableAnalyzer.Analyze(func, elf);
            if (func.JumpTables.Count == 0) continue;
            funcsWithTables++;
            foreach (var jt in func.JumpTables) totalEntries += jt.Entries.Length;
        }
        if (funcsWithTables > 0)
            Console.WriteLine($"[Recompiler] {name}: found jump tables in {funcsWithTables} function(s), {totalEntries} entries in total");
    }
    
    static void ApplyPatches(List<MipsFunction> funcs, Config.PatchEntry[] patches)
    {
        if (patches.Length == 0) return;
        int applied = 0;
        foreach (var patch in patches)
        {
            uint? addr = string.IsNullOrEmpty(patch.Address) ? null : Convert.ToUInt32(patch.Address, 16);
            int matched = 0;
            foreach (var func in funcs)
            {
                if (!string.IsNullOrEmpty(patch.Overlay) && !string.Equals(func.OverlayName, patch.Overlay, StringComparison.OrdinalIgnoreCase)) continue;
                bool hit = addr.HasValue ? func.Start == addr.Value : string.Equals(func.Name, patch.Function, StringComparison.Ordinal);
                if (!hit) continue;
                switch (patch.Mode.ToLowerInvariant())
                {
                    case "pre":
                        func.PreHookTarget = patch.Target;
                        break;
                    case "post":
                        func.PostHookTarget = patch.Target;
                        break;
                    // Keeps the original body and records what it does. Deliberately takes no
                    // target: unlike the other modes this one adds an observer, not a
                    // replacement, so there is nothing for the config to point at.
                    case "verify":
                        func.IsVerify = true;
                        break;
                    default:
                        func.IsPatch = true;
                        func.PatchTarget = patch.Target;
                        break;
                }
                matched++;
                applied++;
            }
            if (matched == 0)
                Console.WriteLine($"[Recompiler] WARNING: patch '{patch.Target}' matched nothing (overlay='{patch.Overlay}' function='{patch.Function}' address='{patch.Address}')");
        }
        Console.WriteLine($"[Recompiler] applied {applied} patches");
    }

    static void ApplyStubsAndIgnored(List<MipsFunction> funcs, IEnumerable<string> stubs, IEnumerable<string> ignored)
    {
        var stubSet = new HashSet<string>(stubs,   StringComparer.OrdinalIgnoreCase);
        var ignoredSet = new HashSet<string>(ignored, StringComparer.OrdinalIgnoreCase);
        foreach (var func in funcs)
        {
            if (ignoredSet.Contains(func.Name)) { func.IsStub = true; func.Name = "__ignored__"; }
            else if (stubSet.Contains(func.Name)) func.IsStub = true;
        }
        funcs.RemoveAll(f => f.Name == "__ignored__");
    }
    
    
    
    static string SafeIdentifier(string s) => Regex.Replace(s, @"[^a-zA-Z0-9_]", "_").TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
    
    
}
