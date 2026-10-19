# 0008. DI-agnostic core; Microsoft DI and Autofac add-ons

**Status:** Accepted

## Context
The library should be opinion-light. Many apps use Microsoft DI, some use Autofac, some use none.

## Decision
- The core depends on no DI library. It uses `System.IServiceProvider` and a replaceable `IExtensionActivator`.
- `Alcove.DependencyInjection` and `Alcove.Autofac` are separate add-ons. Each provides host-side registration (`AddPlugins`), an activator based on the container, and plugin-side service registration (`IServicePlugin`).
- One service provider per **cell**, falling back to the host's provider. Providers are disposed in reverse load order.
- Other containers can be adapted through the activator and `IServiceProvider`; no other adapters are shipped.

## Consequences
- Apps without DI pay nothing.
- Two adapters to maintain, which keeps the extension seam honest.
