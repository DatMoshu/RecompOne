using System.IO;
using System.Runtime.InteropServices;
using RecompOne.Runtime.Hle;

namespace RecompOne.Runtime;

//ToDO: HW Renderer must use a separate "view" quad text so widescren patches are possible
// or find a betrter approach, maybe allongate the vram as needed? a dufferent tex seens more ideal?
public sealed partial class Gpu
{
    public const int VramWidth = VramShadow.Width;
    public const int VramHeight = VramShadow.Height;

    public readonly VramShadow Shadow = new();
    public ushort[] Vram => Shadow.Pixels;

    int _drawAreaLeft, _drawAreaTop, _drawAreaRight = VramWidth - 1, _drawAreaBottom = VramHeight - 1;
    int _drawOffsetX, _drawOffsetY;

    int _texPageX, _texPageY;
    int _texDepth;
    int _blendMode;
    bool _dither;
    bool _texDisable;

    int _texWinMaskX, _texWinMaskY, _texWinOffX, _texWinOffY;

    bool _setMask, _checkMask;

    int _dispVramX, _dispVramY;
    int _hRange1 = 0x200, _hRange2 = 0xC00, _vRange1 = 0x10, _vRange2 = 0x100;
    int _hres;
    bool _hres368, _vres480, _pal, _disp24, _interlace, _displayDisabled = true;
    int _dmaDir;

    readonly List<uint> _fifo = new(16);
    int _need;
    bool _polyline;

    bool _loadImage;
    int _loadX, _loadY, _loadW, _loadH, _loadPx;

    bool _readImage;
    int _readX, _readY, _readW, _readH, _readPx;
    uint _gpuRead;

    bool _statField;

    public int DisplayX => _dispVramX;
    public int DisplayY => _dispVramY;
    public bool DisplayEnabled => !_displayDisabled;
    public bool Display24Bit => _disp24;
    public bool Pal => _pal;

    // Raw GP0(E3h)/GP0(E4h)/GP0(E5h) draw area + offset and GP1(06h)/GP1(07h) display
    // range, exposed read-only for diagnostics (DumpAll / CaptureData).
    public int DrawAreaLeft => _drawAreaLeft;
    public int DrawAreaTop => _drawAreaTop;
    public int DrawAreaRight => _drawAreaRight;
    public int DrawAreaBottom => _drawAreaBottom;
    public int DrawOffsetX => _drawOffsetX;
    public int DrawOffsetY => _drawOffsetY;
    public int HRange1 => _hRange1;
    public int HRange2 => _hRange2;
    public int VRange1 => _vRange1;
    public int VRange2 => _vRange2;

    int CyclesPerPixel => _hres368 ? 7 : _hres switch { 0 => 10, 1 => 8, 2 => 5, _ => 4 };

    public int DisplayWidth
    {
        get
        {
            int w = ((_hRange2 - _hRange1) / CyclesPerPixel + 2) & ~3;
            return Math.Clamp(w, 0, VramWidth);
        }
    }

    public int DisplayHeight
    {
        get
        {
            int lines = _vRange2 - _vRange1;
            if (_vres480) lines <<= 1;
            return Math.Clamp(lines, 0, VramHeight);
        }
    }

    public uint ReadStat() 
    {
        uint s = 0;
        s |= (uint)((_texPageX / 64) & 0xF);
        s |= (uint)(((_texPageY / 256) & 1) << 4);
        s |= (uint)((_blendMode & 3) << 5);
        s |= (uint)((_texDepth & 3) << 7);
        
        if (_dither) s |= 1u << 9;
        s |= 1u << 10;
        if (_setMask) s |= 1u << 11;
        if (_checkMask) s |= 1u << 12;
        s |= 1u << 13;
        
        if (_texDisable) s |= 1u << 15;
        if (_hres368) s |= 1u << 16;
        
        s |= (uint)((_hres & 3) << 17);
        
        if (_vres480) s |= 1u << 19;
        if (_pal) s |= 1u << 20;
        if (_disp24) s |= 1u << 21;
        if (_interlace) s |= 1u << 22;
        if (_displayDisabled) s |= 1u << 23;
        
        s |= 1u << 26;
        s |= 1u << 27;
        s |= 1u << 28;
        s |= (uint)((_dmaDir & 3) << 29);
        s |= _dmaDir switch { 1 => 1u << 25, 2 => 1u << 28, 3 => 1u << 27, _ => 0u };
        
        _statField = !_statField;
        if (_statField) s |= 1u << 31;
        return s;
    }

    public uint ReadData()
    {
        if (!_readImage) return _gpuRead;
        ushort lo = ReadImageHalfword();
        ushort hi = ReadImageHalfword();
        return (uint)(lo | (hi << 16));
    }

    public long Gp0Words, Gp1Words; // diagnostic counters (host-visible)

    public void WriteGp0(uint word)
    {
        Gp0Words++;
        if (_loadImage) { StoreImageHalfword((ushort)word); StoreImageHalfword((ushort)(word >> 16)); return; }
        if (_polyline)
        {
            if ((word & 0xF000F000u) == 0x50005000u) { _polyline = false; ExecutePolyline(); _fifo.Clear(); }
            else _fifo.Add(word);
            return;
        }

        _fifo.Add(word);
        if (_fifo.Count == 1)
        {
            _need = CommandLength(word);
            if (_need == LenPolyline) { _polyline = true; return; }
            if (_need == LenImageLoad) _need = 3;
        }

        if (_fifo.Count >= _need) { Execute(); if (!_loadImage) _fifo.Clear(); }
    }

