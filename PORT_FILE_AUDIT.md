# AssetStudio repository usage audit for the iOS/MAUI port

Audit basis: original `AssetStudio-master-original.zip` extracted at `/mnt/data/as_original/AssetStudio-master`, compared with the actual v27 mobile source folder.

## Current status

This report describes the earlier v27 reconstruction and is historical, not an audit of the current source tree. The current MAUI project references the `AssetStudio` parser and invokes `AssetStudioMobileLoader` for both file and folder loading. Native folder pickers, persistent folder import, and folder extraction are implemented in the mobile project. See `AssetStudio.Mobile/AssetStudioMobile.csproj`, `AssetStudio.Mobile/AssetStudioMobileLoader.cs`, and `AssetStudio.Mobile/MobileFolderPicker.cs`.

## Historical finding from v27

**None of the original repository projects are currently compiled into the v27 mobile source.** The v27 folder contains reconstructed MAUI page files and iOS drop support, but it has no `.csproj`, `.sln`, project references, or direct `using AssetStudio...` integration. `MainPage.AssetRow.SourceObject` is only an opaque `object` reference. Therefore, the categories below describe what should be reused, adapted, or excluded for the port—not files already integrated.

The original repository contains 243 files across the projects listed below (file counts from the extracted source tree).

## Reuse as the core parser (highest priority)

### `AssetStudio/` — 111 files

This is the main platform-neutral parsing/data project. It should be the foundation of the iOS port. Important entry points include:

- `AssetStudio/AssetsManager.cs` — load files, bundles and serialized files.
- `AssetStudio/FileReader.cs`, `EndianBinaryReader.cs`, `ObjectReader.cs` — binary reading.
- `AssetStudio/BundleFile.cs`, `WebFile.cs`, `SerializedFile.cs`, `StreamFile.cs` — container and serialized-file parsing.
- `AssetStudio/SerializedType.cs`, `TypeTree.cs`, `TypeTreeHelper.cs`, `TypeTreeNode.cs` — type-tree handling.
- `AssetStudio/ObjectInfo.cs`, `Object.cs` and `Classes/*.cs` — Unity object models.
- `AssetStudio/Classes/` — textures, audio, meshes, materials, shaders, sprites, animation, MonoBehaviour, GameObject, transforms, etc.
- `AssetStudio/Brotli/`, `AssetStudio/7zip/` and `AssetStudio/BigArrayPool.cs` — compression/support code used by parsing.

**Recommendation:** port/reference this project first and make its target framework compatible with the MAUI app. Do not rewrite the parser in the UI layer.

## Reuse selectively, after platform adaptation

### `AssetStudioUtility/` — 41 files

Useful conversion/export helpers exist here, but the project currently references platform-sensitive dependencies and wrappers. Review individually:

- Likely useful: `Texture2DConverter.cs`, `Texture2DExtensions.cs`, `ImageExtensions.cs`, `ImageFormat.cs`, `SpriteHelper.cs`, `AudioClipConverter.cs`, `MonoBehaviourConverter.cs`, `ShaderConverter.cs`, `SpirVShaderConverter.cs`, `ModelConverter.cs`, `ModelExporter.cs`, `TypeDefinitionConverter.cs`, `SerializedTypeHelper.cs`, `Unity.CecilTools/*`, `Unity.SerializationLogic/*`, `CSspv/*`, `Smolv/*`.
- Must be adapted/validated: `AssemblyLoader.cs`, `MyAssemblyResolver.cs`, and any converter using Windows paths, native libraries, unsupported ImageSharp APIs, or desktop assumptions.
- `AssetStudioUtility.csproj` currently references `AssetStudio.PInvoke`, `AssetStudioFBXWrapper`, `AssetStudio`, and `Texture2DDecoderWrapper`; those references cannot simply be copied unchanged into iOS.
- The FMOD binding files under `AssetStudioUtility/FMOD Studio API/` are not a complete iOS audio solution by themselves. The native FMOD runtime and platform-specific licensing/binaries would also be required. Prefer AVFoundation/MAUI audio playback for the mobile preview unless FMOD-specific decoding is essential.

## Do not use unchanged in the iOS port

### `AssetStudioGUI/` — 26 files

