# Design QA — blocked

## Comparison target

- Source visual truth: user-supplied 1920 × 1080 Xbox streaming mockups, including `C:\Users\lukeg\Downloads\ChatGPT Image Oct 16, 2026, 01_44_49 AM-2.png`.
- Implementation: UWP `StremioXboxPrototype` Debug x64 build.
- Intended state: dark desktop/TV shell with the Discover/Home navigation rail selected.

## Evidence and blocker

The revised UWP project compiles and packages successfully, but the app is not installed in the current user profile and no native application window is available to capture. Therefore no implementation screenshot exists to normalize against the source at 1920 × 1080, and an evidence-based visual comparison cannot yet be made.

## Implemented design alignment

- Fonts and typography: Segoe UI with a semibold 38px page title, 16px navigation labels, and smaller muted labels.
- Spacing and layout rhythm: 210px navigation rail, 51px utility bar, 29px desktop content gutter, compact 12px surface radii, and poster-card rows.
- Colors and visual tokens: deep navy background/surfaces, violet selected and focus treatment, slate borders, and the reference green/red/yellow controller hints.
- Image quality and asset fidelity: provider-supplied poster and backdrop URLs are retained. No fictional catalogue artwork was added.
- Copy and content: navigation and page labels mirror the reference categories while results remain derived from installed add-ons.

## Findings

- [P1] Runtime visual comparison is unavailable.
  Location: packaged UWP app.
  Evidence: no installed `StremioXbox` app package or targetable native app window was available.
  Impact: screenshot fidelity and focus states cannot be visually verified yet.
  Fix: deploy the Debug x64 MSIX through the `MsixPackage` launch profile, capture the running app at 1920 × 1080, then compare and iterate.

## Implementation checklist

- [x] Apply the reference shell, navigation rail, header, palette, typography, cards, and focus states.
- [x] Preserve real provider-driven content and interactions.
- [x] Compile the Debug x64 package.
- [ ] Capture the packaged app and conduct visual comparison at the reference viewport.

## Follow-up polish

- Add per-screen layouts for Settings, Diagnostics, Server, and Playback Lab after runtime capture confirms the base shell scale.

final result: blocked
