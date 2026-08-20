# Redline provider glyphs

Small, monochrome provider marks for visually distinguishing Codex, Anthropic,
and Ollama inside Redline. A Claude sparkle is included as an optional alternative
when the UI refers to the Claude product rather than the Anthropic provider.

## Xcode

1. Drag `ProviderIcons.xcassets` into the app target and enable **Copy items if needed**.
2. Use `Image("Codex")`, `Image("Anthropic")`, or `Image("Ollama")` in SwiftUI.
3. The images are configured as template vectors, so `.foregroundStyle(...)`
   works in light, dark, and high-contrast appearances.

`ProviderBadge.swift` is a ready-to-copy SwiftUI example.

## Design recommendation

Keep the marks monochrome at 14–16 pt and pair each with its text label. If you
want stronger separation, tint the surrounding Redline-owned chip or status
indicator—not the third-party mark. Do not imply endorsement or use these marks
as Redline's app icon.

## Contents

- `raw/`: portable SVGs using `currentColor`.
- `ProviderIcons.xcassets/`: Xcode asset catalog with vector preservation.
- `ProviderBadge.swift`: compact accessible SwiftUI badge.
- `preview.html`: quick browser preview on light and dark surfaces.

## Sources and notices

- Codex uses the OpenAI blossom glyph from Bootstrap Icons 1.13.1 (MIT). There
  is no separate third-party Codex glyph in this pack; label the blossom “Codex.”
- Anthropic, Claude, and Ollama glyphs are from Simple Icons 16.28.0 (CC0-1.0).
- Brand names and logos remain trademarks of their respective owners. Review
  each owner's current brand rules before public distribution.

See the included license files and `THIRD_PARTY_NOTICES.md`.