This is the original Windows Forms application. Its controls, designer files, WinForms tree/list items, dialogs, OpenTK WinForms GLControl and Windows-specific UI resources are not mobile source. Recreate the behavior in MAUI instead. In particular, do not bring these into the iOS build:

- `AssetStudioGUI/AssetStudioGUIForm.cs` and `AssetStudioGUI/AssetStudioGUIForm.Designer.cs`
- `AssetStudioGUI/Studio.cs` and `AssetStudioGUI/Exporter.cs` as-is: they are tightly coupled to `AssetItem`, WinForms controls, and desktop dialogs; extract/refactor their engine operations into platform-neutral services.
- `AssetStudioGUI/Components/AssetItem.cs`, `GOHierarchy.cs`, `GameObjectTreeNode.cs`, `TypeTreeItem.cs`, `OpenFolderDialog.cs` — replace UI wrappers with MAUI view models/data rows.
- `AssetStudioGUI/DirectBitmap.cs`, `Libraries/OpenTK.WinForms.dll`, `Properties/Settings.*`, `Properties/Resources.*`, `*.resx`, `Resources/as.ico`, `Resources/preview.png` — desktop-specific implementation/resources.
- `Program.cs`, `GUILogger.cs`, `ExportOptions.*` — reimplement startup, logging, and options in the mobile app.

### `AssetStudioFBXNative/` — 19 files
### `AssetStudioFBXWrapper/` — 7 files

Do not include the Windows-built FBX DLLs or assume the wrapper works on iOS. The C++ FBX implementation is native/platform/toolchain-specific. If FBX export is required, it needs a supported iOS build of the native dependency and a compatible P/Invoke layer, or a different export route. This is not needed for initial loading, browsing, texture preview, or ordinary asset extraction.

## Native texture decoder: source may be reusable, binaries are not

### `Texture2DDecoderNative/` — 32 files
### `Texture2DDecoderWrapper/` — 4 files

The decoder algorithms (ASTC, ATC, BCn, ETC, PVRTC, Crunch and related code) are potentially valuable for texture preview/export. However, the `.vcxproj`, Windows `.rc`/DLL build setup, and Windows DLL binaries are not usable on iOS unchanged. The C/C++ source must be built as an iOS static library/framework for the correct architectures, and the C# wrapper must use an iOS-compatible native binding strategy.

## Build/repository files not directly part of the mobile app

- `AssetStudio.sln` — original multi-project solution; it is not a MAUI solution and includes Windows-native projects.
- `.github/workflows/build.yml` — original repository CI workflow; not the iOS app build pipeline.
- `.gitattributes`, `.gitignore`, `README.md`, `LICENSE` — repository metadata/documentation; keep license notices and attribution, but they are not runtime source files.
- `AssetStudio.PInvoke/` — 3 files; not automatically required for the parser itself. Keep only if a selected native decoder/export component still needs its interop helpers, and adapt its native library loading for iOS.

## What is actually in the current mobile source

The v27 mobile folder contains: `MainPage.xaml`, `MainPage.xaml.cs`, `AssetDocumentLibrary.cs`, `AssetLibraryPage.cs`, `Platforms/iOS/AssetStudioDropBridge.cs`, and an Info.plist settings note. It does not contain the original parser project, utility project, a MAUI `.csproj`, or an app-level host that subscribes to the loader/export events. The UI currently emits callbacks; no original `AssetsManager.LoadFiles()` call is wired in this folder.

## Recommended port order

1. Add the original `AssetStudio` core project (or a clean mobile-targeted copy) to a real MAUI solution and compile it for the iOS target.
2. Create a platform-neutral `AssetStudioService` around `AssetsManager`, `LoadFiles`, and asset-structure construction; connect it to `MainPage.AssetFilesSelected` and folder loading.
3. Populate `AssetRow` with actual `AssetStudio.Object` references and real metadata.
4. Implement generic dump and TextAsset preview first; then texture decoding using the decoder source built for iOS.
5. Add platform-native audio preview and image display.
6. Refactor raw/dump/selected/all export operations out of WinForms `Studio.cs` / `Exporter.cs`.
7. Treat FBX/model rendering and shader conversion as separate later work because they have native/graphics dependencies.

## Verification limits

This is a static source-tree audit. It confirms the project references and source contents described above. No MAUI build or iPhone runtime test has been performed. The audit does not claim any original parser file is already integrated.
