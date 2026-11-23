AssetStudio GUI Reconstruction v4

GUI-only update based on the original AssetStudio Windows Forms source.

Changes from v3:
- Restores clickable sortable headers for Name, Container, Type, PathID and Size.
- PathID and Size sort numerically; text columns sort case-insensitively.
- Sorting is applied after search and type filtering, matching the original list workflow.
- Scene hierarchy rows now support depth-based indentation and checkbox selection.
- Checking/unchecking a hierarchy row cascades to following descendant rows, matching the original GOHierarchy AfterCheck behavior.
- Asset list column headers are hidden when switching to Scene Hierarchy or Asset Classes.

Integration contract:
- Continue using SetAssets(IEnumerable<AssetRow>), SetHierarchy(IEnumerable<HierarchyRow>), and SetClasses(...).
- Populate HierarchyRow.Depth in parent-before-child order for checkbox cascade to work correctly.
- This is a MAUI source patch, not a rebuilt IPA. It does not invent sample asset data.
- Replace the corresponding MainPage.xaml and MainPage.xaml.cs in the existing MAUI project.

Known limitations:
- The hierarchy is represented as an indented flat list rather than a native expandable tree.
- Parent-child check cascade assumes hierarchy rows are supplied in preorder and Depth is accurate.
- Real asset loading, preview rendering and export actions remain host-service integration tasks.
