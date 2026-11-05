# Trophy Unlocker（独立 pkg 版）
#
# 产物：eboot.bin —— 替换掉模板包 CONTENTS 目录里的 eboot.bin
# 打包：PkgStudio 打 pkg 时会自动用它（见 template\eboot.bin 的说明）
#
# 需要 OpenOrbis 工具链：export OO_PS4_TOOLCHAIN=/path/to/PS4Toolchain

TOOLCHAIN ?= $(OO_PS4_TOOLCHAIN)
INTDIR    := build

# NpTrophy 用 dlsym 取地址，所以不用 -lSceNpTrophy；UserService 直接链接
LIBS := -lc -lkernel -lSceUserService

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

all: eboot.bin

$(INTDIR):
	mkdir -p $(INTDIR)

# img_create 只收 ELF；而普通 ET_DYN 装进 pkg 会 CE-30045-2，
# 所以用 tools/fix_elf.py 补成 SCE ELF（e_type=0xFE10 + PT_INTERP）
eboot.bin: $(INTDIR) $(OBJS)
	$(LD) $(OBJS) -o $(INTDIR)/unlocker.elf $(LDFLAGS)
	python3 tools/fix_elf.py $(INTDIR)/unlocker.elf eboot.bin

# 需要 SELF（给 PkgTool.Core 那条流水线）时用：make self
self: $(INTDIR) $(OBJS)
	$(LD) $(OBJS) -o $(INTDIR)/unlocker.elf $(LDFLAGS)
	$(TOOLCHAIN)/bin/$(CDIR)/create-fself -in=$(INTDIR)/unlocker.elf \
		-out=$(INTDIR)/unlocker.oelf --eboot "eboot.self" --paid 0x3800000000000011

$(INTDIR)/%.o: source/%.c
	$(CC) $(CFLAGS) -o $@ $<

clean:
	rm -rf $(INTDIR) eboot.bin

.PHONY: all clean self
