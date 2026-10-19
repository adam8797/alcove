# 0009. Separate, stable Abstractions package

**Status:** Accepted

## Context
Apps and plugins upgrade independently. A plugin built against library v1 must keep working in a host using v1.x, and ideally later versions.

## Decision
Plugin-facing types (`IPlugin`, `IPluginContext`, `IExtensionRegistrar`, `ExtensionAttribute`) go in `Alcove.Abstractions`. It's host-owned, backward compatible within a major version, and kept as small as possible. The manifest records its schema version. (`IServicePlugin` lives in the DI add-ons, since it references container types.)

## Consequences
- The core can change freely without breaking plugins.
- The Abstractions package depends only on `NuGet.Versioning` (see Amendments), so `IPluginContext.Version` is a `NuGetVersion`. The core already depends on the NuGet libraries, so this adds nothing new; `NuGet.Versioning` is treated as host-owned.

## Amendments
- *2026-10-17 (spike: nuget-resolution-and-assets):* `IPluginContext.Logger` (`ILogger`) means Abstractions also depends on `Microsoft.Extensions.Logging.Abstractions`, which must then be host-owned. Plugins needing a newer version than the host has are incompatible. *Decided 2026-10-18:* accepted and documented. `Alcove.Abstractions` references the **lowest supported** `Microsoft.Extensions.Logging.Abstractions` version, so plugins don't raise the minimum by accident, and the host's version caps what plugins can use. A logging interface of our own was rejected: plugins logging through the host still need shared logging types, so it would only move the problem.
