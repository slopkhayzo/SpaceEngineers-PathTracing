# Vendored upstream

- Project: MinHook
- Repository: https://github.com/TsudaKageyu/minhook
- Tag: `v1.3.4`
- Commit: `c3fcafdc10146beb5919319d0683e44e3c30d537`
- Purpose: statically linked x64 detours. The plugin hooks the native code the
  .NET JIT produced for a handful of game methods, and calls the originals
  through MinHook's trampolines.

The upstream license is preserved in `LICENSE.txt` and reproduced with binary
packages as `MINHOOK-LICENSE.txt`.

## Local patches

Without valid NGen images (after a game update, until Windows rebuilds them) the
.NET JIT compiles the game's methods itself and uses VEX-encoded AVX
instructions, starting with `vzeroupper` (`C5 F8 77`) in many prologues. HDE64
decodes no VEX instruction, so such a method could not be hooked (MinHook status
8, `MH_ERROR_UNSUPPORTED_FUNCTION`; Space Engineers 1.211.017:
`MyPostponedUpdateManager.Apply`, prologue `57 56 48 83 EC 78 C5 F8 77`).

- `src/trampoline.c`, `CreateTrampolineFunction`: the instruction after the
  first five copied bytes is no longer decoded (it is only jumped to).
- `src/hde/hde64.c`, `hde64_disasm`: `vzeroupper` and `vzeroall` decode as
  three-byte instructions without operands (reported as opcode `0F 77`).
