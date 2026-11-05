# 放到 GoldHEN_Plugins_Repository/plugin_src/trophy_unlocker/ 下，
# 或在独立目录用：需要 OO_PS4_TOOLCHAIN、GOLDHEN_SDK 两个环境变量。
#
# 产物：trophy_unlocker.prx
# 安装：拷到 PS4 的 /data/GoldHEN/plugins/，并在 plugins.ini 里加一行 trophy_unlocker

TOOLCHAIN ?= $(OO_PS4_TOOLCHAIN)
GH_SDK    ?= $(GOLDHEN_SDK)

OUTPUT_PRX := trophy_unlocker
INTDIR     := build
CDIR       := linux

# NpTrophy 不静态链接：插件里用 sceKernelDlsym 从游戏的 libSceNpTrophy 取地址，
# 这样不依赖 OpenOrbis 是否提供了对应的 stub。
LIBS := -lGoldHEN_Hook -lSceLibcInternal -lSceSysmodule -lScePad -lSceUserService -lkernel

CFLAGS := -D__FINAL__=1 -D__USE_PRINTF__ \
          --target=x86_64-pc-freebsd12-elf -fPIC -funwind-tables -c -Wall \
          -isysroot $(TOOLCHAIN) -isystem $(TOOLCHAIN)/include \
          -I$(GH_SDK)/include -Iinclude

LDFLAGS := -m elf_x86_64 -pie --script $(TOOLCHAIN)/link.x -e _init \
           --eh-frame-hdr -L$(TOOLCHAIN)/lib -L$(GH_SDK) -L$(GH_SDK)/build $(LIBS)

OBJS := $(INTDIR)/main.o $(INTDIR)/trophy_plugin.o $(INTDIR)/plugin_common.o $(INTDIR)/unlock_order.o

all: $(OUTPUT_PRX).prx

$(INTDIR):
	mkdir -p $(INTDIR)

$(INTDIR)/%.o: source/%.c | $(INTDIR)
	clang $(CFLAGS) -o $@ $<

$(OUTPUT_PRX).elf: $(OBJS)
	ld.lld $(GH_SDK)/build/crtprx.o $(OBJS) -o $@ $(LDFLAGS)

$(OUTPUT_PRX).prx: $(OUTPUT_PRX).elf
	$(TOOLCHAIN)/bin/$(CDIR)/create-fself -in=$(OUTPUT_PRX).elf \
	    -out=$(OUTPUT_PRX).oelf --lib=$(OUTPUT_PRX).prx --paid 0x3800000000000011
	@echo "==> $(OUTPUT_PRX).prx  拷到 /data/GoldHEN/plugins/"

clean:
	rm -rf $(INTDIR) $(OUTPUT_PRX).elf $(OUTPUT_PRX).oelf $(OUTPUT_PRX).prx

.PHONY: all clean
