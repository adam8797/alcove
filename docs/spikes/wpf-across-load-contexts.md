# Spike: WPF across load contexts

## Goal
Confirm that WPF plugins work when loaded into a non-default `AssemblyLoadContext`, and characterise the duplicate-assembly-name problem the architecture warns about (§4).

> **Constraint:** WPF only runs on Windows. This spike is *written and compiled* on Linux (`EnableWindowsTargeting=true`) and must be *run* on Windows. The report says clearly what was built, what was verified, and gives a step-by-step checklist for the Windows run.

## Questions
1. A plugin assembly in a compartment containing a `UserControl` with XAML (compiled BAML): can the host create and show it? (BAML loading of `InitializeComponent`.)
2. `pack://application:,,,/PluginA;component/Themes/Plugin.xaml`: can the host load a plugin's `ResourceDictionary` by URI and merge it into `Application.Resources`? Does `Application.LoadComponent` work?
3. Plugin XAML referencing types in its own assembly (`xmlns:local="clr-namespace:...;assembly=PluginA"`), in a host-owned assembly (`HostSdk`, with a shared `UserControl` and styles), and in another plugin's assembly.
4. Implicit `DataTemplate`s (`DataType="{x:Type vm:SomeViewModel}"`) defined in a plugin dictionary: do they apply to view-models created by the plugin?
5. **Duplicate names:** two compartments each loading a *different version* of the same XAML-containing library `SharedControls` (v1 and v2, visibly different). Do both plugins show their own version, or does WPF resolve `pack://`/BAML/`xmlns` to the wrong one? Try with and without version in the pack URI (`;v2.0.0.0;component/`).
6. `DynamicResource` to app-level resources (keys defined in `HostSdk`) from plugin XAML.
7. What does WPF use internally to map an assembly name to an `Assembly` (e.g. `MS.Internal.WindowsBase`/`ReflectionHelper`/`AppDomain.GetAssemblies` scanning)? Read the WPF source (github.com/dotnet/wpf) to predict the result of question 5 where it can't be run here.

## Experiments
- `spike/wpf-across-load-contexts/`: `HostSdk` (host-owned library), `WpfHost` app with a minimal compartment loader and buttons per scenario, `PluginA`, `PluginB`, `SharedControls` v1 and v2.
- A scripted mode (`WpfHost.exe --scenario N`) that runs each scenario, renders off-screen if possible, and prints PASS/FAIL, so the Windows run can be quick.
- Static analysis from the WPF source for question 7.

## Success criteria
- Everything compiles on Linux.
- Report with predicted outcomes (from WPF source reading) and a Windows run checklist.
- Recommendations for the WPF add-on and the duplicate-assembly warning rule.
