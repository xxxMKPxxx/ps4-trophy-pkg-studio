#!/usr/bin/env python3
"""
把 OpenOrbis 编出来的普通 ET_DYN ELF 补成 PS4 认的 SCE ELF。

为什么要这一步：
  - create-fself 产出的是 SELF（magic 4f153d1d），而 orbis-pub-cmd 的 img_create
    只接受 ELF，给 SELF 会报 "already converted from elf file to self file"；
  - 但普通的 ET_DYN ELF 装进 pkg 后，PS4 启动时报 CE-30045-2（模板自带的 eboot 是
    e_type=0xFE10 的 SCE ELF，带 /libexec/ld-elf.so.1 解释器）。

所以这里做两件事：
  1. e_type 改成 0xFE10（ET_SCE_EXEC）
  2. 补一个 PT_INTERP 段指向 /libexec/ld-elf.so.1（借用空闲的 GNU_STACK 段位）
"""
import struct
import sys

ET_SCE_EXEC = 0xFE10
PT_INTERP = 3
INTERP = b"/libexec/ld-elf.so.1\x00"


def patch(src, dst):
    d = bytearray(open(src, "rb").read())

    if d[:4] != b"\x7fELF":
        raise SystemExit("不是 ELF: " + src)
    if d[4] != 2:
        raise SystemExit("只处理 64 位 ELF")

    e_type = struct.unpack_from("<H", d, 16)[0]
    e_phoff = struct.unpack_from("<Q", d, 32)[0]
    e_phentsize = struct.unpack_from("<H", d, 54)[0]
    e_phnum = struct.unpack_from("<H", d, 56)[0]

    # 1) e_type -> 0xFE10
    struct.pack_into("<H", d, 16, ET_SCE_EXEC)

    # 2) 找一个可牺牲的段位（PT_GNU_STACK=0x6474e551 或 PT_NULL）
    slot = -1
    for i in range(e_phnum):
        o = e_phoff + i * e_phentsize
        p_type = struct.unpack_from("<I", d, o)[0]
        if p_type in (0x6474e551, 0):
            slot = i
            break

    if slot < 0:
        raise SystemExit("没有空闲的 program header 可用（phnum=%d）" % e_phnum)

    # 3) 把解释器字符串追加到文件末尾（4 字节对齐）
    while len(d) % 4:
        d.append(0)
    interp_off = len(d)
    d.extend(INTERP)

    o = e_phoff + slot * e_phentsize
    struct.pack_into("<IIQQQQQQ", d, o,
                     PT_INTERP, 0x4,            # p_type, p_flags = R
                     interp_off, 0, 0,          # offset, vaddr, paddr
                     len(INTERP), len(INTERP),  # filesz, memsz
                     0x1)                       # align

    open(dst, "wb").write(bytes(d))

    print("patched: %s -> %s" % (src, dst))
    print("  e_type 0x%04X -> 0x%04X, PT_INTERP at file offset %d (phdr #%d)"
          % (e_type, ET_SCE_EXEC, interp_off, slot))


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("用法: fix_elf.py <in.elf> <out.elf>")
    patch(sys.argv[1], sys.argv[2])
