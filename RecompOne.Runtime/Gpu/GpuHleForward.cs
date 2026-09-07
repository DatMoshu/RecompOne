using RecompOne.Runtime.Hle;

namespace RecompOne.Runtime;

public sealed partial class Gpu
{
    static bool HleOn => GpuHle.Active && GpuHle.Backend is { Ready: true };

    int CurTPage() => ((_texPageX / 64) & 0xf) | (((_texPageY / 256) & 1) << 4)
                    | ((_blendMode & 3) << 5) | ((_texDepth & 3) << 7);

    HleDrawEnv CurEnv() => new()
    {
        ClipX0 = _drawAreaLeft, ClipY0 = _drawAreaTop, ClipX1 = _drawAreaRight, ClipY1 = _drawAreaBottom,
        TwMaskX = _texWinMaskX, TwMaskY = _texWinMaskY, TwOffX = _texWinOffX, TwOffY = _texWinOffY,
        SetMask = _setMask, CheckMask = _checkMask, Dither = _dither,
    };

    static HleVertex HV(in Vert v) => new()
    {
        X = v.X, Y = v.Y, R = (byte)v.R, G = (byte)v.G, B = (byte)v.B, U = (short)v.U, V = (short)v.V,
    };

    PrimFlags PrimOf(bool tex, bool semi, bool raw, int clut) => new()
    {
        Textured = tex, SemiTrans = semi, RawTexture = raw, TPage = (ushort)CurTPage(), Clut = (ushort)clut,
    };

    void HleTri(in Vert a, in Vert b, in Vert c, bool tex, bool semi, bool raw, int clut)
    {
        int spanX = Math.Max(a.X, Math.Max(b.X, c.X)) - Math.Min(a.X, Math.Min(b.X, c.X));
        int spanY = Math.Max(a.Y, Math.Max(b.Y, c.Y)) - Math.Min(a.Y, Math.Min(b.Y, c.Y));
        if (spanX > 1023 || spanY > 511) return;

        var be = GpuHle.Backend!;
        be.SetDrawEnv(CurEnv());
        var ha = HV(a); var hb = HV(b); var hc = HV(c);
        // Room/model exporter hook (--export-room): off by default, zero cost. This is the single
        // funnel every triangle passes through regardless of backend, so it's where the GTE's 3D
        // capture (GteCapture.OnRtp) gets correlated with the material/UV data assembled here.
        // The texture window and the semi-transparency flag travel with the triangle because the
        // exporter has to bake the exact texels the GPU would fetch: the window rewrites UVs
        // before the fetch, and the per-texel STP bit only blends when the primitive is semi.
        if (GteCapture.Enabled)
            GteCapture.OnTriangle(ha, hb, hc, tex, (ushort)CurTPage(), (ushort)clut,
                Export.TextureBaker.PackTexWindow(_texWinMaskX, _texWinMaskY, _texWinOffX, _texWinOffY), semi);
        be.DrawTri(ha, hb, hc, PrimOf(tex, semi, raw, clut));
        // Post-raster half of the same hook: a screen-space primitive drawn AFTER 3D geometry
        // has started is late-arriving backdrop, and a listener can only read back what it
        // painted once it has been painted. No-op unless something is listening.
        if (GteCapture.Enabled) GteCapture.AfterTriangleDrawn();
        OnTriangleDrawn?.Invoke();
    }

    void HleRect(int x, int y, int w, int h, int u, int v, int clut, int r, int g, int b, bool tex, bool semi, bool raw)
    {
        var be = GpuHle.Backend!;
        be.SetDrawEnv(CurEnv());
        be.DrawRect(new HleRect { X = x, Y = y, W = w, H = h, U = (short)u, V = (short)v, R = (byte)r, G = (byte)g, B = (byte)b },
            PrimOf(tex, semi, raw, clut));
        // Sprites are the third path a backdrop can reach the framebuffer by, and the one no
        // GTE-correlating capture can see. See Gpu/GpuVramAccess.cs. Null unless something asked.
        OnRectDrawn?.Invoke(x, y, w, h, tex, (ushort)CurTPage(), (ushort)clut);
    }

    void HleLine(int x0, int y0, int r0, int g0, int b0, int x1, int y1, int r1, int g1, int b1, bool semi)
    {
        if (Math.Abs(x1 - x0) > 1023 || Math.Abs(y1 - y0) > 511) return;

        var be = GpuHle.Backend!;
        be.SetDrawEnv(CurEnv());
        be.DrawLine(
            new HleVertex { X = x0, Y = y0, R = (byte)r0, G = (byte)g0, B = (byte)b0 },
            new HleVertex { X = x1, Y = y1, R = (byte)r1, G = (byte)g1, B = (byte)b1 },
            PrimOf(false, semi, false, 0));
    }

    void HleFill(int x, int y, int w, int h, ushort color) => GpuHle.Backend!.FillRect(x, y, w, h, color);
    void HleCopy(int sx, int sy, int dx, int dy, int w, int h) => GpuHle.Backend!.CopyVram(sx, sy, dx, dy, w, h);

    ushort[] _readBuf = Array.Empty<ushort>();

    void HleReadback(int x, int y, int w, int h)
    {
        int n = w * h;
        if (_readBuf.Length < n) _readBuf = new ushort[n];
        GpuHle.Backend!.ReadVram(x, y, w, h, _readBuf);
    }

    //img load
    ushort[] _hleLoad = Array.Empty<ushort>();
    bool _hleLoadActive;
    int _hleLoadPos;

    void HleLoadBegin()
    {
        _hleLoadActive = HleOn;
        if (!_hleLoadActive) return;
        int n = _loadW * _loadH;
        if (_hleLoad.Length < n) _hleLoad = new ushort[n];
        _hleLoadPos = 0;
    }

    void HleLoadPut(ushort value)
    {
        if (_hleLoadActive && _hleLoadPos < _hleLoad.Length) _hleLoad[_hleLoadPos++] = value;
    }

    void HleLoadFlush()
    {
        if (!_hleLoadActive) return;
        GpuHle.Backend!.WriteVram(_loadX, _loadY, _loadW, _loadH, _hleLoad.AsSpan(0, _loadW * _loadH));
        _hleLoadActive = false;
    }
}
