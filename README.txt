AssetStudio unified patch for the uploaded studio-main.zip baseline

Apply from the repository root with:
  git apply AssetStudio-current-issues.patch

Included fixes:
1. Removes the misleading "Loader not connected" gate from saved-library loading.
   MainPage now invokes its concrete AssetStudioMobileLoader directly.
2. Awaits the main-thread asset-list update before reporting 100% load completion.

Important findings from the uploaded source:
- The landscape GUI already has the 38/62 browser/preview split and the File/Options/Model/Export/Filter Type/Debug menus. The portrait layout stacks panes.
- The selected-asset preview is still metadata-only; MainPage explicitly says decoded preview data is not supplied by the host.
- Texture2DDecoderWrapper still P/Invokes a native library, while DllLoader only supports Windows/Linux/macOS. This archive contains no iOS native decoder build/link configuration. A working iOS texture decoder cannot honestly be included as a managed-only patch.
- Folder picker event subscriptions are still missing from MainPage in this baseline; this patch does not claim folder selection is fixed.
- No iOS build or device test has been run.

This is one consistent patch against the exact uploaded archive, not a bundle of older patches. It deliberately does not claim to fix unimplemented texture decoding or folder picking.
