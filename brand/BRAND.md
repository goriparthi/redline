# RedLine brand kit

RedLine is a macOS usage monitor for Claude, Codex, and Ollama.

## Brand idea

Three model streams converge on one threshold. The result is an abstract **R**: calm telemetry meeting a precise red limit line. Red should signal attention, not danger.

## Messaging

- **Name:** RedLine
- **Category:** AI usage monitor for macOS
- **Primary tagline:** Know your limit.
- **Product descriptor:** Claude, Codex, and Ollama usage at a glance.
- **Short store line:** One quiet place to track every AI limit.
- **Voice:** concise, calm, technical, candid
- **Avoid:** fear language, racing metaphors, claims of exactness when a provider only exposes estimates

Preferred UI terms: **Usage**, **Remaining**, **Resets**, **Pace**, **Projected**, **Limit**.

## Visual system

| Token | Hex | Role |
| --- | --- | --- |
| Carbon | `#0B0D10` | Main background |
| Graphite | `#171A1F` | Elevated surfaces |
| Steel | `#818792` | Secondary text and structure |
| Chalk | `#F4F1EA` | Primary text and light mark |
| Signal | `#FF3B30` | Limit, warning, active brand accent |
| Amber | `#FF9F0A` | Approaching limit |
| Clear | `#32D74B` | Healthy status |

Use Signal sparingly. A typical screen should be 80% neutral, 15% typography/structure, and no more than 5% red.

### Typography

- **Product UI:** SF Pro / system font (`-apple-system`)
- **Metrics:** SF Mono / system monospaced
- **Marketing fallback:** Inter, then system sans-serif
- **Wordmark:** use the supplied outlined SVG; do not recreate it from a font in production artwork

Sentence case is preferred. Use tabular numerals for quota and reset-time displays.

## Included assets

- `AppIcon.appiconset/` — ready for an Xcode asset catalog
- `masters/redline-app-icon-1024.png` — full-size raster master
- `logo/redline-symbol.svg` — flat vector symbol
- `logo/redline-wordmark-dark.svg` and `logo/redline-wordmark-light.svg`
- `logo/redline-lockup-dark.svg` and `logo/redline-lockup-light.svg`
- `menu-bar/RedlineTemplate.svg`, PNG, and `@2x` PNG
- `tokens/redline-tokens.json` — design tokens
- `tokens/RedlineColors.swift` — SwiftUI color constants
- `copy/voice-and-copy.md` — launch-ready copy and voice rules

## Usage rules

Maintain clear space equal to the width of the red threshold stroke around the symbol. Do not recolor the red line, add provider logos inside the mark, rotate the symbol, place it over noisy imagery, or use the dimensional app icon as a tiny menu-bar glyph.

For monochrome contexts, use `RedlineTemplate`; macOS applies the correct menu-bar color. Set `isTemplate = true` when loading it outside an asset catalog.

## Xcode setup

1. Copy `AppIcon.appiconset` into `Assets.xcassets`.
2. Set the app target's App Icon source to `AppIcon`.
3. Add the menu-bar template image and load it by the asset name `RedlineTemplate`.

The dimensional app icon was generated as original artwork for this project. The vector symbol and lockups are production companions derived from the same concept.
