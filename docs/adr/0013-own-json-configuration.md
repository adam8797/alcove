# 0013. The app's options object is the configuration

**Status:** Accepted

## Context
Operators need to change feeds, cells and trust settings without recompiling. Depending on `Microsoft.Extensions.Configuration` would add an opinion and a dependency. Merging code settings with a file needs precedence rules, which are a source of surprises (e.g. can a file weaken a trust policy set in code?).

## Decision
- `PluginHostOptions` is a plain object, and **whatever the app passes to `PluginHost.Create` is final**. The library never reads a configuration file on its own and never merges sources.
- The library provides `PluginHostOptions.Load(path)` / `Save(path)` for a JSON file format (System.Text.Json). The app can load the file as-is, load it and modify it, or build options entirely in code.
- The library-owned `state.json` is separate from configuration.
- Binding from `IConfiguration` may come later as an add-on.

## Consequences
- No precedence rules to document or get wrong.
- Whether operators can change a given setting (including trust policy) is the app's decision.
- No configuration-framework dependency in the core.
