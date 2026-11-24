# AssetStudio
[![Build status](https://ci.appveyor.com/api/projects/status/rnu7l90422pdewx4?svg=true)](https://ci.appveyor.com/project/Perfare/assetstudio/branch/master/artifacts)

**None of the repo, the tool, nor the repo owner is affiliated with, or sponsored or authorized by, Unity Technologies or its affiliates.**

AssetStudio is a tool for exploring, extracting and exporting assets and assetbundles.

## Features
* Support version:
  * 3.4 - 2022.1
* Support asset types:
  * **Texture2D** : convert to png, tga, jpeg, bmp
  * **Sprite** : crop Texture2D to png, tga, jpeg, bmp
  * **AudioClip** : mp3, ogg, wav, m4a, fsb. support convert FSB file to WAV(PCM)
  * **Font** : ttf, otf
  * **Mesh** : obj
  * **TextAsset**
  * **Shader**
  * **MovieTexture**
  * **VideoClip**
  * **MonoBehaviour** : json
  * **Animator** : export to FBX file with bound AnimationClip

## Requirements

- AssetStudio.net472
   - [.NET Framework 4.7.2](https://dotnet.microsoft.com/download/dotnet-framework/net472)
- AssetStudio.net5
   - [.NET Desktop Runtime 5.0](https://dotnet.microsoft.com/download/dotnet/5.0)
- AssetStudio.net6
   - [.NET Desktop Runtime 6.0](https://dotnet.microsoft.com/download/dotnet/6.0)

## iOS app (in progress)

`AssetStudio.Mobile` is the MAUI front end in the original AssetStudio solution and reuses its Unity asset parser. It can import multiple files, Unity bundles, WebData, and ZIP files; search parsed assets; unpack bundles into a shareable ZIP; and export all, selected, or filtered assets as converted files, raw data, or dumps. The mobile toolbar exposes the desktop-style **File**, **Options**, **Model**, **Export**, **Filter Type**, and **Debug** menus as touch-sized, horizontally scrollable buttons; submenu choices use native mobile action sheets. On iOS, the file picker waits for the current menu sheet to finish dismissing, and files dragged from Files or another app are loaded using an advertised provider representation, copied into the app's library, and then cleaned up. Multi-asset exports are ZIP files containing uniquely named entries and an `AssetStudio_Export_Report.json` manifest; the app reports when an unsupported conversion is preserved as raw asset data. Asset-list and class-structure XML exports are also available. Individual Mesh assets can be viewed as an interactive wireframe and supported textures can be previewed. Exports and extracted bundles are handed to the native iOS share sheet, where they can be saved to Files or shared with another app.

The iPhone app supports portrait and landscape. On narrow screens the desktop-style browser and preview switch to full-height **Assets** and **Preview** tabs, and asset rows collapse into compact summaries; wider screens retain the side-by-side workspace.

This is not yet feature parity with the Windows GUI:

- The mobile exporter decodes DXT1, DXT3, and DXT5 textures in managed code and includes platform-native decoder runtimes for Android, iOS, Mac Catalyst, and Windows. This adds the desktop decoder's PVRTC, ETC/EAC, ATC, ASTC, BC4/5/6H/7, and Crunch support to mobile exports. The converter also handles Unity's ARGBFloat, BGR24, and RGBFloat formats. The upstream iOS native package has only been verified by its publisher in the simulator, so physical-device decoding still needs device testing.
- PCM audio can be exported as WAV. Other audio is exported in its stored format, with MP3, Vorbis, and AAC retaining their corresponding file extensions. Native iOS, Mac Catalyst, and Android playback use the platform audio players and remain limited to codecs those players accept; the desktop FMOD-based decoder/transcoder is Windows-only.
- The mobile app builds a scene hierarchy and supports static checked-scene OBJ archives with hierarchy transforms, serialized skin and blend-shape weights baked into vertices, vertex colors, MTL ambient/diffuse/emissive/specular/opacity/shininess properties, and base/normal/specular/emission PNG maps with texture scale and offset; individual mesh OBJ exports still omit materials and skinning. Scene OBJ and iOS static FBX bake resolved skin and blend-shape poses but do not preserve an editable rig or animation. Scene FBX exports transforms, vertex colors, and supported material colors/PBR values, but not texture maps; individual mesh FBX has no renderer material context. Animator plus AnimationClip export remains raw data rather than animated FBX.
- MonoBehaviour JSON and text dumps use serialized type trees when present. If a MonoBehaviour has no type tree, choose **Options → Load script assemblies** and select the DLLs from the game's `Managed` folder. Mono.Cecil then rebuilds a Unity-serializable field tree for preview and export. Missing script types or unsupported field schemas are reported; converted exports preserve the original bytes as a raw `.dat` fallback.

Build the iOS simulator target on Windows with:

```shell
dotnet build AssetStudio.Mobile/AssetStudioMobile.csproj -f net10.0-ios
```

This builds the iOS simulator target; it does not produce an installable iPhone IPA. A physical-device build, IPA signing, and App Store distribution require macOS with Xcode and Apple signing credentials. On Windows, use Visual Studio's Pair to Mac workflow to build and sign for a device.

The [GitHub Actions workflow](.github/workflows/build-ios.yml) builds the `net10.0-ios` app for `ios-arm64` on macOS and uploads an unsigned IPA artifact. It packages the app without signing credentials; the IPA is not installable on an iPhone until it is signed. Device installation requires an Apple Development certificate and a provisioning profile that includes the device (or appropriate distribution signing for TestFlight/App Store), matching the app's bundle identifier `com.assetstudio.mobile`.

## Usage

### Load Assets/AssetBundles

Use **File-Load file** or **File-Load folder**.

When AssetStudio loads AssetBundles, it decompresses and reads it directly in memory, which may cause a large amount of memory to be used. You can use **File-Extract file** or **File-Extract folder** to extract AssetBundles to another folder, and then read.

### Extract/Decompress AssetBundles

Use **File-Extract file** or **File-Extract folder**.

### Export Assets

use **Export** menu.

### Export Model

Export model from "Scene Hierarchy" using the **Model** menu.

Export Animator from "Asset List" using the **Export** menu.

#### With AnimationClip

Select model from "Scene Hierarchy" then select the AnimationClip from "Asset List", using **Model-Export selected objects with AnimationClip** to export.

Export Animator will export bound AnimationClip or use **Ctrl** to select Animator and AnimationClip from "Asset List", using **Export-Export Animator with selected AnimationClip** to export.

### Export MonoBehaviour

When you select an asset of the MonoBehaviour type for the first time, AssetStudio will ask you the directory where the assembly is located, please select the directory where the assembly is located, such as the `Managed` folder.

#### For Il2Cpp

First, use my another program [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) to generate dummy dll, then when using AssetStudio to select the assembly directory, select the dummy dll folder.

## Build

* Visual Studio 2022 or newer
* **AssetStudioFBXNative** uses [FBX SDK 2020.2.1](https://www.autodesk.com/developer-network/platform-technologies/fbx-sdk-2020-2-1), before building, you need to install the FBX SDK and modify the project file, change include directory and library directory to point to the FBX SDK directory

## Open source libraries used

### Texture2DDecoder
* [Ishotihadus/mikunyan](https://github.com/Ishotihadus/mikunyan)
* [BinomialLLC/crunch](https://github.com/BinomialLLC/crunch)
* [Unity-Technologies/crunch](https://github.com/Unity-Technologies/crunch/tree/unity)
