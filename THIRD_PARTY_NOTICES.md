# Third-party notices

## Nuclear Option Graphics AMD local rework 0.5.3

`src/NuclearOptionGraphicsAmd/` directly derives from Talon-One Fighter
Squadron Nuclear Option Frame Generation 0.5.2, commit
3314665a3372ddac95f0a02ab0d293e70e69bea0, under MIT. Its LICENSE and original
notices accompany the package. Local changes add FSR 4.1.2 selection/fallback,
RCAS controls, and AMD Anti-Lag 2. This is not an official upstream release.

AMD Anti-Lag 2 uses the official MIT header, commit
390aa4a8c8655d0ae6e90079db2c85e103a96da3, with its notice in
`src/NuclearOptionGraphicsAmd/src/ThirdParty/AntiLag2-license.txt` and the
installed Runtime folder. https://github.com/GPUOpen-LibrariesAndSDKs/AntiLag2-SDK

AMD-signed FidelityFX SDK 2.4.0 loaders/upscalers/frame-generation components,
MinHook 1.3.4, Streamline 2.14.16 headers/import declarations, Unity native
plugin headers and Vulkan headers retain the upstream distribution notices.
No NVIDIA runtime DLLs or game assets are included. Historical notices for
the deactivated custom renderer follow.
## AMD FSR renderer integration (local experimental mod)

The native bridge and managed native-call definitions under `src/AmdRendering/`
derive from AchiraStudio's MIT-licensed Unity FSR 4 Wrapper:
https://github.com/AchiraStudio/unity-fsr4-wrapper . The Unity 6 renderer wrapper
is not used; this project supplies a separate Unity 2022 / URP 14 integration.
Local modifications include render-pass wiring, camera state, fallback and
lifecycle handling, dispatch-error reporting and GPU-fence timeout handling.
The full MIT notice is in `src/AmdRendering/ThirdParty/LICENSE-AchiraStudio.txt`
and is included with the installed mod.

Official AMD FidelityFX loader/upscaler DLLs come from the v2.4.0 release of
https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK . They are used without
binary modification. The AMD redistribution notice is included in the package
as `LICENSE-AMD-FidelityFX.txt`. The sources and binary hashes are recorded in
the local package manifest. No game assemblies or game art are redistributed.

The ray-regeneration probe and optional 0.2.0/0.3.0 DXR runtime use the unmodified, AMD-signed
`amd_fidelityfx_denoiser_dx12.dll` from the same SDK under its AMD license.
The DLL is included when building with `-WithRayTracing`. The local ray-generation/hit shaders and test
receiver geometry are authored for this probe.

The 0.3.0 native-shadow fallback shader compiles against Unity URP/Core 14.0.12
shader includes from the licensed Unity 2022.3 editor installation. The Unity
Companion License notice from the URP package is included as
`LICENSE-Unity-URP.txt`. No Unity editor binaries or game art are redistributed.

The cockpit depth/motion treatment was informed by the MIT-licensed
Talon-One-Fighter-Squadron/nuclear-option-frame-generation project, inspected
at commit `3314665a3372ddac95f0a02ab0d293e70e69bea0`:
https://github.com/Talon-One-Fighter-Squadron/nuclear-option-frame-generation .
Its MIT notice is included as `LICENSE-Talon-One.txt`. This package does not
redistribute that project's native frame-generation bridge or NVIDIA runtime.

## GE-87-3-A engine model (CC BY 4.0) - JFD-28 Courier

The eight engines of the JFD-28 Courier GE-87 configuration are based on
"GE-87-3-A" by MoistSquid, licensed under Creative Commons Attribution 4.0
International (https://creativecommons.org/licenses/by/4.0/):
https://sketchfab.com/3d-models/ge-87-3-a-84ebcc9e701249cb9a229a794a962a2c

Changes: repainted materials, mounted in paired B-52-style pods, reduced level
of detail meshes (LOD0/LOD1/LOD2), inlet liner and far-range silhouettes
rebuilt as revolved surfaces. MoistSquid does not endorse this mod.

## KAR runtime LOSAT integration

The optional Type-12 LOSAT ground variant references the installed KAR
`kar_mbt_losat` mount and `kar_missile_at56` projectile. KAR and Blueprinter
must be installed to register this variant. The restoration package contains
the integration code and does not embed KAR's models, textures or projectiles.

## NeutralQoL cargo physics (MPL-2.0)

CargoDropModel.cs and CargoDropSimulator.cs are adapted from DevHrytsan/NeutralQoL,
commit 5da0322b330374519c96ce0cd336524c646a6295, obtained 2026-11-02:
https://github.com/DevHrytsan/NeutralQoL/tree/5da0322b330374519c96ce0cd336524c646a6295

These files remain under Mozilla Public License 2.0. Changes: host namespace,
field-access helper and logger integration. The executable package includes
the corresponding modified source in Sources/CargoPhysics and the full licence
in LICENSES/MPL-2.0.txt. Recipients may use and modify that source under MPL-2.0.

## Commander and WingCommand study

NOCommander f238bba816be377c40fce675635045dab555a4a1 (Unlicense): route progression,
turn anticipation and passed-waypoint handling informed the new controller.
https://github.com/DontKnowWhatImDoingHere/NOCommander

WingCommand 425b36bfd9f744544e080435614c335b8abcb480 (MIT): terrain look-ahead,
continuous energy management and progress supervision informed the controller.
https://github.com/GrabowMar/NuclearOption-WingCommand

The new flight policy/controller is independently implemented; these projects
do not certify this plugin or its Type-12 flight behaviour.

## WingCommand

The tactical-map MFD integration in NO Native Logistics AI was independently
adapted from the public VirtualMFD/MFDScreen integration pattern used by
WingCommand v0.9.2.2:

https://github.com/GrabowMar/NuclearOption-WingCommand

MIT License

Copyright (c) 2026 marci

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
