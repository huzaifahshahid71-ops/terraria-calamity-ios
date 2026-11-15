# Troubleshooting and known limits

These observations come from the development phone, not from a clean-checkout certification.

- Calamity startup can take approximately three minutes; much of that is Finalizing Content. Profiling and further AOT coverage are ongoing.
- In-world memory has been around 4.5 GB. The tested host has increased-memory-limit support. A normal process limit may terminate the game; the guest IPA cannot grant the host an entitlement.
- Heat/FPS can worsen during play. Start with the 60 FPS cap. GPU-compressed textures, texture loading, and remaining interpreted hot paths need more work.
- The Metal/MoltenVK main-menu background can appear pink; OpenGL ES renders that background correctly.
- Touch aim range and control placement need tuning. Physical controller mapping has not been confirmed on a paired controller.
- `Item.Prefix` and `WallDrawing.DrawWalls` edits cannot currently be baked in the tested pipeline. Interpreter fallbacks remain necessary.
- Initial title/intro dimensions have needed additional adjustments; check a fresh launch rather than only an already-resized menu.

## AOT and rebuilds

A native abort in `load_aot_module` can indicate stale managed/AOT images. Rebuild with a fresh intermediate/output directory after changing woven assemblies or mods. Never copy a loose managed DLL into an app whose native AOT image was compiled against a different version.

LLVM reduces the enormous frames seen in non-LLVM tML AOT. A measured `NPC.SetDefaults` frame fell from roughly 440 KB to 300 KB. Avoid reverting to non-LLVM builds without measuring startup stack use.

## Assets and audio

A missing `Images/ChestStack_0` indicates missing matching tML content overrides. A missing `TerrariaMusic.xgs` under the app bundle can indicate relative-path resolution against the wrong base. Keep the content-root iOS patch and stage the complete override set.

An SDL `Audio device already open` error indicates the shared-output patch is absent or stale. Silent audio can also mean the default-device/zero-channel corrections are missing. Both are included in the current FAudio patch and helper header.

## Device tooling

If `devicectl` times out initializing CoreDeviceService only inside an assistant's restricted execution environment but works in Mac Terminal, investigate host execution permissions rather than repeatedly changing the phone cable. Run the generic log collector from a working terminal, then inspect the resulting `metadata/<version>` folder locally. Crash reports may be old; match their timestamps and native binary to the launch being investigated.

## Build prerequisites

The kit deliberately excludes the locally installed SDK, NuGet cache, managed game references, content, and native frameworks. Missing references on a fresh checkout usually mean input staging is incomplete. The optional in-process ILLink targets file uses versioned local paths; update those paths when using another toolchain.
