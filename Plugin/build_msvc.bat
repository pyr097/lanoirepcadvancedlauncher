@echo off
rem Builds the 32-bit HD texture plugin (the game is 32-bit) and puts it in ..\Assets so the next launcher build embeds it.
rem Run from the "x86 Native Tools Command Prompt for VS".
cl /nologo /std:c++17 /O2 /EHsc /MT /LD LanTexReplace.cpp /Fe:..\Assets\LanTexReplace.asi /Fo:%TEMP%\ /link kernel32.lib
if errorlevel 1 (echo BUILD FAILED) else (echo built ..\Assets\LanTexReplace.asi)
