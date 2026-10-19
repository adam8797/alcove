# Spikes

Throwaway proof-of-concept work that answers a specific question before we commit to a design. Each spike:

- is described here (goal, questions, experiments, success criteria);
- is carried out in `spike/<name>/` at the repo root (throwaway code, never shipped);
- ends with `spike/<name>/REPORT.md`: answers, evidence (commands + output), recommendations, and effects on the [implementation plan](../implementation-plan.md) and ADRs.

| Spike | Status | Report |
|---|---|---|
| [cross-compartment-deps](cross-compartment-deps.md) | Done (Linux) | [REPORT](../../spike/cross-compartment-deps/REPORT.md) |
| [hot-load-into-loaded-compartment](hot-load-into-loaded-compartment.md) | Done (Linux), 30/30 checks | [REPORT](../../spike/hot-load-into-loaded-compartment/REPORT.md) |
| [nuget-resolution-and-assets](nuget-resolution-and-assets.md) | Done (Linux) | [REPORT](../../spike/nuget-resolution-and-assets/REPORT.md) |
| [nuget-signature-verification](nuget-signature-verification.md) | Done (Linux); Windows run wanted | [REPORT](../../spike/nuget-signature-verification/REPORT.md) |
| [wpf-across-load-contexts](wpf-across-load-contexts.md) | Built on Linux; **Windows run needed** | [REPORT](../../spike/wpf-across-load-contexts/REPORT.md) |

Note: the spike agents' environment didn't allow them to write `REPORT.md` files, so each report was saved by the orchestrating session from the agent's returned findings. Each report says so at the top. For nuget-resolution-and-assets, the report was put together from the agent's summary plus the evidence logs, because the full draft couldn't be returned.

> **Naming:** the spikes ran before the project was named. In spike docs and reports, **compartment** means **cell** and `<Name>` means **Alcove**. Spike folder names (e.g. `cross-compartment-deps`) are kept as they are.

## Shared rules for all spikes

- .NET 10 (`net10.0`, or `net10.0-windows` for WPF). The development machine is Linux.
- Simulate packages with real `dotnet pack` output in a local folder feed inside the spike directory when packages are needed.
- Prefer small console "host" apps with explicit `Console.WriteLine` evidence over test frameworks; whatever is fastest to make the result unambiguous.
- Report negative results as clearly as positive ones. "It doesn't work, here's why" is a successful spike.
