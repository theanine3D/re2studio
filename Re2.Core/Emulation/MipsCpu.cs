using System;
using System.Buffers.Binary;

namespace Re2.Core.Emulation;

/// <summary>Thrown when the interpreter meets an instruction it does not implement.</summary>
public sealed class MipsUnsupportedException : Exception
{
    public MipsUnsupportedException(uint pc, uint instruction, string what)
        : base($"{what} at 0x{pc:X8}: 0x{instruction:X8}") { }
}

/// <summary>
/// A small MIPS III interpreter, enough to run routines lifted straight out of the cart.
/// </summary>
public sealed class MipsCpu
{
    /// <summary>RDRAM, addressed through the 0x80000000 / 0xA0000000 segments.</summary>
    public byte[] Ram { get; }

    public readonly ulong[] Reg = new ulong[32];
    public ulong Hi, Lo;
    public uint Pc;

    private readonly uint _mask;

    public MipsCpu(int ramSize = 8 << 20)
    {
        Ram = new byte[ramSize];
        _mask = (uint)(ramSize - 1);
    }

    /// <summary>Strips the segment bits; the cart's addresses are all KSEG0/KSEG1 views of RDRAM.</summary>
    private int Physical(ulong address) => (int)((uint)address & _mask);

    public byte Read8(ulong at) => Ram[Physical(at)];
    public ushort Read16(ulong at) => BinaryPrimitives.ReadUInt16BigEndian(Ram.AsSpan(Physical(at)));
    public uint Read32(ulong at) => BinaryPrimitives.ReadUInt32BigEndian(Ram.AsSpan(Physical(at)));

    public void Write8(ulong at, byte value) => Ram[Physical(at)] = value;
    public void Write16(ulong at, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Ram.AsSpan(Physical(at)), value);
    public void Write32(ulong at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Ram.AsSpan(Physical(at)), value);

    public void Load(uint address, ReadOnlySpan<byte> data) => data.CopyTo(Ram.AsSpan(Physical(address)));

    /// <summary>Sign-extends a 32-bit result the way a 64-bit MIPS register holds it.</summary>
    private static ulong S32(uint value) => (ulong)(long)(int)value;

    /// <summary>
    /// Runs from <paramref name="entry"/> until it returns to <paramref name="sentinel"/>.
    /// </summary>
    public void Call(uint entry, uint sentinel = 0x0BADF00D, int maxSteps = 20_000_000)
    {
        Reg[31] = sentinel;
        Pc = entry;

        for (int step = 0; step < maxSteps; step++)
        {
            if (Pc == sentinel) return;
            Step();
        }

        throw new InvalidOperationException($"ran {maxSteps:N0} instructions without returning (pc 0x{Pc:X8})");
    }

