using System.Text;
using RecompOne.Recompiler.Analysis;
using RecompOne.Recompiler.Disasm;

namespace RecompOne.Recompiler.CodeGen;

public static class FunctionEmitter
{
    public static string Emit(MipsFunction func, FunctionContext ctx)
    {
        var sb = new StringBuilder();
        var instrs = func.Instructions;
        
        //a delay slot of an unconditional transfer is emitted only inline (before the
        // jump) and skipped here but  for the edge case where that same instruction is also a branch target it needs to
        // also be emitted at its ""natural(?)"" position (and the label too) so jumps to it need to be into the following
        //  instructions instead of into the preceding jump in that case it shouldnt be skiped, otherwise it will be emmited in the wrong location and cause crashes
        // on the functions with this edge case
        var dsIdx = new HashSet<int>();
        for (int i = 0; i < instrs.Length - 1; i++)
            if (instrs[i].HasDelaySlot && InstructionEmitter.SkipDelaySlot(instrs[i])
                && !ctx.Labels.Contains(instrs[i + 1].Vram))
                dsIdx.Add(i + 1);

        string name = func.EmittedName;
        const string ind = "        ";

        if (func.IsStub)
        {
            sb.AppendLine($"    public static void {name}(CpuContext c, IMemory m) {{ }}");
            return sb.ToString();
        }
        if (func.IsPatch)
        {
            sb.AppendLine($"    public static void {name}(CpuContext c, IMemory m) => {func.PatchTarget}(c, m);");
            return sb.ToString();
        }
        // Verified function: the PUBLIC name becomes a gate into the oracle and the real body
        // moves to a private name. Every existing call site -- including the direct static calls
        // the emitter uses within an overlay, which never touch the dispatcher -- routes through
        // it with no change at the call site. The delegate is cached in a static field so a
        // gated call allocates nothing.
        if (func.IsVerify)
        {
            sb.AppendLine($"    static readonly Action<CpuContext, IMemory> {name}_VD = {name}_V;");
            sb.AppendLine($"    public static void {name}(CpuContext c, IMemory m)");
            sb.AppendLine($"        => RecompOne.Runtime.Verify.Oracle.Gate(0x{func.Start:X8}u, c, m, {name}_VD);");
            name += "_V";
        }
        if (func.PostHookTarget.Length > 0)
        {
            sb.AppendLine($"    public static void {name}(CpuContext c, IMemory m)");
            sb.AppendLine("    {");
            sb.AppendLine($"        {name}_Impl(c, m);");
            sb.AppendLine($"        {func.PostHookTarget}(c, m);");
            sb.AppendLine("    }");
            name += "_Impl";
        }

        sb.AppendLine($"    public static void {name}(CpuContext c, IMemory m)");
        sb.AppendLine("    {");
        if (func.PreHookTarget.Length > 0)
            sb.AppendLine($"        {func.PreHookTarget}(c, m);");
        if (ctx.Debug)
            sb.AppendLine($"        RecompOne.Runtime.Trace.Enter(0x{func.Start:X8}u);");

        for (int i = 0; i < instrs.Length; i++)
        {
            if (dsIdx.Contains(i)) continue;

            var instr = instrs[i];

            if (ctx.Labels.Contains(instr.Vram))
                sb.AppendLine($"        L{instr.Vram:X8}: ;");

            if (instr.HasDelaySlot)
            {
                var delaySlot = i + 1 < instrs.Length ? instrs[i + 1] : null;
                InstructionEmitter.EmitWithDelaySlot(sb, instr, delaySlot, ctx, ind);
            }else
            {
                string line = InstructionEmitter.EmitSingle(instr);
                if (!string.IsNullOrEmpty(line))
                    sb.AppendLine($"{ind}{line}");
            }
        }

        if (FallsThrough(instrs))
        {
            if (ctx.KnownFunctions.TryGetValue(func.End, out var fallthroughName))
                sb.AppendLine($"{ind}{fallthroughName}(c, m);");
            else
                sb.AppendLine($"{ind}Dispatcher.Call(c, m, 0x{func.End:X8}u);");
        }

        sb.AppendLine("    }");
        return sb.ToString();
    }

    //some hand-written asm (crt0 stubs, etc) has no jr/j/branch at its declared end at all and just
    // runs straight into the next symbol if a jal at the end is a call, not a fall-through, so its left alone
    static bool FallsThrough(MipsInstruction[] instrs)
    {
        if (instrs.Length == 0) return false;

        int idx = instrs.Length - 1;
        if (instrs.Length >= 2 && instrs[idx - 1].HasDelaySlot) idx--;

        var ctrl = instrs[idx];
        if (ctrl.IsReturn || ctrl.IsJump || ctrl.IsRegisterJump || ctrl.IsUnconditionalBranch) return false;
        if (ctrl.IsFunctionCall) return false;
        return true;
    }
}
