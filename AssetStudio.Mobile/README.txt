AssetStudio Mobile GUI — Stage 37 / v27
======================================

HISTORICAL NOTE
---------------
The Stage 37/38 limitations below describe an earlier source snapshot. The current
AssetStudio.Mobile project includes its MAUI project, AssetStudio parser reference,
and connected mobile loader. For current build instructions and iOS limitations,
see the repository README.md.

iOS Files drag-and-drop + persistent error log.

WHAT THIS VERSION ADDS
----------------------
1. iOS native UIDropInteraction target attached to the MAUI page's native UIView.
2. Accepts file-provider drops from Files.app and copies the temporary provider URL
   immediately, before iOS revokes access to that temporary URL.
3. Every file received through drag-and-drop is copied into the app cache so the
   temporary provider URL can be released immediately.
4. Files selected through File -> Load file are also copied into that same cache
   before the host loader callback is invoked.
5. Errors shown by the app are written to:
      <App Sandbox>/Documents/AssetStudio/Errors/error-log.txt
   The Debug -> Show error message option controls popups and is remembered across
   launches; logging happens even when the popups are turned off.
6. The source cache uses unique stored filenames, so files with identical original
   names do not overwrite each other.
7. Existing Info.plist in the supplied unsigned IPA already has:
      UIFileSharingEnabled = true
      LSSupportsOpeningDocumentsInPlace = true
   A merge snippet is included at Platforms/iOS/InfoPlist-settings.txt.

IMPORTANT LIMITATIONS — PLEASE READ
-----------------------------------
This is a source-level integration feature, NOT a rebuilt IPA. The supplied material
contains a compiled unsigned IPA and these reconstructed MAUI page files, but does
not include the editable MAUI .csproj, App.xaml/App.xaml.cs, full iOS project, or the
host's AssetStudio loader subscription. No .NET SDK/Xcode build environment is
available here, so this version has NOT been compiled or tested on an iPhone.

The native drag/drop bridge receives files and the app copies them into Documents.
The app then invokes the existing AssetFilesSelected callback with FileResult objects
pointing at the persistent copies. For Unity assets to appear in the Asset List, the
host must subscribe to MainPage.AssetFilesSelected and call the real AssetStudio core
loader (AssetsManager.LoadFiles + BuildAssetStructures or the appropriate mobile
adapter), then call MainPage.SetAssets(...), SetHierarchy(...), etc.
If that callback is not connected, files are safely saved but cannot yet be parsed or
shown as Unity objects. This is not hidden or simulated by the UI.

The Errors folder contains only text logs of errors shown to the user. It does not
serialize individual Unity sub-assets. Source files are kept in the app cache only for
the duration of the current import and can be removed by the OS.

FILES
-----
MainPage.xaml                         Updated toolbar and drop hint.
MainPage.xaml.cs                     Persistent import, error popups, drop dispatch.
AssetDocumentLibrary.cs              Copies, hashes, and deduplicates source files into the app cache.
AssetErrorLog.cs                     Writes shown errors to Documents/AssetStudio/Errors/error-log.txt.
Platforms/iOS/AssetStudioDropBridge.cs Native UIDropInteraction implementation.
Platforms/iOS/InfoPlist-settings.txt  Info.plist keys to preserve/merge.

INTEGRATION NOTES
-----------------
- Include AssetDocumentLibrary.cs and AssetErrorLog.cs in the MAUI project.
- Include Platforms/iOS/AssetStudioDropBridge.cs in the iOS target. It is guarded by
  #if IOS; it uses UIKit UIDropInteraction and NSItemProvider.LoadFileRepresentation.
- The MainPage.OnHandlerChanged override attaches the drop interaction to the page's
  native UIView on iOS.
- Keep UIFileSharingEnabled and LSSupportsOpeningDocumentsInPlace enabled.
- Ensure the host subscribes to AssetFilesSelected before the user drops or selects files.
- The iOS Files app can browse the app's Documents directory through On My iPhone when
  file sharing is enabled. The error log folder is Documents/AssetStudio/Errors.

SECURITY / STORAGE
------------------
- Files are copied into the app cache; the original Files.app item is not modified.
- Error logs are kept in Documents and survive app restarts; users can view or delete
  them through Files.app under On My iPhone -> AssetStudio -> Errors (if exposed by iOS).
- Large bundles consume device storage. The app does not yet impose a quota.

VALIDATION PERFORMED
--------------------
- MainPage.xaml parsed as XML.
- InfoPlist-settings.txt manually checked against the existing IPA's Info.plist;
  both required file-sharing keys are already present in that IPA.
- Static source checks only. No C# compiler, MAUI build, signing, or device test ran.

PROGRESS
--------
GUI reconstruction estimate: approximately 70% (rough estimate; library management UI was added).
File workflow source implementation: added, but host parser hookup and device testing
remain unverified. Do not treat this as a working rebuilt IPA.


STAGE 38 — ORIGINAL REPOSITORY PORT AUDIT
----------------------------------------
Added PORT_FILE_AUDIT.md. It compares the original 243-file source tree with the current v27 mobile source. Key finding: none of the original projects are currently compiled/referenced by the mobile source folder; the parser and utility code must be integrated. The report classifies core parser files to reuse, utilities to adapt, and Windows Forms/Windows native projects not to use unchanged.
