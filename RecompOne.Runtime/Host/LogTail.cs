using System.Text;

namespace RecompOne.Runtime.Host;

/// <summary>
/// Keeps the tail of everything written to the console so the host window can show it.
///
/// Every diagnostic in this engine already goes through Console.WriteLine -- Log.Cd/Sdk/Bios,
/// the per-300-frame "[bfm] frame N" lines, "[Dispatcher] loaded overlay", "[card] gate=",
/// the libcd debug the games themselves print through the BIOS write call. So this TEES
/// Console.Out rather than adding a logging API: no call site changes, nothing can be
/// forgotten, and output still reaches the real console and any redirected file exactly as
/// before. That last part matters -- the automated runs harvest crashes by redirecting
/// stdout, and a capture that swallowed lines would break them silently.
///
/// The point of having it on screen is that a screenshot of a misbehaving run then carries
/// its own evidence. Several bugs on this project were diagnosed only after someone went
/// back for a log that no longer existed.
/// </summary>
public static class LogTail
{
    public const int Capacity = 4000;

    static readonly object _lock = new();
    static readonly string[] _lines = new string[Capacity];
    static int _count, _next;
    static bool _installed;

    /// <summary>Total lines ever seen; the panel uses it to detect new output.</summary>
    public static long Total { get; private set; }

    public static void Install()
    {
        lock (_lock)
        {
            if (_installed) return;
            _installed = true;
            Console.SetOut(new TeeWriter(Console.Out));
        }
    }

    public static void Add(string line)
    {
        lock (_lock)
        {
            _lines[_next] = line;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
            Total++;
        }
    }

    /// <summary>
    /// Oldest-to-newest copy of the retained tail, optionally substring-filtered, keeping at
    /// most <paramref name="max"/> of the NEWEST matching lines.
    ///
    /// Walks BACKWARDS and stops as soon as it has enough. Copying the whole 4000-line ring
    /// every frame is what made this panel a performance bug on its first outing: PE1 prints
    /// card-poll lines continuously, so the ring is always full, and an unbounded copy plus
    /// the allocation it needs ran on the UI thread while the writer thread contended for
    /// the same lock. Bounding the work is the fix; the buffer size is not the problem.
    /// </summary>
    public static List<string> Snapshot(string? filter = null, int max = int.MaxValue)
    {
        var outp = new List<string>(Math.Min(max, 256));
        lock (_lock)
        {
            for (int i = _count - 1; i >= 0 && outp.Count < max; i--)
            {
                var s = _lines[(_next - _count + i + Capacity) % Capacity];
                if (s is null) continue;
                if (!string.IsNullOrEmpty(filter) &&
                    s.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                outp.Add(s);
            }
        }
        outp.Reverse();
        return outp;
    }

    /// <summary>Lines currently retained (cheap; for the panel's counter).</summary>
    public static int Count { get { lock (_lock) return _count; } }

    public static void Clear()
    {
        lock (_lock) { _count = 0; _next = 0; Array.Clear(_lines); }
    }

    /// <summary>
    /// Forwards to the original writer verbatim and splits a copy into lines. Console.Write
    /// arrives as fragments (Write(char) at a time in places), so partial text is held until
    /// a newline instead of being recorded as its own line.
    /// </summary>
    sealed class TeeWriter(TextWriter inner) : TextWriter
    {
        readonly StringBuilder _pending = new();

        public override Encoding Encoding => inner.Encoding;
        public override IFormatProvider FormatProvider => inner.FormatProvider;

        public override void Write(char value)
        {
            inner.Write(value);
            lock (_pending)
            {
                if (value == '\n') { Flush(_pending); }
                else if (value != '\r') _pending.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            inner.Write(value);
            lock (_pending)
            {
                foreach (var ch in value)
                {
                    if (ch == '\n') Flush(_pending);
                    else if (ch != '\r') _pending.Append(ch);
                }
            }
        }

        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);
            lock (_pending)
            {
                // Anything buffered by a preceding partial Write belongs to this line.
                if (_pending.Length > 0) { _pending.Append(value); Flush(_pending); }
                else Add(value ?? string.Empty);
            }
        }

        public override void Flush() => inner.Flush();

        static void Flush(StringBuilder sb)
        {
            Add(sb.ToString());
            sb.Clear();
        }
    }
}
