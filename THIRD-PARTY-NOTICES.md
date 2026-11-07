# Third-party notices

Contain is distributed under its own [license](LICENSE). The modern desktop uses
the components below. Their licenses and required notices are retained under
`licenses/` and must accompany the Windows self-contained distribution.

| Component | Version / provenance | License and notices | Use |
| --- | --- | --- | --- |
| WPF UI (`WPF-UI`) | 4.3.0; upstream commit `153baadeaf475875a81a4605881ca5c2dbe53972` | [MIT](licenses/WPF-UI-LICENSE.txt), as included in the NuGet package | Third-party themes, navigation, window/title bar, buttons, dialogs and controls |
| WPF UI Abstractions (`WPF-UI.Abstractions`) | 4.3.0; same upstream commit | [MIT](licenses/WPF-UI.Abstractions-LICENSE.txt), as included in the NuGet package | Transitive navigation/control contracts |
| .NET Community Toolkit (`CommunityToolkit.Mvvm`) | 8.4.2; upstream commit `35dfe7b12da0e64384bfaec7ebc0883196a89f9f` | [MIT](licenses/CommunityToolkit.Mvvm-LICENSE.txt) | Observable view models and commands |
| Fluent System Icons | Font resources embedded in WPF UI 4.3.0 (`FluentSystemIcons-Regular.ttf`, `FluentSystemIcons-Filled.ttf`) | [MIT, Microsoft Corporation](licenses/FluentSystemIcons-LICENSE.txt) | Fluent-style `SymbolIcon` glyphs; no separate proprietary system font redistribution |
| .NET runtime | 10.0.14; `dotnet/runtime` tag `v10.0.14` | [MIT](licenses/Microsoft.NETCore.App-LICENSE.txt), [third-party notices](licenses/Microsoft.NETCore.App-THIRD-PARTY-NOTICES.txt) | Self-contained managed runtime and framework libraries |
| Windows Desktop / WPF runtime | 10.0.14; `dotnet/wpf` tag `v10.0.14` | [MIT](licenses/Microsoft.WindowsDesktop.App-LICENSE.txt), [third-party notices](licenses/Microsoft.WindowsDesktop.App-THIRD-PARTY-NOTICES.txt) | Windows Presentation Foundation |

WPF UI is an independent open-source project maintained by lepo.co / Leszek
Pomianowski and WPF UI contributors. It is **not a Microsoft official UI product**.
The component library supplies general desktop presentation and interaction;
Contain implements installation-record workflows, exact-session selection,
evidence presentation, queries and exports.

The .NET 10 target selects WPF UI's `net10.0-windows7.0` assets, whose sole package
dependency is WPF UI Abstractions 4.3.0 (which has no further package dependencies).
CommunityToolkit.Mvvm 8.4.2's compatible .NET 8 assets have no additional package
dependencies. Framework/runtime redistribution notices are separate from this
NuGet dependency graph. Locked restore and the final package manifest must verify
the actual resolved graph; changes require updating these notices.

The WPF UI license files preserve the copyright years supplied in the actual
4.3.0 packages, even though package metadata has a later copyright range. These
upstream notices have not been silently rewritten.

The UI uses installed system text fonts with Chinese fallback. Contain does not
redistribute Segoe UI, Segoe Fluent Icons, commercial UI assets or Gallery branding.
The MIT Fluent System Icons font resources already embedded by WPF UI are distinct
from the restricted Segoe Fluent Icons font.

The UI SDK is pinned to 10.0.403. The SDK is a build prerequisite, not bundled as
an application dependency. The self-contained runtime is pinned to 10.0.14; the
packaging process must retain notices supplied by the actual runtime packs as
well as the upstream notices listed here. No font or runtime license is replaced
by Contain's own license.

The reused Rust `contain.exe` remains version `0.3.2-beta.10`, source
`b0d2510db34cdf6630d2d550b69ccdbfe6da3fd4`. Its Cargo lockfile, existing dependency
policy and capture evidence remain separate from the new UI dependencies. This
desktop migration does not claim to relicense the core or its dependencies.

## Sources checked for this migration

- [WPF UI 4.3.0](https://github.com/lepoco/wpfui/releases/tag/4.3.0), including
  `LICENSE.md` in the WPF-UI and WPF-UI.Abstractions NuGet 4.3.0 packages.
- [WPF UI resource declarations](https://github.com/lepoco/wpfui/blob/4.3.0/src/Wpf.Ui/Wpf.Ui.csproj)
  and [font explanation](https://github.com/lepoco/wpfui/blob/4.3.0/README.md#segoe-fluent-icons).
- [Fluent System Icons MIT license](https://github.com/microsoft/fluentui-system-icons/blob/06f559c29c761ca979894d07a14b3a44c94a24d1/LICENSE).
- [.NET Community Toolkit 8.4.2 license](https://github.com/CommunityToolkit/dotnet/blob/v8.4.2/License.md).
- [.NET runtime 10.0.14 license and notices](https://github.com/dotnet/runtime/tree/v10.0.14).
- [WPF runtime 10.0.14 license and notices](https://github.com/dotnet/wpf/tree/v10.0.14).

These are redistribution notices, not a claim that the new application has
already passed native Windows acceptance. Build and acceptance identities belong
to the final package's accompanying reports.
