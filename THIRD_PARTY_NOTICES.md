# Third-party notices

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