    /// <summary>Executes one instruction, including its delay slot when it branches.</summary>
    private void Step()
    {
        uint pc = Pc;
        uint op = Read32(pc);

        uint code = op >> 26;
        int rs = (int)((op >> 21) & 31), rt = (int)((op >> 16) & 31), rd = (int)((op >> 11) & 31);
        int sa = (int)((op >> 6) & 31);
        ushort imm = (ushort)op;
        short simm = (short)imm;

        ulong target;
        bool take;

        switch (code)
        {
            case 0x00: Special(pc, op, rs, rt, rd, sa); return;
            case 0x01: RegImm(pc, op, rs, rt, simm); return;

            case 0x02:                                            // j
                Branch(pc, (pc + 4 & 0xF0000000) | ((op & 0x03FFFFFF) << 2));
                return;

            case 0x03:                                            // jal
                Reg[31] = S32(pc + 8);
                Branch(pc, (pc + 4 & 0xF0000000) | ((op & 0x03FFFFFF) << 2));
                return;

            case 0x04: take = Reg[rs] == Reg[rt]; goto branch;    // beq
            case 0x05: take = Reg[rs] != Reg[rt]; goto branch;    // bne
            case 0x06: take = (long)Reg[rs] <= 0; goto branch;    // blez
            case 0x07: take = (long)Reg[rs] > 0; goto branch;     // bgtz

            case 0x14: take = Reg[rs] == Reg[rt]; goto likely;    // beql
            case 0x15: take = Reg[rs] != Reg[rt]; goto likely;    // bnel
            case 0x16: take = (long)Reg[rs] <= 0; goto likely;    // blezl
            case 0x17: take = (long)Reg[rs] > 0; goto likely;     // bgtzl

            case 0x08:                                            // addi (no trap: the cart's code
            case 0x09: Set(rt, S32((uint)((int)Reg[rs] + simm))); break;   // addiu
            case 0x0A: Set(rt, (long)Reg[rs] < simm ? 1u : 0u); break;     // slti
            case 0x0B: Set(rt, Reg[rs] < (ulong)(long)simm ? 1u : 0u); break; // sltiu
            case 0x0C: Set(rt, Reg[rs] & imm); break;             // andi
            case 0x0D: Set(rt, Reg[rs] | imm); break;             // ori
            case 0x0E: Set(rt, Reg[rs] ^ imm); break;             // xori
            case 0x0F: Set(rt, S32((uint)imm << 16)); break;      // lui

            case 0x18: Set(rt, (ulong)((long)Reg[rs] + simm)); break;      // daddi
            case 0x19: Set(rt, (ulong)((long)Reg[rs] + simm)); break;      // daddiu

            case 0x20: Set(rt, (ulong)(long)(sbyte)Read8(Reg[rs] + (ulong)(long)simm)); break;   // lb
            case 0x21: Set(rt, (ulong)(long)(short)Read16(Reg[rs] + (ulong)(long)simm)); break;  // lh
            case 0x23: Set(rt, S32(Read32(Reg[rs] + (ulong)(long)simm))); break;                 // lw
            case 0x24: Set(rt, Read8(Reg[rs] + (ulong)(long)simm)); break;                       // lbu
            case 0x25: Set(rt, Read16(Reg[rs] + (ulong)(long)simm)); break;                      // lhu
            case 0x27: Set(rt, Read32(Reg[rs] + (ulong)(long)simm)); break;                      // lwu

            case 0x28: Write8(Reg[rs] + (ulong)(long)simm, (byte)Reg[rt]); break;                // sb
            case 0x29: Write16(Reg[rs] + (ulong)(long)simm, (ushort)Reg[rt]); break;             // sh
            case 0x2B: Write32(Reg[rs] + (ulong)(long)simm, (uint)Reg[rt]); break;               // sw

            case 0x37:                                            // ld
            {
                ulong at = Reg[rs] + (ulong)(long)simm;
                Set(rt, ((ulong)Read32(at) << 32) | Read32(at + 4));
                break;
            }

            case 0x3F:                                            // sd
            {
                ulong at = Reg[rs] + (ulong)(long)simm;
                Write32(at, (uint)(Reg[rt] >> 32));
                Write32(at + 4, (uint)Reg[rt]);
                break;
            }

            default:
                throw new MipsUnsupportedException(pc, op, $"opcode 0x{code:X2}");
        }

        Pc = pc + 4;
        return;

    branch:
        // An ordinary branch runs its delay slot either way; only where it lands differs.
        target = pc + 4 + (ulong)(simm * 4);
        Branch(pc, take ? (uint)target : pc + 8);
        return;

    likely:
        // A "likely" branch nullifies its delay slot when not taken.
        target = pc + 4 + (ulong)(simm * 4);
        if (take) Branch(pc, (uint)target);
        else Pc = pc + 8;
        return;
    }

    /// <summary>Runs the delay slot, then lands on the target.</summary>
    private void Branch(uint pc, uint target)
    {
        Pc = pc + 4;
        Step();                       // the delay slot, which may not itself branch
        Pc = target;
    }

    private void Set(int register, ulong value)
    {
        if (register != 0) Reg[register] = value;
    }

