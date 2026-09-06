namespace RecompOne.Runtime.Hardware;

/// <summary>
/// Controller/memory-card serial port (SIO0, 0x1F801040..0x1F80104F).
///
/// Most PS1 games reach the pad through libpad or the BIOS pad buffers, which the runtime
/// serves from <see cref="Controller"/> directly. Games that link their own copy of the
/// kernel pad driver (Brave Fencer Musashi, and Parasite Eve's Square-custom driver) instead
/// drive this port byte by byte, so without it their controller reads return nothing and the
/// game never sees a button press.
///
/// Only the digital-pad exchange on port 1 is emulated:
///   host 0x01 -> 0xFF        (address: "controller, are you there")
///   host 0x42 -> 0x41        (read command; 0x41 = digital pad id low byte)
///   host 0x00 -> 0x5A        (id high byte / "ready")
///   host 0x00 -> buttons low  (active low: Select L3 R3 Start Up Right Down Left)
///   host 0x00 -> buttons high (active low: L2 R2 L1 R1 Triangle Circle Cross Square)
/// which is exactly the layout <see cref="Controller.State"/> already stores. Any other
/// address byte (0x81 = memory card, handled by the BIOS card HLE) and port 2 simply do not
/// answer: no data, no acknowledge, which is what an empty port looks like.
/// </summary>
public sealed class SioPad
{
    public const uint Base = 0x1F801040u;
    public const uint End = 0x1F801050u;
    public static bool InRange(uint phys) => phys >= Base && phys < End;

    // JOY_CTRL bits
    const ushort CtrlTxEn = 1 << 0;
    const ushort CtrlSelect = 1 << 1;   // /JOY_output — asserted for the whole exchange
    const ushort CtrlAck = 1 << 4;   // write 1 to acknowledge (clears the IRQ flag)
    const ushort CtrlReset = 1 << 6;
    const ushort CtrlAckIrqEn = 1 << 12;
    const ushort CtrlSlot2 = 1 << 13;

    /// <summary>Diagnostic: bytes exchanged, and completed controller reads.</summary>
    public long Bytes, PadReads;
    /// <summary>Log the first N port operations (--padlog) to work out a driver's handshake.</summary>
    public static int LogOps;
    static uint _lastLoggedStat = 0xFFFFFFFF;
    /// <summary>Expose the pending-interrupt flag in JOY_STAT (experiment toggle).</summary>
    public static bool ReportIrqBit = true;
    /// <summary>Expose the /ACK line level in JOY_STAT (experiment toggle).</summary>
    public static bool ReportAckBit = true;
    /// <summary>Answer the address byte with the pad id instead of idle 0xFF (experiment).</summary>
    public static bool IdOnAddress;

    readonly Action<int> _raiseIrq;

    ushort _ctrl, _mode, _baud;
    byte _rx;
    bool _rxFull;
    bool _irq;
    bool _ack;
    int _step = -1;      // -1 = idle (no device addressed yet), else index into the exchange
    bool _padSelected;   // the 0x01 address byte reached a controller on this port

    /// <summary>A device is addressed and part-way through its response.</summary>
    public bool TransferActive => _step >= 0;

    public SioPad(Action<int> raiseIrq) => _raiseIrq = raiseIrq;

    public uint Read(uint phys, int size)
    {
        switch (phys - Base)
        {
            case 0: // JOY_RX_DATA
            {
                byte v = _rxFull ? _rx : (byte)0xFF;
                if (LogOps > 0) { LogOps--; Console.WriteLine($"[sio] RX read 0x{v:X2} (full={_rxFull})"); }
                _rxFull = false;
                return v;
            }
            case 4: case 5: case 6: case 7: // JOY_STAT
            {
                uint stat = 1u                       // bit0: TX ready / FIFO empty
                          | (_rxFull ? 1u << 1 : 0)  // bit1: RX FIFO has a byte
                          | 1u << 2                  // bit2: TX finished
                          | (ReportAckBit && _ack ? 1u << 7 : 0) // bit7: /ACK input level
                          | (ReportIrqBit && _irq ? 1u << 9 : 0); // bit9: interrupt request
                if (LogOps > 0 && stat != _lastLoggedStat)
                {
                    LogOps--; _lastLoggedStat = stat;
                    Console.WriteLine($"[sio] STAT -> 0x{stat:X4} (rxFull={_rxFull} ack={_ack} irq={_irq} step={_step})");
                }
                _ack = false; // /ACK is a brief pulse: observed once, then gone
                return stat;
            }
            case 8: case 9: return _mode;
            case 10: case 11: return _ctrl;
            case 14: case 15: return _baud;
            default: return 0;
        }
    }

