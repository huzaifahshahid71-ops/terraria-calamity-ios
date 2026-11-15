# Architecture

The app is a .NET 10 iOS host using Mono with JIT disabled. LLVM AOT supplies native code for tModLoader, FNA, ReLogic, Calamity, Music, ManagedHooks, and selected dependencies. Remaining assemblies and unmatched dynamic edits use Mono's interpreter.

UIKit's scene delegate forwards scene attachment into patched SDL2. SDL owns the graphical game loop and the editable native touch overlay. FNA renders through FNA3D, using OpenGL ES or Vulkan translated to Metal by MoltenVK. The FAudio iOS backend mixes independent effect/music engines into one physical SDL output.

## Managed hooks

Native MonoMod detours are unavailable on the tested JIT-less iOS runtime. HookWeaver audits the supplied mod assemblies, moves original target bodies into generated methods, and inserts dispatch stubs. HookGen and tML hook operations are redirected into ManagedHooks. This preserves the managed hook-chain model without runtime patching of native code.

The iOS patches also adapt platform initialization, logging, writable paths, assembly load-context ownership, mod access, IME/display initialization and lazy textures. A mod assembly shipped with the app uses the byte-identical default-context copy so Mono can find its AOT image.

## IL baking

A headless Mac run captures Calamity IL edits into generated `__mh_baked_*` methods. Those methods become part of the assembly fed to iOS AOT. Runtime dispatch accepts a baked body only when the manipulator identities and IL hash match. Generated `__mh_fdi_*` helpers replace supported fast-delegate invoker patterns. A mismatch retains the dynamic/interpreter fallback instead of silently using a stale baked body.

## Native controls

The SDL UIKit overlay supplies movement, aiming, action buttons, configurable positions/sizes/opacity, frame caps, renderer selection and performance display. The source-only checkout uses generic symbols. A local generator can populate its item sprites from the builder's own game content. Physical controller input follows the existing SDL/FNA game-controller path and still needs device testing.

## Boundaries

Inputs and source assets stay separate from derived files under `build/`. Bundled game/mod content is generated locally. Neither the input assemblies nor the resulting personal IPA belong in this repository. Native-only packaging can replace frameworks without changing managed/AOT images; a managed change requires a matching full app build.
