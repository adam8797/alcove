# Trusted root bundles

`codesignctl.pem` (code-signing roots) and `timestampctl.pem` (timestamping roots) are copied unchanged from the
.NET SDK 10.0.203: `$(NetCoreRoot)sdk/10.0.203/trustedroots/`. NuGet only finds them next to `NuGet.Packaging.dll`,
so the `Alcove` package copies them to every consuming app's output (`buildTransitive/Alcove.targets`); see ADR 0010.

To refresh them (with each SDK or NuGet update), copy both files from the newest SDK's `trustedroots/` folder over these
and update the SDK version above, e.g. `cp "$(dirname "$(which dotnet)")"/sdk/<version>/trustedroots/*.pem src/Alcove/trustedroots/`.
