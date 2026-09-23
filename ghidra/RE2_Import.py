# Imports Resident Evil 2 (N64) into Ghidra with correct memory layout.
#
# Why this exists instead of an N64 loader plugin:
#   * N64LoaderWV crashes on Ghidra 11 (it casts every import option to String, and Ghidra 11 adds
#     two Boolean options to every loader).
#   * More importantly, a cart loader maps the *raw* ROM, and roughly 97% of this ROM is compressed.
#     Only 0x1000..0x14422 is directly executable code. The rest of the program lives in 36 chained
#     raw-deflate streams that have to be inflated first.
#
# Usage:
#   1. File > New Project (or open the existing RE2 project)
#   2. File > Import File > the .z64, Format "Raw Binary", Language "MIPS:BE:64:default".
#      Leave the base address at 0 -- this script rebuilds the memory map itself.
#      (An empty program created with New > Program also works.)
#   3. Window > Script Manager > run this script.
#   4. It asks for the .z64, then optionally for the directory of decompressed segments produced by
#         re2 dump-code <rom> --out <dir>
#
# @category  RE2
# @author    RE2Suite

import json
import os

from java.io import FileInputStream

# ROM 0x1000 is copied to RAM by IPL3 and jumped to at the header's entry point. Confirmed two ways:
# the header says 0x80000480, and voting jal targets against "jr ra; nop" epilogues independently
# recovers 0x80000480 from the code itself.
BOOT_ROM_OFFSET = 0x1000
BOOT_LOAD_ADDRESS = 0x80000480
BOOT_END_ROM_OFFSET = 0x14422            # where the deflate chain begins
BOOT_LENGTH = BOOT_END_ROM_OFFSET - BOOT_ROM_OFFSET

# The whole cart, mapped where the PI bus exposes it, so pointer-chasing into ROM resolves.
CART_BUS_ADDRESS = 0xB0000000


EXPECTED_BLOCKS = ("boot", "cart")
NEWLINE = "\n"


def remove_stray_blocks(mem):
    """
    Offer to drop blocks this script did not create.

    The usual case is a leftover "ram" block from Raw-Binary-importing the cart at base 0. That is
    worse than useless here: jal resolves its target against (PC & 0xF0000000), so with the code
    sitting at 0x0 instead of 0x80000480 every call lands 0xB80 off, and auto-analysis manufactures
    a whole parallel set of functions at wrong addresses.

    Blocks cannot be deleted from the Listing window -- only via Window > Memory Map, or from a
    script like this one.
    """
    strays = [b for b in mem.getBlocks()
              if b.getName() not in EXPECTED_BLOCKS and not b.getName().startswith("ovl_")]
    if not strays:
        return

    print("")
    print("Found %d block(s) this script did not create:" % len(strays))
    for b in strays:
        print("  %s  %s - %s" % (b.getName(), b.getStart(), b.getEnd()))

    summary = NEWLINE.join("%s (%s - %s)" % (b.getName(), b.getStart(), b.getEnd()) for b in strays)
    question = "Delete these blocks? They shift jal targets and produce bogus functions."

    if not askYesNo("Remove stray blocks?", question + NEWLINE + NEWLINE + summary):
        print("left in place - expect duplicate, wrongly-based functions.")
        return

    for b in strays:
        print("  removing %s ..." % b.getName())
        mem.removeBlock(b, monitor)
    print("removed. Re-run Analysis > Auto Analyze for a clean result.")


def _block(mem, name, address, stream, length, comment, overlay=False):
    """Create an initialized block, replacing any block already occupying the name."""
    existing = mem.getBlock(name)
    if existing is not None:
        mem.removeBlock(existing, monitor)

    block = mem.createInitializedBlock(name, toAddr(address), stream, length, monitor, overlay)
    block.setRead(True)
    block.setWrite(True)
    block.setExecute(True)
    block.setComment(comment)
    return block


def load_boot(mem, rom_path):
    stream = FileInputStream(rom_path)
    try:
        stream.skip(BOOT_ROM_OFFSET)
        _block(mem, "boot", BOOT_LOAD_ADDRESS, stream, BOOT_LENGTH,
               "ROM 0x%X..0x%X, loaded by IPL3. Contains the zlib inflate used to unpack the "
               "deflate chain, plus the loader we care about." % (
                   BOOT_ROM_OFFSET, BOOT_ROM_OFFSET + BOOT_LENGTH))
    finally:
        stream.close()

    print("boot: ROM 0x%X -> 0x%08X (0x%X bytes)" % (BOOT_ROM_OFFSET, BOOT_LOAD_ADDRESS, BOOT_LENGTH))


