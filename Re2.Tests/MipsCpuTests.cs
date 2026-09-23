using System;
using Re2.Core.Emulation;
using Xunit;

namespace Re2.Tests;

/// <summary>The interpreter, checked before anything is decoded with it.</summary>
public class MipsCpuTests
{
    private const uint Base = 0x80001000;

    private static MipsCpu Program(params uint[] instructions)
    {
        var cpu = new MipsCpu(1 << 20);
        for (int i = 0; i < instructions.Length; i++) cpu.Write32(Base + (uint)i * 4, instructions[i]);
        return cpu;
    }

    private static uint Addiu(int rt, int rs, short imm) => (9u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | (ushort)imm;
    private static uint Beq(int rs, int rt, short off) => (4u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | (ushort)off;
    private static uint Bne(int rs, int rt, short off) => (5u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | (ushort)off;
    private static uint Beql(int rs, int rt, short off) => (0x14u << 26) | ((uint)rs << 21) | ((uint)rt << 16) | (ushort)off;
    private static uint JrRa() => (31u << 21) | 8u;
    private static readonly uint Nop = 0;

    /// <summary>The delay slot after a branch runs whether or not the branch is taken.</summary>
    [Fact]
    public void ADelaySlotRunsEvenWhenTheBranchIsNotTaken()
    {
        var cpu = Program(
            Addiu(2, 0, 5),          // v0 = 5
            Beq(0, 2, 2),            // not taken (0 != 5)
            Addiu(3, 0, 7),          // delay slot: v1 = 7, must still run
            JrRa(),
            Nop);

        cpu.Call(Base);

        Assert.Equal(5u, (uint)cpu.Reg[2]);
        Assert.Equal(7u, (uint)cpu.Reg[3]);
    }

    [Fact]
    public void ADelaySlotRunsWhenTheBranchIsTaken()
    {
        var cpu = Program(
            Addiu(2, 0, 1),
            Bne(0, 2, 2),            // taken (0 != 1): skips the next-but-one instruction
            Addiu(3, 0, 7),          // delay slot, runs
            Addiu(4, 0, 9),          // jumped over
            JrRa(),
            Nop);

        cpu.Call(Base);

        Assert.Equal(7u, (uint)cpu.Reg[3]);
        Assert.Equal(0u, (uint)cpu.Reg[4]);
    }

    /// <summary>A "likely" branch is the one exception: not taken means the slot is nullified.</summary>
    [Fact]
    public void ALikelyBranchNullifiesItsDelaySlotWhenNotTaken()
    {
        var cpu = Program(
            Addiu(2, 0, 5),
            Beql(0, 2, 2),           // not taken
            Addiu(3, 0, 7),          // delay slot, must NOT run
            JrRa(),
            Nop);

        cpu.Call(Base);

        Assert.Equal(0u, (uint)cpu.Reg[3]);
    }

    [Fact]
    public void ArithmeticResultsAreSignExtendedIntoSixtyFourBits()
    {
        var cpu = Program(
            (0x0Fu << 26) | (2u << 16) | 0xFFFF,     // lui v0, 0xFFFF
            JrRa(),
            Nop);

        cpu.Call(Base);

        // 0xFFFF0000 held in a 64-bit register is sign-extended, which the cart's 64-bit compares rely on.
        Assert.Equal(0xFFFFFFFFFFFF0000UL, cpu.Reg[2]);
    }

    [Fact]
    public void MultiplyAndDivideUseHiAndLo()
    {
        var cpu = Program(
            Addiu(2, 0, 7),
            Addiu(3, 0, 3),
            (2u << 21) | (3u << 16) | 0x18,          // mult v0, v1
            (4u << 11) | 0x12,                       // mflo a0
            (5u << 11) | 0x10,                       // mfhi a1
            JrRa(),
            Nop);

        cpu.Call(Base);

        Assert.Equal(21u, (uint)cpu.Reg[4]);
        Assert.Equal(0u, (uint)cpu.Reg[5]);
    }

    /// <summary>The real check: run the cart's own memset.</summary>
    [RomFact]
    public void TheCartsOwnMemsetClearsExactlyWhatItIsAsked()
    {
        var cpu = GameCode.Load(TestRom.Rom);

        const uint buffer = 0x80200000;
        const int length = 0x140;

        for (int i = 0; i < length + 16; i++) cpu.Write8(buffer + (uint)i, 0xAB);

        cpu.Reg[4] = buffer;
        cpu.Reg[5] = length;
        cpu.Reg[29] = 0x80300000;                    // a stack to run on
        cpu.Call(GameCode.Memset);

        for (int i = 0; i < length; i++)
            Assert.Equal(0, cpu.Read8(buffer + (uint)i));

        // And nothing past the end, which a length or loop-bound error would trample.
        for (int i = length; i < length + 16; i++)
            Assert.Equal(0xAB, cpu.Read8(buffer + (uint)i));
    }
}
