using RecompOne.Recompiler.Disasm;

namespace RecompOne.Recompiler.Analysis;

public sealed class MipsFunction
{
    public string Name = "";
    public string OverlayName = "";
    public uint Start;
    public uint End;
    public MipsInstruction[] Instructions = [];
    public string EmittedName = "";
    public bool IsStub;
    public bool IsPatch;
    /// <summary>Emit the body under a private name and route the public name through the
    /// differential oracle, so every existing call site records instead of changing.</summary>
    public bool IsVerify;
    public string PatchTarget = "";
    public string PreHookTarget = "";
    public string PostHookTarget = "";
    public List<JumpTable> JumpTables = [];
}