def load_cart(mem, rom_path):
    """Map the raw cart read-only so ROM offsets referenced by the loader resolve to something."""
    size = os.path.getsize(rom_path)
    stream = FileInputStream(rom_path)
    try:
        block = _block(mem, "cart", CART_BUS_ADDRESS, stream, size,
                       "Raw cart image as seen on the PI bus. Mostly compressed data.")
        block.setWrite(False)
        block.setExecute(False)
    finally:
        stream.close()

    print("cart: 0x%08X (0x%X bytes, read-only)" % (CART_BUS_ADDRESS, size))


def load_segments(mem, code_dir):
    """
    Map decompressed overlays using the game's own overlay table.

    bases.json is produced by:  re2 overlays <rom> --dump --out <dir>
    Its addresses come from the table at RAM 0x80012150, so they are authoritative rather than
    inferred.

    Most overlays genuinely share a load slot -- 28 of them target 0x80161CF0, because the game
    swaps them in and out at runtime. Only the first claimant of an address can be a normal block;
    the rest are created as Ghidra overlay blocks so they coexist without clobbering each other.
    """
    bases_path = os.path.join(code_dir, "bases.json")
    if not os.path.exists(bases_path):
        print("no bases.json in %s - skipping overlays." % code_dir)
        print("Generate one with: re2 overlays <rom> --dump --out %s" % code_dir)
        return

    with open(bases_path) as handle:
        bases = json.load(handle)

    claimed = {}
    for name in sorted(bases.keys()):
        if name.startswith("_"):
            continue

        path = os.path.join(code_dir, name + ".bin")
        if not os.path.exists(path):
            print("  %s: no such file, skipped" % name)
            continue

        text = str(bases[name])
        base = int(text, 16) if text.lower().startswith("0x") else int(text)
        size = os.path.getsize(path)

        # First segment at an address gets the real block; later ones become overlays.
        as_overlay = base in claimed
        claimed.setdefault(base, name)

        stream = FileInputStream(path)
        try:
            _block(mem, name, base, stream, size,
                   "Overlay %s, load address from the game's overlay table" % name,
                   overlay=as_overlay)
        finally:
            stream.close()

        print("  %s -> 0x%08X (0x%X bytes)%s" % (
            name, base, size, "  [overlay, shares slot with %s]" % claimed[base] if as_overlay else ""))


def main():
    rom = askFile("Select the Resident Evil 2 .z64", "Use ROM")
    rom_path = rom.getAbsolutePath()

    mem = currentProgram.getMemory()

    remove_stray_blocks(mem)
    load_boot(mem, rom_path)
    load_cart(mem, rom_path)

    try:
        code_dir = askDirectory("Decompressed overlays (from 're2 overlays --dump')", "Use directory")
        load_segments(mem, code_dir.getAbsolutePath())
    except Exception:
        print("no overlay directory chosen - boot and cart only.")

    entry = toAddr(BOOT_LOAD_ADDRESS)
    createLabel(entry, "entry", True)
    addEntryPoint(entry)
    disassemble(entry)
    createFunction(entry, "entry")

    print("")
    print("Done. Run Analysis > Auto Analyze, then start from these landmarks:")
    print("  0x%08X  %s" % (0x800115D0, "RE.093099.1542 build stamp"))
    print("  0x%08X  %s" % (0x800115E0, "zlib inflate error strings"))
    print("  0x%08X  %s" % (0x80012150, "OVERLAYTABLENUMC"))
    print("  0x%08X  %s" % (0x80011B94, "libultra pi/si source names"))
    print("")
    print("Targets, in order of usefulness:")
    print("  1. xrefs to the zlib error strings -> the inflate driver -> the loop that walks the")
    print("     deflate chain. That loop carries each segment's load address, which is the one")
    print("     thing still missing before the rest of the code can be disassembled correctly.")
    print("  2. PI DMA writes (PI_DRAM_ADDR 0xA4600000, PI_CART_ADDR 0xA4600004) -> the asset")
    print("     reader -> the directory that maps asset ids to ROM offsets.")


main()