    public void Write(uint phys, uint value, int size)
    {
        switch (phys - Base)
        {
            case 0: Transfer((byte)value); break;
            case 8: _mode = (ushort)value; break;
            case 10: WriteCtrl((ushort)value); break;
            case 14: _baud = (ushort)value; break;
        }
    }

    void WriteCtrl(ushort v)
    {
        if (LogOps > 0) { LogOps--; Console.WriteLine($"[sio] CTRL <- 0x{v:X4} (step={_step} sel={_padSelected})"); }
        if ((v & CtrlAck) != 0) { _irq = false; _ack = false; }
        if ((v & CtrlReset) != 0)
        {
            _mode = 0; _baud = 0; _rxFull = false; _irq = false; _ack = false;
            _step = -1; _padSelected = false;
            _ctrl = 0;
            return;
        }
        // Dropping /select ends the exchange: the next byte starts a fresh addressing round.
        if ((v & CtrlSelect) == 0) { _step = -1; _padSelected = false; }
        _ctrl = v;
    }

    void Transfer(byte tx)
    {
        Bytes++;
        _ack = false;
        if ((_ctrl & (CtrlTxEn | CtrlSelect)) != (CtrlTxEn | CtrlSelect))
        {
            _rx = 0xFF; _rxFull = true;
            return;
        }
        // Port 2 is empty: no response byte and, crucially, no acknowledge, so the driver's
        // "is anything there" timeout expires instead of it waiting forever.
        if ((_ctrl & CtrlSlot2) != 0)
        {
            _rx = 0xFF; _rxFull = true; _step = -1;
            return;
        }

        byte reply = 0xFF;
        bool ack = false;
        if (_step < 0)
        {
            // Address byte. 0x01 = controller; anything else (0x81 = memory card) is not
            // answered here, so the port looks empty for that device.
            _padSelected = tx == 0x01;
            // Some drivers expect the pad's id in the response to the address byte rather
            // than the idle 0xFF that a real pad returns while it is still decoding.
            reply = _padSelected && IdOnAddress ? (byte)0x41 : (byte)0xFF;
            ack = _padSelected;
            _step = _padSelected ? 0 : -1;
        }
        else if (_padSelected)
        {
            ushort buttons = Controller.State; // active low already
            switch (_step)
            {
                case 0: reply = IdOnAddress ? (byte)0x5A : (byte)0x41; ack = true; break; // pad id / ready
                case 1: reply = IdOnAddress ? (byte)(Controller.State & 0xFF) : (byte)0x5A; ack = true; break;
                case 2: reply = IdOnAddress ? (byte)(buttons >> 8) : (byte)(buttons & 0xFF);
                        ack = !IdOnAddress; if (IdOnAddress) PadReads++; break;
                case 3: reply = (byte)(buttons >> 8); ack = false; PadReads++; break; // L2..Square, last byte
                default: reply = 0xFF; ack = false; break;
            }
            _step++;
            if (!ack) { _step = -1; _padSelected = false; }
        }

        if (LogOps > 0) { LogOps--; Console.WriteLine($"[sio] TX 0x{tx:X2} -> RX 0x{reply:X2} (step={_step} ack={ack})"); }
        _rx = reply;
        _rxFull = true;
        if (ack)
        {
            _ack = true;
            if ((_ctrl & CtrlAckIrqEn) != 0)
            {
                _irq = true;
                _raiseIrq(7); // IRQ7 = controller/memory-card acknowledge
            }
        }
    }
}
