---
title: Field Notes on Typography
subtitle: A longer sample for testing conversion, reading and themes
author: AllInOne PDF
date: 2026-11-05
keywords: sample, typography, markdown
---

# Field Notes on Typography

Typography is the craft of arranging type so that written language is legible, readable and appealing when displayed. Good typography disappears: the reader notices the ideas, not the letters. This document exercises every block the converter understands so you can judge a theme at a glance.

## 1. Measure, leading, rhythm

The **measure** is the length of a line of text. Between 45 and 75 characters is comfortable; 66 is often quoted as ideal. Lines that are too long tire the eye on the return sweep; lines that are too short break the rhythm of reading.

**Leading** (line height) gives the eye a track to follow. Body text usually wants 1.4–1.8× the font size. Larger measures need more leading.

> Typography exists to honor content.
> — Robert Bringhurst, *The Elements of Typographic Style*

### 1.1 A checklist

- [x] Choose a measure of 60–75 characters
- [x] Set leading between 1.5 and 1.7
- [ ] Decide on justified or ragged-right text
- [ ] Pick a paragraph style: spacing *or* indent, never both

### 1.2 Ordered steps

1. Pick the body face first; everything else follows from it.
2. Set the body size for the medium (10–11 pt print, 18–20 px screen).
3. Derive the heading scale (a ratio such as 1.25 or 1.333).
4. Tune spacing until the page breathes.
   1. Space above headings should exceed space below.
   2. Keep lists tight unless items are long.

## 2. Code and tables

Inline code like `font-feature-settings: "liga" 1` sits in the text. Blocks get their own treatment:

```css
.article {
  font-family: "Literata", Georgia, serif;
  font-size: 1.1875rem;
  line-height: 1.7;
  max-width: 68ch;
}
```

```python
def modular_scale(base: float, ratio: float, steps: int) -> list[float]:
    """Return a modular scale starting at base."""
    return [round(base * ratio ** i, 2) for i in range(steps)]

print(modular_scale(16, 1.25, 6))
```

| Ratio   | Name            | Scale from 16 px              |
|:--------|:----------------|------------------------------:|
| 1.125   | Major second    | 16 · 18 · 20.3 · 22.8 · 25.6  |
| 1.25    | Major third     | 16 · 20 · 25 · 31.3 · 39.1    |
| 1.333   | Perfect fourth  | 16 · 21.3 · 28.4 · 37.9 · 50.5|
| 1.5     | Perfect fifth   | 16 · 24 · 36 · 54 · 81        |
| 1.618   | Golden ratio    | 16 · 25.9 · 41.9 · 67.8 · 110 |

## 3. Callouts, notes and breaks

> [!NOTE]
> Front matter at the top of this file sets the title, subtitle, author and date used on the cover page.

> [!TIP]
> Duplicate the *Manuscript* theme and switch the body font to Inter to see how much the face alone changes the feel.

> [!IMPORTANT]
> Headings should never be orphaned at the bottom of a page. The renderer keeps them with the following paragraph.

> [!WARNING]
> Very long unbroken strings such as URLs can overflow narrow columns: https://example.com/a/very/long/path/that/keeps/going/and/going/until/it/wraps

> [!CAUTION]
> Mixing more than two type families on one page rarely ends well.

---

Footnotes work too.[^1] So do ~~strikethrough~~, ==highlight==, H~2~O and x^2^.

[^1]: Footnotes are collected at the end of the document and numbered in order of appearance.

## 4. Mathematics

Inline math such as $E = mc^2$ renders as monospace until a math renderer exists. Display math:

$$
\int_0^1 x^2 \, dx = \frac{1}{3}
$$

## 5. The closing paragraph

If a theme survives this document — the dense table, the long URL, the nested lists, the five callouts — it will survive most real-world files. Open the Theme builder, pick a preset and start adjusting.
