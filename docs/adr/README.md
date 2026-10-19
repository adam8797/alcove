# Architecture decision records

Each record is short: context, decision, consequences. Status is one of *Accepted*, *Proposed*, *Superseded by NNNN*.

| # | Decision | Status |
|---|---|---|
| [0001](0001-target-dotnet-10.md) | Target .NET 10 and later only | Accepted |
| [0002](0002-nuget-packages-packagetype-and-manifest.md) | Plugins are NuGet packages identified by PackageType + manifest | Accepted |
| [0003](0003-cells.md) | Cells are the unit of isolation | Accepted |
| [0004](0004-dependency-sharing-classes.md) | Host-owned / host-shared / cell-private dependencies | Accepted |
| [0005](0005-hot-install-restart-for-the-rest.md) | Hot install; everything else at restart; no unloading | Accepted |
| [0006](0006-validation-always-resolution-optional.md) | Validation always runs; resolution is optional | Accepted |
| [0007](0007-extension-catalog.md) | Extensions are obtained from an ExtensionCatalog | Accepted |
| [0008](0008-di-agnostic-core-with-addons.md) | DI-agnostic core; Microsoft DI and Autofac add-ons | Accepted |
| [0009](0009-separate-abstractions-package.md) | Separate, stable Abstractions package | Accepted |
| [0010](0010-trust-model.md) | Full trust; optional policy; hashes always pinned | Accepted |
| [0011](0011-report-failures-never-self-heal.md) | Report failures; never disable or roll back automatically | Accepted |
| [0012](0012-app-supplies-root-no-global-state.md) | App supplies the plugin root; no global state | Accepted |
| [0013](0013-own-json-configuration.md) | The app's options object is the configuration | Accepted |
| [0014](0014-cross-cell-dependencies.md) | Cross-cell plugin dependencies via exposed dependencies | Accepted |
| [0015](0015-share-identical-wpf-assemblies.md) | Share identical WPF-relevant private assemblies across cells | Accepted |
| [0016](0016-resolution-with-dependency-walker.md) | Resolve with RemoteDependencyWalker and host pins | Accepted |

Records with an **Amendments** section: 0003, 0004, 0005, 0009, 0010, 0011, 0012, 0016.
