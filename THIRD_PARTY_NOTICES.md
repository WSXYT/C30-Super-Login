# References and third-party notices

## EasiAuto

- Project: https://github.com/hxabcd/EasiAuto
- Reference branch: `master`; fetched using the GitHub API. API tree and selected reference files are retained under `references/` for local review only.
- Upstream license: GPL-3.0. Its Python implementation is not bundled. The unmodified project icon is included solely to identify the related-project link, alongside a PNG rendering, original ICO source, source manifest and license text. Artwork retains its upstream terms.
- Reviewed areas: README, page/config organization, account-to-subject bindings, ClassIsland integration, and IPC.
- This project independently implements its Avalonia pages and native C30 transport. It references workflow concepts and public ClassIsland identifiers/JSON schema, not EasiAuto's runtime implementation or themes. Icon: `resources/icons/EasiAuto.ico`, Git blob `62072f3b8b9f168bf854eed5a5a7817729d99dad`.

## ClassIsland

- Project: https://github.com/ClassIsland/ClassIsland
- Website: https://classisland.tech
- Interoperability uses public automation action/rule identifiers and exported JSON configuration; no ClassIsland binary or source is included in the release.
- JSON schema compatibility is locally tested; actual ClassIsland timed execution remains to be tested on a configured installation.
- ClassIsland (GPL-3.0) is a design reference for native Avalonia / FluentAvalonia navigation and settings organization; its themes and screenshots are not copied. The unmodified related-link icon is included, with a PNG rendering, original ICO source, source manifest and license. Source: `ClassIsland.Desktop/Assets/AppLogo.ico`, Git blob `1240ade6c93e539cb1f0040ac36b4b53f4fe319a`. Artwork retains its upstream terms; project marks do not imply endorsement.

Original icon files and provenance are under `src/C30SuperLogin.Desktop/Assets/`; the release includes their source copies and `PROJECT-ICONS.json` under `licenses/project-icons/`. GPL license texts are included under `licenses/`. The helper's own C-shaped portal SVG is newly drawn, not derived from either project logo.

## UI and rendering dependencies

- Avalonia 11.3.22 and FluentAvaloniaUI 2.5.1: MIT. Projects: https://github.com/AvaloniaUI/Avalonia and https://github.com/amwx/FluentAvalonia .
- FluentAvalonia also resolves Avalonia ColorPicker/DataGrid 11.3.12; MicroCom.Runtime 0.11.0 is a transitive dependency. The release uses FluentAvalonia controls and independently written layout, not ClassIsland styles.
- SkiaSharp 2.88.9 and HarfBuzzSharp 8.3.1.1 supply rendering/shaping; their package license and third-party notices are retained under `licenses/` when packaging.
- Avalonia.Fonts.Inter embeds Inter; font license is SIL Open Font License 1.1. System fonts are requested by name and are not bundled by this project.
- Package metadata and available upstream license texts are included under `licenses/`; exact shipped files are listed in `SHA256SUMS.txt`.

## Velopack

- Official .NET SDK and vpk CLI: 1.2.161, MIT. https://velopack.io/ and https://github.com/velopack/velopack .
- Early bootstrap, update checking/downloading and packaging follow the official C# guide. Startup auto-apply is explicitly disabled; applying updates is user initiated and gated against active login/warning tasks.
- The self-contained installer includes Microsoft .NET runtime redistribution components. Velopack's generated package checksums are not a publisher code signature; this local preview is unsigned.

## PostHog / Sentry / Microsoft

- Services: https://posthog.com / https://sentry.io
- Uses HTTP capture/envelope protocols. No third-party telemetry SDK is included; service accounts, regions, retention and access rules are configured by the deployer.
- Desktop pages use Avalonia / FluentAvalonia, not WPF. The core UI Automation bridge uses Windows Desktop framework assemblies; the framework-dependent package still requires an independently installed .NET 10 Desktop Runtime. Windows DPAPI provides local credential protection. Windows Forms is used only by the owned control-test fixture, not bundled in the release as an application.
- Official Sentry SDK integration and production service configuration are pending; this preview does not claim production telemetry validation.

## C30

C30 and related names belong to their respective owners. This is an independent helper, not a vendor release. Installed binaries remain unchanged. Local analysis snapshots and transaction copies are not part of the distribution ZIP.
