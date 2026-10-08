# Third-party notices

My Avatar works with the projects below. It does not ship them: each is installed through the VRChat Creator Companion (VPM) or Unity's package manager, under its own license. The thumbnail fonts at the end ship with My Avatar, under the SIL Open Font License.

## Adjerry91’s Face Tracking Templates
Face tracking blendshapes are animated by Adjerry91’s Face Tracking Templates. My Avatar downloads them through VPM and sets them up when you choose Add in the Face tracking section; it does not ship or modify them. Products utilizing this project must include visible credit on their store or product page: “Face tracking blendshapes are animated by Adjerry91’s Face Tracking Templates” with a link to https://github.com/Adjerry91/VRCFaceTracking-Templates. The package also includes its own license agreement (License folder, credit required).
https://github.com/Adjerry91/VRCFaceTracking-Templates
```
MIT License

Copyright (c) 2023 Adjerry91

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

## VRCFaceTracking
The face tracking test reproduces what VRCFaceTracking sends to VRChat, so testing in Unity matches the game: its Unified Expressions, correctors, "v2" parameters and binary parameter encoding (VRCFaceTracking.Core), and the mappings of the community iPhone modules it uses (Live Link: kusomaigo/VRCFaceTracking-LiveLink; iFacialMocap: Shuisho10/VRC_iFacialMocap), rewritten from their published behaviour. My Avatar does not ship VRCFaceTracking or the modules; you install VRCFaceTracking yourself to use face tracking in VRChat.
https://github.com/benaclejames/VRCFaceTracking
```
Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the
License. You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an
"AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific
language governing permissions and limitations under the License.
```

## VRCFury
Installs accessories with Armature Link, merges their toggles and animations, and the face tracking template, at build time. Downloaded from VRCFury's own listing (vcc.vrcfury.com), never shipped with My Avatar.
https://vrcfury.com
```
License Options

VRCFury (c) 2026 Senky

For the purposes of this document, a "commercial purpose" is one primarily intended for commercial advantage or monetary compensation (including, but not limited to, one-time payments, subscription payments, and donations). A "personal purpose" is any purpose aside from those defined as a "commercial purpose."

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

You may choose to use any of the following licenses:

Personal License

VRCFury may be used, copied, modified, merged, published, distributed, or sublicensed for personal purposes if all of the following restrictions are met:
* This full copyright and license notice is included in any derivitive works or distributions.

Commercial License

VRCFury may be used for personal or commercial purposes if all of the following restrictions are met:
* VRCFury must be downloaded directly by the end-user from an archive distributed on https://vcc.vrcfury.com
* VRCFury must not be redistributed with your product
* The package may be downloaded by an interactive guided process and extracted from a compressed archive, but the source files must be left unmodified.

VRCA / VRCW License

VRCFury may distributed for personal or commercial purposes if all of the following restrictions are met:
* The distribution is made as part of an "uploaded VRChat avatar asset bundle," or an "uploaded VRChat world asset bundle" hosted on VRChat asset servers.
* This license is only here to permit the use of the SPS shader, which is linked and packaged into avatars / worlds uploaded using SPS. This means the SPS shader CANNOT be used for commercial purposes at runtime outside of VRChat avatar / world packages.

FAQ
(These FAQ are for reference only and are not a part of the license above)

Why isn't VRCFury fully "open source"?

For a project to officially be "FOSS" (Free and Open Source Software), its use must be unrestricted and unencumbered,
essentially meaning that it can be used by any person or company for any purpose (sometimes with limited restrictions).

We are not using a FOSS license because:

1. We want to prevent malicious avatar creators from modifying VRCFury and introducing it as an "exclusive" paid part of their avatar
   products. Not only would this be... evil, it also would cause these versions of VRCFury to diverge, preventing components
   made using one version from working on the other version.
2. We want to prevent corporations from taking VRCFury and making it a part of their game without asking. VRCFury includes
   a lot of novel unity logic which would be valuable to a VR or Desktop game company, and if they "took" all of our work
   and made it a built-in part of their game, the development priorities likely would shift toward monetization (as most
   companies do), rather than what's best for the users of VRCFury.


Can I use VRCFury for an avatar that I sell?

Yes! As long as you do not distribute VRCFury itself along with your avatar package, you are still totally in
compliance with the license. Simply instruct your users to download VRCFury from VRCFury website.

Why not use GPL / AGPL?

GPL can discourage commercial use by forcing commercial users to open-source their own applications using such a library.
However, this adds a lot of complexity in our case, as neither Unity nor the VRCSDK are GPL-compatible. Even with special exceptions for these,
we also need to allow custom non-GPL avatar scripts to interact with the VRCFury API, as well as other integrations. Without a way to give separate
permissions specifically for commercial use, there's not a good way to allow these exceptions while still achieving our
goals above.
```

## Modular Avatar
Accessories made for Modular Avatar keep their Merge Armature and menu setup. Downloaded through VPM when you add one.
https://modular-avatar.nadena.dev
```
Files under Editor/images are licensed for redistribution as part of an official
modular avatar package only. Please replace them with other images (or delete them)
if you are making modifications. If you're interested in using the Modular Avatar
logo as part of your own asset's advertising, please refer to
https://modular-avatar.nadena.dev/docs/distributing-prefabs/logo-usage .

