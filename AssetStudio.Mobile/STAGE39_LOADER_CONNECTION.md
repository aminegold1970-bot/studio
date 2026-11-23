# Stage 39 — AssetStudio loader connection

## Changes
- Added `AssetStudioMobileLoader`, which receives the persisted `FileResult` items and passes their full paths to the original `AssetStudio.AssetsManager.LoadFiles()` parser.
- Converts each parsed `AssetStudio.Object` into the existing mobile `MainPage.AssetRow`, retaining the original object reference for later preview/export operations.
- Uses the Unity object's name when available, otherwise its Unity class/type name; includes source file, class ID, PathID and byte size.
- Runs the synchronous parser on a worker thread and reports progress around parsing.
- Subscribes the loader to `MainPage.AssetFilesSelected` during page initialization.
- Makes file-import and parser errors visible even when optional error-message display is disabled, so failures are not silent.

## Verification status
Source wiring was added and reviewed against the APIs in this repository (`AssetsManager.LoadFiles`, `assetsFileList`, `SerializedFile.Objects`, `Object.type`, `Object.m_PathID`, and `Object.byteSize`). This environment does not have the .NET SDK / iOS workload, so no compile, IPA build, or on-device parsing test is claimed. The original parser may log and skip malformed/unsupported files internally; a real Unity file should be used to verify the resulting asset count on device.

## Remaining
- Add folder loading to the same service path.
- Connect preview/export actions to the retained `SourceAsset` references and implement platform-compatible decoders where required.
- Add cancellation within the original synchronous parser; the current cancellation checks run before and after `LoadFiles()`.
