# Overview

## Problem

Many .NET apps want third parties, other teams or the users themselves to add features without rebuilding the app. Doing that well needs several separate pieces:

1. **Delivery:** finding, downloading, verifying and versioning plugin binaries.
2. **Dependencies:** plugins have their own dependencies, which can overlap or conflict with the host's and with each other's.
3. **Loading:** getting plugin code into the process without breaking type identity between host and plugin, while still letting plugins bring their own dependency versions.
4. **Lifecycle:** install, update, uninstall, enable/disable, and doing these while the app runs where possible.
5. **Extension discovery:** letting the host find what plugins contribute, including contributions that arrive after startup.

NuGet already solves delivery, versioning and dependency description, and every .NET developer knows how to make a NuGet package. `AssemblyLoadContext` already solves loading code in isolation. What's missing is a library that connects the two: from a package on a feed to extensions in a running app.

## Goals

- **NuGet-native.** Plugins are NuGet packages. Compatibility is expressed with ordinary NuGet version ranges on the host's contracts package. Any NuGet v3 feed or local folder works.
- **Works in any kind of app.** Console, desktop and web. WPF is the first target, and plugins can contribute whole windows and controls.
- **Lightweight and opinion-light.** The core depends only on the NuGet client libraries and `Microsoft.Extensions.Logging.Abstractions`. Everything else is an add-on.
- **Predictable.** Startup reads local state and doesn't need the network. Changes that can't safely happen while running are staged for the next startup. Failures are reported, never hidden or "fixed" automatically.
- **Hot install.** A newly installed plugin becomes available without a restart whenever that's safe.
- **Grouped isolation.** Cells let plugins be fully separate (the default) or deliberately share a load context.
- **Good experience for plugin authors.** One SDK package reference turns a class library into a correct plugin package.

## Non-goals

- **Security sandboxing.** Modern .NET can't sandbox code in the same process. Plugins run with the same permissions as the host. The library verifies *what* gets installed (feeds, signatures, hashes); it doesn't limit what installed code can do.
- **Unloading code at runtime.** Updates, uninstalls and disables take effect at restart. Collectible load contexts are not used. See [ADR 0005](adr/0005-hot-install-restart-for-the-rest.md).
- **.NET Framework or older .NET versions.** The target is .NET 10 and later.
- **Deciding who may manage plugins.** The library provides operations; the app decides who can use them.
- **UI conventions.** No menus, docking or ribbons. The app defines those as its own extension points.
- **Choosing a DI container.** The core uses none; add-ons integrate Microsoft DI and Autofac.
- **Picking install locations.** The app always supplies the plugin root.

## Audiences

| Who | What they do | What they use |
|---|---|---|
| **App developer** | Builds the host app, defines extension points and the contracts package, decides policy | `Alcove`, add-ons |
| **Plugin developer** | Builds plugins against the host's contracts | `Alcove.Abstractions`, `Alcove.Sdk` |
| **Operator / user** | Installs and manages plugins through whatever UI or tooling the app provides | The app's UI, later `Alcove.Tool` |

## Scope: v1 vs. later

**v1**
- Core: feeds and authentication, search, install/update/uninstall/restore, rollback, validation, cells, loading, catalog, trust policy, configuration and state files.
- `Alcove.Abstractions`, `Alcove.Sdk`.
- `Alcove.DependencyInjection`, `Alcove.Autofac`.
- `Alcove.Wpf` add-on (built in Phase 7; see the [implementation plan](implementation-plan.md#phase-7-wpf-add-on--wpf-runtime-scenarios-verified-by-the-windows-ci-job-once-pushed) and [architecture §11](architecture.md#11-wpf-add-on-alcovewpf)).

**Later**
- `Alcove.Wpf.Manager` (plugin manager UI).
- `Alcove.Tool` (command-line tool).
- `Alcove.Testing`.
- `Alcove.Templates`.
- Optional plugin dependencies.