All other files are under the MIT license.

---

MIT License

Copyright (c) 2022 bd_

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

## d4rkAvatarOptimizer
Optional optimizer added from the Tools section. My Avatar is not associated with d4rkAvatarOptimizer.
https://github.com/d4rkc0d3r/d4rkAvatarOptimizer
```
MIT License

Copyright (c) 2021 d4rkpl4y3r

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

## Gesture Manager
Tests gestures, the expression menu and animations in Play Mode, added from the Tools section.
https://github.com/BlackStartx/VRC-Gesture-Manager
```
MIT License

Copyright © 2019-2023 BlackStartx

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

## AudioLink
Tests audio-reactive materials in the editor, added from the Tools section. My Avatar is not associated with AudioLink.
https://github.com/llealloo/audiolink
```
Copyright 2024 llealloo, cnlohr, lox9973, pema99, float3

Permission is hereby granted, free of charge, to any person obtaining a copy of this software 
and associated documentation files (the "Software"), to deal in the Software without restriction, 
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, 
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial 
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT 
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. 
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, 
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE 
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

You may freely use this code in any CC 3.0+ licensed projects.

"Sludge Bath" provided by Lamp DX under Creative Commons: BY-NC-ND 4.0
"Shibuya" provided by Rollthered under Creative Commons: BY-NC 4.0
```

## MCP for Unity
Optional: My Avatar's tools for AI assistants run through it when it is installed.
https://github.com/CoplayDev/unity-mcp
```
MIT License

Copyright (c) 2025 CoplayDev

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

## Newtonsoft.Json
JSON for the asset gallery and the account, through Unity's com.unity.nuget.newtonsoft-json package.
https://github.com/JamesNK/Newtonsoft.Json
```
The MIT License (MIT)

Copyright (c) 2007 James Newton-King

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Thumbnail fonts
The fonts for the line of text on a thumbnail, shipped in My Avatar's Fonts folder with their license files, under the SIL Open Font License 1.1. They are used as they are, unmodified.
https://openfontlicense.org
- Bonbon: Copyright (c) 2011, Cyreal (www.cyreal.org), with Reserved Font Name "Bonbon".
- Creepster: Copyright (c) 2011, Font Diner, Inc (diner@fontdiner.com), with Reserved Font Names "Creepster".
- Diplomata: Copyright 2011 The Diplomata Project Authors (https://github.com/etunni/diplomata), with Reserved Font Name "Diplomata".
- JetBrains Mono: Copyright 2020 The JetBrains Mono Project Authors (https://github.com/JetBrains/JetBrainsMono).
- Matemasie: Copyright 2024 The Matemasie-Font Project Authors (https://github.com/YADAMSS/Matemasie-Font).
- Monoton: Copyright (c) 2011 by vernon adams (vern@newtypography.co.uk), with Reserved Font Names "Monoton".
- Pirata One: Copyright (c) 2012, Rodrigo Fuenzalida, Nicolas Massi (www.taip.com.ar / abc.taip.com.ar), with Reserved Font Name 'Pirata'.
- Plaster: Copyright (c) 2011 by Sorkin Type Co (www.sorkintype.com), with Reserved Font Name "Plaster".
```
This Font Software is licensed under the SIL Open Font License, Version 1.1.
This license is copied below, and is also available with a FAQ at:
https://openfontlicense.org


-----------------------------------------------------------
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
-----------------------------------------------------------

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font creation
efforts of academic and linguistic communities, and to provide a free and
open framework in which fonts may be shared and improved in partnership
with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded, 
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply
to any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may
include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software components as
distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting,
or substituting -- in part or in whole -- any of the components of the
Original Version, by changing formats or by porting the Font Software to a
new environment.

"Author" refers to any designer, engineer, programmer, technical
writer or other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining
a copy of the Font Software, to use, study, copy, merge, embed, modify,
redistribute, and sell modified and unmodified copies of the Font
Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components,
in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be
included either as stand-alone text files, human-readable headers or
in the appropriate machine-readable metadata fields within text or
binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the corresponding
Copyright Holder. This restriction only applies to the primary font name as
presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any
Modified Version, except to acknowledge the contribution(s) of the
Copyright Holder(s) and the Author(s) or with their explicit written
permission.

5) The Font Software, modified or unmodified, in part or in whole,
must be distributed entirely under this license, and must not be
distributed under any other license. The requirement for fonts to
remain under this license does not apply to any document created
using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are
not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM
OTHER DEALINGS IN THE FONT SOFTWARE.
```
