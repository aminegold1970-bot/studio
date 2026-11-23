AssetStudio Mobile GUI — Stage 37 / v27
======================================

HISTORICAL NOTE
---------------
The Stage 37/38 limitations below describe an earlier source snapshot. The current
AssetStudio.Mobile project includes its MAUI project, AssetStudio parser reference,
and connected mobile loader. For current build instructions and iOS limitations,
see the repository README.md.

New feature: iOS Files drag-and-drop + persistent source-file library.

WHAT THIS VERSION ADDS
----------------------
1. iOS native UIDropInteraction target attached to the MAUI page's native UIView.
2. Accepts file-provider drops from Files.app and copies the temporary provider URL
   immediately, before iOS revokes access to that temporary URL.
3. Every file received through drag-and-drop is copied into:
      <App Sandbox>/Documents/AssetStudio/Library/
4. Files selected through File -> Load file are also copied into that same persistent
   library before the host loader callback is invoked.
5. Added a Library toolbar button. It lists saved source files and lets the user
   load a previously saved file without selecting/importing it from Files again.
6. The library uses unique stored filenames, so files with identical original names
   do not overwrite each other.
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

The Library button reloads stored SOURCE FILES. It does not currently serialize every
individual Unity sub-asset into a separate document. That requires the real AssetStudio
exporter to be connected and a defined export policy. The original Unity bundle/file
is preserved so the loader can parse it again without asking the user to re-import it.

FILES
-----
MainPage.xaml                         Updated toolbar and drop hint.
MainPage.xaml.cs                     Persistent import, reload library, drop dispatch.
AssetDocumentLibrary.cs              Copies, hashes, deduplicates, lists, and safely deletes source files.
AssetLibraryPage.cs                   Searchable multi-select library manager with load/delete actions.
Platforms/iOS/AssetStudioDropBridge.cs Native UIDropInteraction implementation.
Platforms/iOS/InfoPlist-settings.txt  Info.plist keys to preserve/merge.

INTEGRATION NOTES
-----------------
- Include AssetDocumentLibrary.cs in the MAUI project.
- Include Platforms/iOS/AssetStudioDropBridge.cs in the iOS target. It is guarded by
  #if IOS; it uses UIKit UIDropInteraction and NSItemProvider.LoadFileRepresentation.
- The MainPage.OnHandlerChanged override attaches the drop interaction to the page's
  native UIView on iOS.
- Keep UIFileSharingEnabled and LSSupportsOpeningDocumentsInPlace enabled.
- Ensure the host subscribes to AssetFilesSelected before the user drops or selects files.
- The iOS Files app can browse the app's Documents directory through On My iPhone when
  file sharing is enabled. The library folder is Documents/AssetStudio/Library.

SECURITY / STORAGE
------------------
- Files are copied into the app sandbox; the original Files.app item is not modified.
- The app can read its saved copies on subsequent launches.
- The library manager now supports confirmed deletion. Users can also manage the library through Files.app
  under On My iPhone -> AssetStudio (if exposed by iOS).
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
