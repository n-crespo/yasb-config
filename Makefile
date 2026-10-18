# Builds the C# helpers into build/ with the csc.exe that ships with Windows.
#   make         build everything
#   make clean   delete build/

SHELL := cmd.exe
CSC   := $(SYSTEMROOT)\Microsoft.NET\Framework64\v4.0.30319\csc.exe

all: build/ticktick-focus.exe build/ticktick-window.exe build/ticktick-upcoming.exe

# Console app: YASB reads its stdout.
build/ticktick-focus.exe: ticktick-focus.cs | build
	"$(CSC)" /nologo /optimize /target:exe /out:$@ $<

# winexe: no console window flashes when YASB launches it.
build/ticktick-window.exe: ticktick-window.cs | build
	"$(CSC)" /nologo /optimize /target:winexe /out:$@ $<

# Console app; needs System.Web.Extensions for JSON parsing.
build/ticktick-upcoming.exe: ticktick-upcoming.cs | build
	"$(CSC)" /nologo /optimize /target:exe /r:System.Web.Extensions.dll /out:$@ $<

build:
	mkdir build

clean:
	if exist build rmdir /s /q build

.PHONY: all clean
