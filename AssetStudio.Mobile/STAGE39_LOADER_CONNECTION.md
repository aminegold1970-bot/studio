# Mobile loader and folder operations

## Changes
- Connects file and folder loading directly to the original `AssetStudio.AssetsManager`.
- Builds mobile asset rows, scene hierarchy, and class-tree rows from parsed Unity data.
- Adds native folder selection on iOS, Mac Catalyst, Android, and Windows. Selected folders are copied into persistent app storage with their directory layout preserved before loading.
- Implements folder extraction by scanning nested files and sharing a ZIP of supported Unity bundle, WebData, and ZIP contents.
- Uses the built-in mobile exporter and share sheet directly; removes unused host-service callbacks that could leave menu actions unwired.
- Preserves the last loaded folder so changing the Unity version override can reload it.

## Verification
The Android build was attempted, but the installed .NET SDK lacks the required `maui-android` workload. Restoring it was denied by the environment for insufficient permissions. No successful compile, iOS device run, or folder-provider runtime test is claimed.

## Remaining desktop feature differences
- The port is not full Windows GUI feature parity. Audio decoding/transcoding beyond native player codecs, editable model rigs and animation export, and some platform-specific conversions remain limited as described in `README.md`.
- The original synchronous parser can only be cancelled before and after `AssetsManager.LoadFiles` or `LoadFolder`; cancellation inside the parser remains upstream work.
