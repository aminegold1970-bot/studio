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
  * **AnimationClip** : legacy curves and non-legacy streamed, dense, and constant muscle curves to JSON; clips without usable bindings or decoded data retain raw Unity data
  * **Animator** : raw Unity data; pair with selected AnimationClips to export a converted animation package

## Requirements

- AssetStudio.net472
   - [.NET Framework 4.7.2](https://dotnet.microsoft.com/download/dotnet-framework/net472)
- AssetStudio.net5
   - [.NET Desktop Runtime 5.0](https://dotnet.microsoft.com/download/dotnet/5.0)
- AssetStudio.net6
   - [.NET Desktop Runtime 6.0](https://dotnet.microsoft.com/download/dotnet/6.0)

## iOS and MAUI app (functional port; not full Windows feature parity)

`AssetStudio.Mobile` is the MAUI front end in the original AssetStudio solution and reuses its Unity asset parser. It can load multiple files or entire folders, unpack bundles, WebData, and ZIP archives, search parsed assets, and export all, selected, or filtered assets as converted files, raw data, or dumps. Native folder pickers are implemented for iOS, Mac Catalyst, Android, and Windows; selected folders are copied into persistent app storage before parsing. File and folder extraction produces a shareable ZIP. The mobile toolbar exposes the desktop-style **File**, **Options**, **Model**, **Export**, **Filter Type**, and **Debug** menus as touch-sized, horizontally scrollable buttons with compact, scrollable popup menus. **Options → Export options** uses the desktop control layout for grouped exports, converted texture/sprite format (JPEG, PNG, BMP, or TGA), whether converted Texture2D exports are decoded images or original `.tex` data, how multi-asset ZIP entries are grouped (by type, container path, source filename, or flat), whether supported PCM audio is converted to WAV, and whether TextAsset exports restore their container extension. Errors are written to `Documents/Errors/<source file name>.txt`, so each log is named for the bundle or file that produced it; older `Documents/AssetStudio` logs are migrated and removed on launch. On iOS, the file picker waits for the current menu to finish dismissing, and files or folders dragged from Files or another app are loaded using an advertised provider representation, copied into the app cache, and then cleaned up. Multi-asset exports are ZIP files containing uniquely named entries and an `AssetStudio_Export_Report.json` manifest; the app reports when an unsupported conversion is preserved as raw asset data. Asset-list XML export is also available; **Debug → Export class structures** writes one text file per class, grouped by Unity version, inside a ZIP. Individual Mesh assets and checked scene objects have interactive 3D previews with solid-shaded and wireframe modes; solid shading uses renderer base colors and vertex colors. The scene view applies hierarchy transforms and the serialized skin/blend-shape pose, but does not sample textures or play animations. Supported texture and sprite previews have a full-screen viewer with zoom and pan. VideoClip selections show parsed resolution, frame timing, stream, and audio metadata and include an embedded player for formats supported by the platform. Other assets display serialized type-tree data when available, or their parsed metadata when no type tree exists. Exports and extracted bundles are handed to the platform share sheet, where they can be saved or shared with another app.

On iPhone, a folder dragged from the Files app onto the main window is imported and loaded just like the desktop **File → Load folder** command. Dropping a folder onto **Options → Export options** sets the export destination: iOS stores a security-scoped bookmark and writes exports directly into that folder. If no export folder has been dropped, exports continue to use `Documents/AssetStudio/Exports` so they remain visible in Files.

When audio conversion is enabled, embedded Unity PCM and Vorbis clips are exported as WAV; Vorbis is decoded to 16-bit PCM. MP3 and AAC remain in their original encoded formats for native playback. Other codecs are preserved as original data and are not decoded by the mobile player.

The **Display all assets**, **Enable preview**, and **Display asset information** options retain their state across launches and show their current state in the Options menu. Disabling previews stops preview generation and playback while keeping the serialized Dump view available.

VideoClip preview shows parsed metadata and includes an embedded player for streams supported by the device's media codecs. On iOS, selecting a clip with embedded video data also generates a thumbnail from the stream, using the native duration when available and the parsed frame count/rate as a fallback. Thumbnail generation is best-effort and never blocks playback or export. Playback controls and format support are platform-dependent. VideoClip export preserves the original path extension when available and otherwise detects common MP4/QuickTime, Ogg, WebM/Matroska, AVI, or MPEG containers. Unrecognized payloads retain the `.video` extension.

The **Filter Type** menu matches the Windows GUI: a drop-down with **All** and one checkable item per exportable type. Tapping an item applies the filter immediately while leaving the menu open. Selecting an object in **Scene Hierarchy** shows its subtree mesh count and offers a direct wireframe preview of that subtree. Hierarchy checkboxes propagate to descendants, and search/Enter cycles matches.

The iPhone app supports portrait and landscape. Portrait uses one navigation strip for **Hierarchy**, **Assets**, **Classes**, **Preview**, and **Dump**; the preview starts hidden so the browser uses the full screen. Landscape opens with the desktop-style split workspace and a visible preview pane. Narrow layouts hide the inner pane tab bars to avoid duplicate navigation, and asset rows collapse into compact summaries. The top menu shows a right-pointing arrow when more commands are available by scrolling.

This is not yet feature parity with the Windows GUI:

- The mobile exporter decodes DXT1, DXT3, and DXT5 textures in managed code and includes platform-native decoder runtimes for Android, iOS, Mac Catalyst, and Windows. Windows explicitly resolves the architecture-matched native decoder from its NuGet runtime asset directory. The texture decoder has routes for all 70 `TextureFormat` values declared by the parser, including PVRTC, ETC/EAC, ATC, ASTC, BC4/5/6H/7, and Crunch. Texture reads reject truncated payloads, Crunch unpacking receives the exact serialized payload size and releases native output buffers on failure, float/HDR channels are clamped to the 8-bit image range, and odd-width YUY2 textures decode their final pixel. Sprite atlases are resized before cropping to match the desktop export path. The upstream iOS native package has only been verified by its publisher in the simulator; physical-device decoding and validation against real Crunch texture fixtures remain outstanding. ImageSharp is pinned to 3.1.12 to avoid the newer license-key build gate. NuGet reports advisories for older TIFF/ICC paths; this exporter only resizes raw pixels and writes JPEG/BMP/TGA, and does not load encoded images or use TIFF APIs. Keep NuGet audit warnings visible and revisit the package if those APIs are added; the package's Split License terms still apply.
- Audio export uses managed decoders instead of the Windows-only FMOD runtime: WAV/AIFF/MP3/AAC containers pass through with their native extension, Ogg Vorbis is decoded with NVorbis, FSB5 streams support PCM8/16/24/32, float, IMA ADPCM, Nintendo DSP, Sony VAG, and embedded MPEG frames, and raw Unity 5+ ADPCM/VAG clips are decoded to WAV. MP3, AAC, and other platform codecs still rely on the device media player for playback; ATRAC9, XMA, BCWAV, and other codecs with no managed decoder remain exportable in their stored format. Embedded fonts render with the local CoreText stack on iOS and Mac Catalyst; other platforms show the font metadata and offer export.
- The mobile Model menu exports all loaded mesh assets as split or merged OBJ archives, resolving renderer materials and exporting supported MTL color properties and base/normal/specular/emission texture maps when that mesh has a renderer in the loaded scene. These all-mesh exports use the mesh's local geometry; checked-scene OBJ archives additionally apply hierarchy transforms and bake serialized skin and blend-shape weights into vertices. Scene OBJ exports also include vertex colors and preserve supported texture scale and offset. Unity submesh base-vertex offsets are applied during triangle decoding. A direct converted export of one Mesh asset remains geometry-only. The iOS-only static FBX export includes available UVs, normals, vertex colors, renderer material colors/PBR values, and scene transforms for checked objects; it bakes the current skin/blend-shape pose rather than preserving an editable rig or animation, and does not embed texture maps. The **Animator + selected AnimationClips** command exports legacy curves and non-legacy streamed/dense/constant curve data as readable `.anim.json` entries, including numeric Unity binding metadata and resolved transform paths when available from the selected Animator hierarchy or Avatar. Unmatched hashes remain present, and Animator assets or clips without usable decoded bindings are preserved as raw `.dat` entries in the ZIP report. This is an animation-data export, not an animated FBX export.
- MonoBehaviour JSON and text dumps use serialized type trees when present. If a MonoBehaviour has no type tree, select it and choose **Load script assemblies** in its preview, then select the DLLs from the game's `Managed` folder. Mono.Cecil then rebuilds a Unity-serializable field tree for preview and export. Missing script types or unsupported field schemas are reported; converted exports preserve the original bytes as a raw `.dat` fallback.
- Unity 6 (6000.x) shaders are loaded and listed by name, but the changed serialized shader layout is not parsed. Converted shader export writes a placeholder note; raw and type-tree dump exports remain available. This avoids object-stream desync and the shader parsing errors reported for Unity 6 asset bundles.

Build the iOS simulator target on Windows with:

```shell
dotnet build AssetStudio.Mobile/AssetStudioMobile.csproj -f net10.0-ios
```

This builds the iOS simulator target; it does not produce an installable iPhone IPA. A physical-device build, IPA signing, and App Store distribution require macOS with Xcode and Apple signing credentials. On Windows, use Visual Studio's Pair to Mac workflow to build and sign for a device.

The [GitHub Actions workflow](../.github/workflows/build-ios.yml) builds the `net10.0-ios` app for `ios-arm64` on macOS and uploads an unsigned IPA artifact. Run the workflow manually to also publish the `.ipa` as a direct GitHub Release asset (GitHub Actions artifacts are always downloaded as ZIPs). Push and pull-request builds keep the normal Actions artifact. The IPA is packaged without signing credentials and is not installable on an iPhone until it is signed. Device installation requires an Apple Development certificate and a provisioning profile that includes the device (or appropriate distribution signing for TestFlight/App Store), matching the app's bundle identifier `com.assetstudio.mobile`.

## Usage

### Load Assets/AssetBundles

Use **File → Load file** (multi-select, like the desktop OpenFileDialog) or **Load folder**. On iPhone, selected documents and folders are copied off the UI thread, with progress and cancellation available during the copy. Folder imports preserve their directory layout in the app cache so the parser can resolve external Unity assets. Loading reports progress through file import, object decoding, relationship linking, and browser construction, while the status line identifies the current file or stage. Cancellation is checked during file/folder enumeration, split-file and archive copies, decompression streams, bundle/WebData traversal, serialized metadata parsing, object parsing boundaries, and browser construction. A decoder, a single compressed LZMA block, or the Unity object currently being decoded is not interruptible mid-operation; cancellation takes effect at the next checkpoint.

When AssetStudio loads AssetBundles, it decompresses and reads them directly in memory, which may use substantial memory. **File → Extract file** and **Extract folder** unpack supported contents into a ZIP and open the platform share sheet; save the ZIP to Files or another location before loading its contents.

### Extract/Decompress AssetBundles

Use **File → Extract file** or **Extract folder**. The app creates a ZIP archive and shares it through the platform share sheet.

### Export Assets

use **Export** menu.

Mobile exports are always saved to `Documents/AssetStudio/Exports` so they appear in the Files app, matching the desktop behavior of writing to a known folder. When **Export options → Open folder after export** is enabled, the share sheet also opens for the exported file; otherwise the status bar reports the saved filename.

### Export Model

Export model from "Scene Hierarchy" using the **Model** menu.

Select exactly one Animator and one or more AnimationClips in **Asset List**, then choose **Export → Animator + selected AnimationClips**. Legacy curves and decoded non-legacy streamed, dense, constant, object-reference, and event data are exported as `.anim.json`. Muscle bindings retain their Unity path hashes and include resolved transform paths when available from the Animator hierarchy or Avatar; unmatched hashes remain unresolved. The Animator asset and clips whose curve stream or bindings cannot be decoded are preserved as raw `.dat` entries. The ZIP report records raw fallbacks. This exports animation data, not a rigged or animated FBX.

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
