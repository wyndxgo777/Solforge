# Third-party notices

Solforge is built on Sunrise Studio by stan (https://github.com/stanuwu/SunriseStudio) and its
contributors. The Sunrise client and its research are stan's work too
(https://github.com/stanuwu/Sunrise).

`Solforge.exe` contains the libraries below and the .NET runtime. Its audio decoder also
holds code ported from four projects. Studio shows this file under **Help > Third-party notices**.
Each license text is copied from the project's own license file.

## Libraries

| library | version | license | copyright |
|---|---|---|---|
| .NET runtime and Windows Forms | 9.0 | MIT | Copyright (c) .NET Foundation and Contributors |
| ImGui.NET | 1.91.6.1 | MIT | Copyright (c) 2017 Eric Mellino and ImGui.NET contributors |
| cimgui, inside ImGui.NET | 1.91.6 | MIT | Copyright (c) 2015 Stephan Dilly |
| Dear ImGui, inside cimgui | 1.91.6 | MIT | Copyright (c) 2014-2024 Omar Cornut |
| Vortice.D3DCompiler, Vortice.Direct3D11, Vortice.DirectX, Vortice.DXGI | 3.8.3 | MIT | Copyright (c) Amer Koleci and Contributors |
| Vortice.Mathematics | 2.1.0 | MIT | Copyright (c) Amer Koleci and contributors |
| SharpGen.Runtime, SharpGen.Runtime.COM | 2.4.2-beta | MIT | Copyright (c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci |
| Loretta.CodeAnalysis, Loretta.CodeAnalysis.Lua | 0.2.13 | MIT | Copyright (c) 2019 GGG KILLER |
| Tsu | 2.2.2 | MIT | Copyright (c) 2016 GGG KILLER |
| BCnEncoder.NET | 2.3.0 | MIT | Copyright 2020 Nomi Lakkala |
| CommunityToolkit.HighPerformance | 8.4.0 | MIT | Copyright (c) .NET Foundation and Contributors |
| QRCoder | 1.8.0 | MIT | Copyright (c) 2013-2025 Raffael Herrmann |

Each library above is licensed under these terms, with its own copyright line:

```text
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
```

The .NET runtime and the Community Toolkit include code from other projects. Their own notice files
are copied unchanged into `licenses/`. Studio shows each on its own tab.

## Fonts

Solforge's interface theme (SolTheme) embeds these faces in `SunriseStudio.Viewport.dll`. They come
unchanged from github.com/google/fonts (`ofl/chakrapetch`, `ofl/rajdhani`). Each is licensed under the
SIL Open Font License 1.1; the full license with each project's copyright line is copied unchanged into
`licenses/chakra-petch-OFL.txt` and `licenses/rajdhani-OFL.txt`, and Studio shows each on its own tab.

| font | files | license | copyright |
|---|---|---|---|
| Chakra Petch | ChakraPetch-SemiBold.ttf, ChakraPetch-Bold.ttf | OFL-1.1 | Copyright 2018 The Chakra Petch Project Authors (https://github.com/m4rc1e/Chakra-Petch.git) |
| Rajdhani | Rajdhani-Medium.ttf, Rajdhani-SemiBold.ttf | OFL-1.1 | Copyright (c) 2014, Indian Type Foundry (info@indiantypefoundry.com) |

## Ported audio code

Studio ships no third-party audio program, script or native library.

| project | license | what Studio uses | Studio files |
|---|---|---|---|
| libvorbis 1.3.7 | BSD 3-clause | Vorbis decoder, ported to C#; encoder codebooks | `src/SunriseStudio.Audio/VorbisDecoder.cs`, `src/SunriseStudio.Audio/Resources/wwise-vorbis-codebooks.bin` |
| aoTuV 6.03 | BSD 3-clause | encoder codebooks, the same books as libvorbis | `src/SunriseStudio.Audio/Resources/wwise-vorbis-codebooks.bin` |
| ww2ogg | BSD 3-clause | Wwise codebook layout and setup rebuild | `src/SunriseStudio.Audio/WwiseVorbisCodebooks.cs`, `src/SunriseStudio.Audio/WwiseVorbisSetup.cs` |
| vgmstream | ISC | Wwise Vorbis setup rebuild, channel downmix, float to PCM16 conversion | `src/SunriseStudio.Audio/WwiseVorbisSetup.cs`, `src/SunriseStudio.Audio/AudioDownmix.cs`, `src/SunriseStudio.Audio/WemDecoder.cs` |

The codebook file is packed by Studio's own generator from the static encoder books of libvorbis or
aoTuV. Both sources give the same file.

### libvorbis

Source: <https://downloads.xiph.org/releases/vorbis/libvorbis-1.3.7.tar.gz>, file `COPYING`.

```text
Copyright (c) 2002-2020 Xiph.org Foundation

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

- Neither the name of the Xiph.org Foundation nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### aoTuV

Source: <https://github.com/AO-Yumi/vorbis_aotuv>, tag `beta6.03-2018`, file `LICENCE`.

```text
aoTuV - Copyright (c) 2003-2018 Aoyumi
libvorbis - Copyright (c) 2002-2018 Xiph.org Foundation

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

- Neither the name of the Xiph.org Foundation nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### ww2ogg

Source: <https://github.com/hcs64/ww2ogg>, file `COPYING`. Only code was ported. ww2ogg's packed
codebook files are not part of Studio.

```text
Copyright (c) 2002, Xiph.org Foundation
Copyright (c) 2009-2016, Adam Gashlin

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

- Neither the name of the Xiph.org Foundation nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### vgmstream

Source: <https://github.com/vgmstream/vgmstream>, release r2117, file `COPYING`. Only vgmstream's
own code was ported. None of the libraries its release bundles is part of Studio.

```text
Copyright (c) 2008-2025 Adam Gashlin, Fastelbja, Ronny Elfert, bnnm,
                        Christopher Snowhill, NicknineTheEagle, bxaimc,
                        Thealexbarney, CyberBotX, EdnessP, et al

Portions Copyright (c) 2004-2008, Marko Kreen
Portions Copyright 2001-2007  jagarl / Kazunori Ueno <jagarl@creator.club.ne.jp>
Portions Copyright (c) 1998, Justin Frankel/Nullsoft Inc.
Portions Copyright (C) 2006 Nullsoft, Inc.
Portions Copyright (c) 2005-2007 Paul Hsieh
Portions Copyright (C) 2000-2004 Leshade Entis, Entis-soft.
Portions Public Domain originating with Sun Microsystems

Permission to use, copy, modify, and distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

## Parhelion-derived weapon code

`src/Solforge.Weapons` (the `.weapon.json` recipe schema, its validation rules and the namespace and
hash rules) is a C# rewrite of the weapon-recipe parts of Parhelion, from the Sundial workspace by
KyleThmpsn and contributors (https://github.com/KyleThmpsn/sundial, `crates/parhelion`, version 0.5.2, checked at
0bda0c5). Parhelion is licensed GPL-3.0-only. Field names, rules and error wording follow
`crates/parhelion/src/recipe.rs` and `crates/parhelion/src/weapon/validation.rs`; the module map is in
`docs/PARHELION_AUDIT.md`.

GPL-3.0-only note: a program that includes code derived from GPL-3.0-only code may only be distributed
as a whole under GPL-3.0-only, with its complete source. Solforge's license is not changed by this
commit. Until the license question (upstream Sunrise Studio has no license; see Provenance) is
settled, `Solforge.Weapons` must not be linked into a released `Solforge.exe`. The full license text
is in the Sundial repository's `LICENSE` file (GNU General Public License, version 3).

## Repository components outside the exe

These are part of the Solforge repository but not of `Sunrise Studio.exe`.

| component | version | license | where | notes |
|---|---|---|---|---|
| Sunrise Blender add-on (stanuwu/SunriseBlenderPlugin) | 1.13.19, upstream e0b5bc6 | GPL-3.0 | `blender/` | Full text in `blender/LICENSE`. A separate Blender add-on that exchanges files with Studio; no add-on code is compiled into the exe. Changes to `blender/` stay GPL-3.0 and must keep that file. |
| Parhelion (KyleThmpsn/sundial, crates/parhelion) | 0.5.2, checked at 0bda0c5 | GPL-3.0-only | `src/Solforge.Weapons` (C# rewrite, see below) | Its weapon-recipe domain is rewritten in C# in `src/Solforge.Weapons`; no Rust source is copied. Its dependencies (tiger-pkg 0.21, egui 0.31, image 0.25) are not used. |

## Provenance

Sunrise Studio upstream (stanuwu/SunriseStudio at 007dd7a) has no license file, and GitHub reports
no license for it. Its README calls it free and open source but grants no written license. Until
upstream adds one, Studio's own code is used under the upstream author's terms, not under any
license listed here.

No game data is in this repository. Packages, package keys, the Oodle library and other files from
a game installation are read from the user's installation at runtime; `.gitignore` refuses them.