    private void Special(uint pc, uint op, int rs, int rt, int rd, int sa)
    {
        switch (op & 0x3F)
        {
            case 0x00: Set(rd, S32((uint)Reg[rt] << sa)); break;                  // sll
            case 0x02: Set(rd, S32((uint)Reg[rt] >> sa)); break;                  // srl
            case 0x03: Set(rd, S32((uint)((int)Reg[rt] >> sa))); break;           // sra
            case 0x04: Set(rd, S32((uint)Reg[rt] << (int)(Reg[rs] & 31))); break; // sllv
            case 0x06: Set(rd, S32((uint)Reg[rt] >> (int)(Reg[rs] & 31))); break; // srlv
            case 0x07: Set(rd, S32((uint)((int)Reg[rt] >> (int)(Reg[rs] & 31)))); break; // srav

            case 0x08: JumpRegister(pc, (uint)Reg[rs]); return;                          // jr
            case 0x09: Set(rd, S32(pc + 8)); JumpRegister(pc, (uint)Reg[rs]); return;     // jalr

            case 0x0C: throw new MipsUnsupportedException(pc, op, "syscall");
            case 0x0D: throw new MipsUnsupportedException(pc, op, "break");

            case 0x10: Set(rd, Hi); break;                        // mfhi
            case 0x11: Hi = Reg[rs]; break;                       // mthi
            case 0x12: Set(rd, Lo); break;                        // mflo
            case 0x13: Lo = Reg[rs]; break;                       // mtlo

            case 0x18:                                            // mult
            {
                long product = (long)(int)Reg[rs] * (int)Reg[rt];
                Lo = S32((uint)product);
                Hi = S32((uint)(product >> 32));
                break;
            }

            case 0x19:                                            // multu
            {
                ulong product = (ulong)(uint)Reg[rs] * (uint)Reg[rt];
                Lo = S32((uint)product);
                Hi = S32((uint)(product >> 32));
                break;
            }

            case 0x1A:                                            // div
            {
                int a = (int)Reg[rs], b = (int)Reg[rt];
                if (b != 0 && !(a == int.MinValue && b == -1))
                {
                    Lo = S32((uint)(a / b));
                    Hi = S32((uint)(a % b));
                }
                break;
            }

            case 0x1B:                                            // divu
            {
                uint a = (uint)Reg[rs], b = (uint)Reg[rt];
                if (b != 0) { Lo = S32(a / b); Hi = S32(a % b); }
                break;
            }

            case 0x20:                                            // add
            case 0x21: Set(rd, S32((uint)((int)Reg[rs] + (int)Reg[rt]))); break;  // addu
            case 0x22:                                            // sub
            case 0x23: Set(rd, S32((uint)((int)Reg[rs] - (int)Reg[rt]))); break;  // subu
            case 0x24: Set(rd, Reg[rs] & Reg[rt]); break;         // and
            case 0x25: Set(rd, Reg[rs] | Reg[rt]); break;         // or
            case 0x26: Set(rd, Reg[rs] ^ Reg[rt]); break;         // xor
            case 0x27: Set(rd, ~(Reg[rs] | Reg[rt])); break;      // nor
            case 0x2A: Set(rd, (long)Reg[rs] < (long)Reg[rt] ? 1u : 0u); break;   // slt
            case 0x2B: Set(rd, Reg[rs] < Reg[rt] ? 1u : 0u); break;               // sltu

            case 0x2D: Set(rd, Reg[rs] + Reg[rt]); break;         // daddu
            case 0x2F: Set(rd, Reg[rs] - Reg[rt]); break;         // dsubu

            case 0x38: Set(rd, Reg[rt] << sa); break;             // dsll
            case 0x3A: Set(rd, Reg[rt] >> sa); break;             // dsrl
            case 0x3B: Set(rd, (ulong)((long)Reg[rt] >> sa)); break;              // dsra
            case 0x3C: Set(rd, Reg[rt] << (sa + 32)); break;      // dsll32
            case 0x3E: Set(rd, Reg[rt] >> (sa + 32)); break;      // dsrl32
            case 0x3F: Set(rd, (ulong)((long)Reg[rt] >> (sa + 32))); break;       // dsra32

            default:
                throw new MipsUnsupportedException(pc, op, $"special 0x{op & 0x3F:X2}");
        }

        Pc = pc + 4;
    }

    private void JumpRegister(uint pc, uint target)
    {
        Pc = pc + 4;
        Step();
        Pc = target;
    }

    private void RegImm(uint pc, uint op, int rs, int rt, short simm)
    {
        bool take = rt switch
        {
            0x00 => (long)Reg[rs] < 0,          // bltz
            0x01 => (long)Reg[rs] >= 0,         // bgez
            0x02 => (long)Reg[rs] < 0,          // bltzl
            0x03 => (long)Reg[rs] >= 0,         // bgezl
            0x10 => (long)Reg[rs] < 0,          // bltzal
            0x11 => (long)Reg[rs] >= 0,         // bgezal
            _ => throw new MipsUnsupportedException(pc, op, $"regimm 0x{rt:X2}")
        };

        if (rt is 0x10 or 0x11) Reg[31] = S32(pc + 8);

        bool nullifies = rt is 0x02 or 0x03;          // bltzl / bgezl
        uint destination = (uint)(pc + 4 + simm * 4);

        if (take) Branch(pc, destination);
        else if (nullifies) Pc = pc + 8;
        else Branch(pc, pc + 8);
    }
}