    public void WriteGp1(uint word)
    {
        Gp1Words++;
        uint op = (word >> 24) & 0xFF;
        uint p = word & 0xFFFFFF;
        switch (op)
        {
            case >= 0x05 and <= 0x08:
                WriteGp1Display(op, p);
                GpuHle.NotifyDisplay(_dispVramX, _dispVramY, DisplayWidth, DisplayHeight);
                return;
            case 0x00: Reset(); break;
            case 0x01: _fifo.Clear(); _polyline = false; _loadImage = false; break;
            case 0x02: break;
            case 0x03: _displayDisabled = (p & 1) != 0; break;
            case 0x04: _dmaDir = (int)(p & 3); break;
            case 0x10: SetGpuInfo(p); break;
        }
    }

    void WriteGp1Display(uint op, uint p)
    {
        switch (op)
        {
            case 0x05: _dispVramX = (int)(p & 0x3FF); _dispVramY = (int)((p >> 10) & 0x1FF); break;
            case 0x06: _hRange1 = (int)(p & 0xFFF); _hRange2 = (int)((p >> 12) & 0xFFF); break;
            case 0x07: _vRange1 = (int)(p & 0x3FF); _vRange2 = (int)((p >> 10) & 0x3FF); break;
            case 0x08:
                _hres = (int)(p & 3);
                _hres368 = (p & 0x40) != 0;
                _vres480 = (p & 4) != 0;
                _pal = (p & 8) != 0;
                _disp24 = (p & 0x10) != 0;
                _interlace = (p & 0x20) != 0;
                break;
        }
    }

    void Reset()
    {
        _fifo.Clear();
        _polyline = _loadImage = _readImage = false;
        _displayDisabled = true;
        _dmaDir = 0;
        _texPageX = _texPageY = _texDepth = _blendMode = 0;
        _dither = _texDisable = false;
        _texWinMaskX = _texWinMaskY = _texWinOffX = _texWinOffY = 0;
        _drawAreaLeft = _drawAreaTop = 0;
        _drawAreaRight = VramWidth - 1;
        _drawAreaBottom = VramHeight - 1;
        _drawOffsetX = _drawOffsetY = 0;
        _setMask = _checkMask = false;
        _dispVramX = _dispVramY = 0;
    }

    void SetGpuInfo(uint p)
    {
        switch (p & 0xFF)
        {
            case 0x03: _gpuRead = (uint)(_drawAreaLeft | (_drawAreaTop << 10)); break;
            case 0x04: _gpuRead = (uint)(_drawAreaRight | (_drawAreaBottom << 10)); break;
            case 0x05: _gpuRead = (uint)((_drawOffsetX & 0x7FF) | ((_drawOffsetY & 0x7FF) << 11)); break;
            default: _gpuRead = 0; break;
        }
    }

    // Savestate: VRAM plus the persistent draw/display register state. The GP0 FIFO
    // and in-flight image-load/read cursors are transient (only live mid-command) and
    // are not captured — a savestate is taken at a frame boundary.
    public void SaveState(BinaryWriter w)
    {
        w.Write(MemoryMarshal.AsBytes<ushort>(Shadow.Pixels));
        w.Write(_drawAreaLeft); w.Write(_drawAreaTop); w.Write(_drawAreaRight); w.Write(_drawAreaBottom);
        w.Write(_drawOffsetX); w.Write(_drawOffsetY);
        w.Write(_texPageX); w.Write(_texPageY); w.Write(_texDepth); w.Write(_blendMode);
        w.Write(_dither); w.Write(_texDisable);
        w.Write(_texWinMaskX); w.Write(_texWinMaskY); w.Write(_texWinOffX); w.Write(_texWinOffY);
        w.Write(_setMask); w.Write(_checkMask);
        w.Write(_dispVramX); w.Write(_dispVramY);
        w.Write(_hRange1); w.Write(_hRange2); w.Write(_vRange1); w.Write(_vRange2);
        w.Write(_hres); w.Write(_hres368); w.Write(_vres480); w.Write(_pal);
        w.Write(_disp24); w.Write(_interlace); w.Write(_displayDisabled);
        w.Write(_dmaDir); w.Write(_gpuRead); w.Write(_statField);
    }

    public void LoadState(BinaryReader r)
    {
        Span<byte> vram = MemoryMarshal.AsBytes<ushort>(Shadow.Pixels.AsSpan());
        int read = 0;
        while (read < vram.Length) read += r.Read(vram[read..]);
        _drawAreaLeft = r.ReadInt32(); _drawAreaTop = r.ReadInt32(); _drawAreaRight = r.ReadInt32(); _drawAreaBottom = r.ReadInt32();
        _drawOffsetX = r.ReadInt32(); _drawOffsetY = r.ReadInt32();
        _texPageX = r.ReadInt32(); _texPageY = r.ReadInt32(); _texDepth = r.ReadInt32(); _blendMode = r.ReadInt32();
        _dither = r.ReadBoolean(); _texDisable = r.ReadBoolean();
        _texWinMaskX = r.ReadInt32(); _texWinMaskY = r.ReadInt32(); _texWinOffX = r.ReadInt32(); _texWinOffY = r.ReadInt32();
        _setMask = r.ReadBoolean(); _checkMask = r.ReadBoolean();
        _dispVramX = r.ReadInt32(); _dispVramY = r.ReadInt32();
        _hRange1 = r.ReadInt32(); _hRange2 = r.ReadInt32(); _vRange1 = r.ReadInt32(); _vRange2 = r.ReadInt32();
        _hres = r.ReadInt32(); _hres368 = r.ReadBoolean(); _vres480 = r.ReadBoolean(); _pal = r.ReadBoolean();
        _disp24 = r.ReadBoolean(); _interlace = r.ReadBoolean(); _displayDisabled = r.ReadBoolean();
        _dmaDir = r.ReadInt32(); _gpuRead = r.ReadUInt32(); _statField = r.ReadBoolean();
    }
}
