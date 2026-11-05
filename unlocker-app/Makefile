# Trophy Unlocker（独立 pkg 版）
#
# 产物：
#   eboot.oelf —— **SCE ELF（Orbis ELF）**，这个才是塞进 pkg 当 eboot.bin 的
#   eboot.self —— 同名 SELF，只有 PkgTool.Core 那条流水线才用得到（这里不用）
#
# 为什么要 create-fself：
#   ld.lld 直接出来的只是普通 ET_DYN ELF，没有 PS4 需要的 SCE 段
#   （PT_SCE_RELA 0x61000010 / PT_SCE_PROCPARAM 0x61000001 / .sceversion …）。
#   自己改 e_type=0xFE10 是假 SCE ELF，装进 pkg 后启动直接报
#   CE-30045-2 / CE-41839-5（"Cannot start the application"）。
#   create-fself 会把 ELF 正确转成 SCE ELF（-out=）与 SELF（--eboot=）。
#
# 需要 OpenOrbis 工具链：export OO_PS4_TOOLCHAIN=/path/to/PS4Toolchain

TOOLCHAIN ?= $(OO_PS4_TOOLCHAIN)
INTDIR    := build

# 直接链接官方 stub（samples/trophies 就是这么链的），不再 dlsym 猜原型
# 界面：VideoOut 出画面，Pad 读手柄，FreeType 画中文字
LIBS := -lc -lkernel -lSceUserService -lSceSysmodule -lSceNpTrophy \
        -lSceVideoOut -lScePad -lSceFreeType -lSceSystemService

CFLAGS := --target=x86_64-pc-freebsd12-elf -fPIC -funwind-tables -c -Wall \
          -isysroot $(TOOLCHAIN) -isystem $(TOOLCHAIN)/include

LDFLAGS := -m elf_x86_64 -pie --script $(TOOLCHAIN)/link.x --eh-frame-hdr \
           -L$(TOOLCHAIN)/lib $(LIBS) $(TOOLCHAIN)/lib/crt1.o

OBJS := $(patsubst source/%.c,$(INTDIR)/%.o,$(wildcard source/*.c))

UNAME_S := $(shell uname -s)
ifeq ($(UNAME_S),Linux)
	CC := clang
	LD := ld.lld
	CDIR := linux
endif
ifeq ($(UNAME_S),Darwin)
	CC := /usr/local/opt/llvm/bin/clang
	LD := /usr/local/opt/llvm/bin/ld.lld
	CDIR := macos
endif

all: eboot.oelf

$(INTDIR):
	mkdir -p $(INTDIR)

# 一条命令同时产出 SCE ELF（-out）与 SELF（--eboot），两者内容必须同源
eboot.oelf: $(INTDIR) $(OBJS)
	$(LD) $(OBJS) -o $(INTDIR)/unlocker.elf $(LDFLAGS)
	$(TOOLCHAIN)/bin/$(CDIR)/create-fself -in=$(INTDIR)/unlocker.elf \
		-out=eboot.oelf --eboot "eboot.self" --paid 0x3800000000000011

$(INTDIR)/%.o: source/%.c
	$(CC) $(CFLAGS) -o $@ $<

clean:
	rm -rf $(INTDIR) eboot.oelf eboot.self

.PHONY: all clean
